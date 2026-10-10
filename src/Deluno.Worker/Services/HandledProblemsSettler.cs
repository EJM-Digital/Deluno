using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Closes the problems whose film or episode has its file now (#1167).
///
/// <para>A download that finished but was not imported, or an import that failed, stays in Needs action, the
/// bell and Activity until something settles it. The settles that exist match a case to its download by the
/// details the case carries, and the cases and failures older builds wrote carry none, so a title that was
/// imported another way, or relinked by hand, kept its alarms for ever. Here the title is the key: when it
/// holds the file, the open download and import cases about it close, and so does a download's own failed
/// import. One Activity line says so for each.</para>
///
/// <para>Only a file that arrived after the problem counts, so an older copy does not hide a failed upgrade; a
/// file whose arrival was never dated (a relink) counts. Cases about the file itself (quality, corrupt, a
/// processor's fault) are left alone: some file being there does not answer them.</para>
/// </summary>
public sealed class HandledProblemsSettler(
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    IJobQueueRepository jobs,
    IDownloadDispatchesRepository? dispatches,
    IActivityFeedRepository activityFeed,
    Deluno.Platform.Data.IProcessorRepository? processors = null,
    IProcessorConnectionService? processorConnections = null,
    TimeProvider? clock = null,
    IHealthCheckRepository? healthChecks = null,
    IRealtimeEventPublisher? realtime = null)
{
    private const int DispatchWindow = 500;

    public async Task<int> SettleAsync(CancellationToken cancellationToken)
    {
        var settled = 0;
        foreach (var (mediaType, cases) in new[]
        {
            ("movie", await movies.SettleCasesOfTitlesHoldingFilesAsync(cancellationToken) ?? []),
            ("series", await series.SettleCasesOfTitlesHoldingFilesAsync(cancellationToken) ?? [])
        })
        {
            foreach (var item in cases)
            {
                settled++;
                await activityFeed.RecordActivityAsync(
                    "recovery.case-settled",
                    $"{item.Title} is in the library now, so Deluno closed the problem that was open for it.",
                    JsonSerializer.Serialize(new { item.CaseId, item.FailureKind, MediaType = mediaType }),
                    null,
                    mediaType,
                    item.CaseId,
                    cancellationToken);
            }
        }

        return settled
            + await SettleDispatchFailuresAsync(cancellationToken)
            + await SettleHandoffsAsync(cancellationToken)
            + await SettleCasesOfClosedHandoffsAsync(cancellationToken)
            + await SettleUnmatchedOutputsOfImportedFilesAsync(cancellationToken)
            + await SettleOutcomesNotNeededAsync(cancellationToken)
            + await TellProcessorsOfImportedHandoffsAsync(cancellationToken);
    }

    /// <summary>
    /// An import issue about a hand-off that is already dismissed is the same problem, counted twice (#1228): the hand-off
    /// was closed (by a person, or by Deluno because the title was in the library) and its issue stayed in Activity, the
    /// bell and System. It closes now, and its System row with it.
    /// </summary>
    private async Task<int> SettleCasesOfClosedHandoffsAsync(CancellationToken cancellationToken)
    {
        if (processors is null)
        {
            return 0;
        }

        var closed = (await processors.ListProcessorHandoffsWithStatusAsync([ProcessorHandoffStatuses.Dismissed], cancellationToken) ?? [])
            .ToDictionary(handoff => handoff.Id, StringComparer.OrdinalIgnoreCase);
        if (closed.Count == 0)
        {
            return 0;
        }

        var settled = 0;
        foreach (var (mediaType, cases) in new[]
        {
            ("movie", ((await movies.GetImportRecoverySummaryAsync(cancellationToken))?.RecentCases ?? []).Select(item => (item.Id, item.Title, item.DetailsJson)).ToArray()),
            ("series", ((await series.GetImportRecoverySummaryAsync(cancellationToken))?.RecentCases ?? []).Select(item => (item.Id, item.Title, item.DetailsJson)).ToArray())
        })
        {
            var aboutClosed = cases
                .Select(item => (item, HandoffId: Deluno.Platform.Data.ProblemTwins.HandoffIdIn(item.DetailsJson)))
                .Where(pair => pair.HandoffId is not null && closed.ContainsKey(pair.HandoffId))
                .ToArray();
            foreach (var handoffId in aboutClosed.Select(pair => pair.HandoffId!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await processors.TellHandoffClosedAsync(handoffId, cancellationToken);
            }

            foreach (var (item, handoffId) in aboutClosed)
            {
                settled++;
                await activityFeed.RecordActivityAsync(
                    "recovery.case-settled",
                    $"{item.Title}'s hand-off to {closed[handoffId!].ProcessorName ?? "the processor"} was already dismissed, so Deluno closed the import issue that was open for it.",
                    JsonSerializer.Serialize(new { CaseId = item.Id, HandoffId = handoffId, MediaType = mediaType }),
                    null,
                    mediaType,
                    item.Id,
                    cancellationToken);
            }
        }

        return settled;
    }

    private const int ImportJobWindow = 600;

    /// <summary>
    /// "Processor output did not match anything" is a statement about a file with no hand-off to claim it. When that
    /// very file has been imported since, by any hand-off, the statement is no longer true (#1242): the case closes at
    /// once, with its row on System and a line in Activity. Rig: Retthree, Twicefilm and Twicetwo, where the resend's
    /// hand-off imported the file one second after the scan raised the problem.
    /// </summary>
    private async Task<int> SettleUnmatchedOutputsOfImportedFilesAsync(CancellationToken cancellationToken)
    {
        var imported = ((await jobs.ListAsync(ImportJobWindow, cancellationToken)) ?? [])
            .Where(job => job.JobType == "filesystem.import.execute"
                && string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .Select(job => (Name: LeafOf(WorkPlanner.TryReadImportSourcePath(job.PayloadJson)), Done: job.CompletedUtc ?? job.CreatedUtc))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();
        if (imported.Length == 0)
        {
            return 0;
        }

        var settled = 0;
        foreach (var (mediaType, cases) in new[]
        {
            ("movie", ((await movies.GetImportRecoverySummaryAsync(cancellationToken))?.RecentCases ?? [])
                .Select(item => (item.Id, item.Title, item.FailureKind, item.DetailsJson, item.DetectedUtc)).ToArray()),
            ("series", ((await series.GetImportRecoverySummaryAsync(cancellationToken))?.RecentCases ?? [])
                .Select(item => (item.Id, item.Title, item.FailureKind, item.DetailsJson, item.DetectedUtc)).ToArray())
        })
        {
            var covered = cases
                .Where(item => item.FailureKind == "processor-unmatched-output"
                    && FileNameIn(item.DetailsJson) is { } file
                    && imported.Any(done => string.Equals(done.Name, file, StringComparison.OrdinalIgnoreCase) && done.Done >= item.DetectedUtc))
                .ToArray();
            if (covered.Length == 0)
            {
                continue;
            }

            var reason = "The file was imported, so it no longer needs matching.";
            var scope = new ImportRecoveryCaseScope([], []) { CaseIds = covered.Select(item => item.Id).ToArray() };
            if (mediaType == "movie")
            {
                await movies.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken);
            }
            else
            {
                await series.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken);
            }

            foreach (var item in covered)
            {
                settled++;
                await Deluno.Platform.Data.ProblemTwins.ClearHealthRowAsync(item.Id, healthChecks, realtime, cancellationToken);
                await activityFeed.RecordActivityAsync(
                    "recovery.case-settled",
                    $"{item.Title} was imported, so Deluno closed the problem that said its cleaned file could not be matched.",
                    JsonSerializer.Serialize(new { CaseId = item.Id, item.FailureKind, MediaType = mediaType }),
                    null,
                    mediaType,
                    item.Id,
                    cancellationToken);
            }
        }

        return settled;
    }

    private static string? LeafOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static string? FileNameIn(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .FirstOrDefault(property => string.Equals(property.Name, "fileName", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String).Value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> SettleDispatchFailuresAsync(CancellationToken cancellationToken)
    {
        if (dispatches is null)
        {
            return 0;
        }

        var failed = ((await jobs.ListDownloadDispatchesAsync(DispatchWindow, null, cancellationToken)) ?? [])
            .Where(dispatch => string.Equals(dispatch.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(dispatch.EntityId))
            .ToList();
        if (failed.Count == 0)
        {
            return 0;
        }

        var holds = new Dictionary<string, TitleFileHolds>
        {
            ["movie"] = await movies.GetTitleFileHoldsAsync(IdsOf(failed, "movie"), cancellationToken) ?? TitleFileHolds.None,
            ["episode"] = await series.GetTitleFileHoldsAsync(IdsOf(failed, "episode"), cancellationToken) ?? TitleFileHolds.None
        };

        var settled = 0;
        foreach (var dispatch in failed)
        {
            if (!holds.TryGetValue(dispatch.EntityType.ToLowerInvariant(), out var held)
                || !held.ImportedByEntityId.TryGetValue(dispatch.EntityId, out var imported)
                || (imported is { } arrived && arrived < dispatch.CreatedUtc))
            {
                continue;
            }

            await dispatches.RecordImportOutcomeAsync(dispatch.Id, "imported", dispatch.ImportedFilePath, null, null, cancellationToken);
            settled++;
            await activityFeed.RecordActivityAsync(
                "download.import-failure-settled",
                $"{dispatch.ReleaseName} could not be imported, but its title is in the library now, so Deluno closed the failure.",
                JsonSerializer.Serialize(new { DispatchId = dispatch.Id, dispatch.EntityType, dispatch.EntityId, dispatch.ImportFailureCode }),
                null,
                "download-dispatch",
                dispatch.Id,
                cancellationToken);
        }

        return settled;
    }

    /// <summary>
    /// A hand-off that ended badly asks for a decision until somebody dismisses it. When its title holds a file
    /// that arrived after the hand-off was made, the question has been answered some other way (the film was
    /// imported by hand, from the original, or from a repeat hand-off), so it closes itself.
    /// </summary>
    private async Task<int> SettleHandoffsAsync(CancellationToken cancellationToken)
    {
        if (processors is null || dispatches is null)
        {
            return 0;
        }

        var open = (await processors.ListProcessorHandoffsWithStatusAsync(
                [
                    ProcessorHandoffStatuses.Failed, ProcessorHandoffStatuses.Rejected, ProcessorHandoffStatuses.TimedOut,
                    ProcessorHandoffStatuses.Stalled, ProcessorHandoffStatuses.Lost, ProcessorHandoffStatuses.Cancelled,
                    ProcessorHandoffStatuses.Completed, ProcessorHandoffStatuses.SourceGone
                ],
                cancellationToken) ?? [])
            .Where(handoff => ProcessorHandoffStatuses.NeedsPerson(handoff.Status, handoff.FailureMessage))
            .ToList();
        if (open.Count == 0)
        {
            return 0;
        }

        var settled = 0;

        // A processor said the download was gone (#1186): it closes when Deluno removed that download or the title
        // has its file, whichever happened since the report.
        foreach (var gone in open.Where(handoff => handoff.Status == ProcessorHandoffStatuses.SourceGone))
        {
            if (await SourceGoneRule.SettledBecauseAsync(gone, dispatches, movies, series, cancellationToken) is not { } reason)
            {
                continue;
            }

            await processors.UpdateProcessorHandoffAsync(
                gone.Id, ProcessorHandoffStatuses.Dismissed, null, null, gone.FailureMessage, cancellationToken);
            settled++;
            await activityFeed.RecordActivityAsync(
                "processing.source-gone.settled",
                $"{gone.FailureMessage ?? $"The download of {gone.ReleaseName} was gone."} {reason}",
                JsonSerializer.Serialize(new { HandoffId = gone.Id, gone.LibraryId, PreviousStatus = gone.Status }),
                null,
                "processor-handoff",
                gone.Id,
                cancellationToken);
        }

        open = open.Where(handoff => handoff.Status != ProcessorHandoffStatuses.SourceGone).ToList();
        if (open.Count == 0)
        {
            return settled;
        }

        var byHandoff = new List<(Deluno.Platform.Contracts.ProcessorHandoffItem Handoff, DownloadDispatchItem Dispatch)>();
        foreach (var handoff in open)
        {
            // The send this hand-off belongs to, found the way the gone-download rule finds it (#1186): the latest send of
            // the release, never an older archived one of the same hash.
            var dispatch = await SourceGoneRule.DispatchOfAsync(handoff, dispatches, cancellationToken);
            if (dispatch is not null && !string.IsNullOrWhiteSpace(dispatch.EntityId))
            {
                byHandoff.Add((handoff, dispatch));
            }
        }

        var holds = new Dictionary<string, TitleFileHolds>
        {
            ["movie"] = await movies.GetTitleFileHoldsAsync(IdsOf(byHandoff.Select(pair => pair.Dispatch), "movie"), cancellationToken) ?? TitleFileHolds.None,
            ["episode"] = await series.GetTitleFileHoldsAsync(IdsOf(byHandoff.Select(pair => pair.Dispatch), "episode"), cancellationToken) ?? TitleFileHolds.None
        };

        foreach (var (handoff, dispatch) in byHandoff)
        {
            if (!holds.TryGetValue(dispatch.EntityType.ToLowerInvariant(), out var held)
                || !held.ImportedByEntityId.TryGetValue(dispatch.EntityId, out var imported)
                || (imported is { } arrived && arrived < handoff.CreatedUtc))
            {
                continue;
            }

            // A hand-off the processor finished, whose import ran, is not a problem that was dismissed: it
            // worked, and only carries a note (the small extras the processor left out, #1182). It stays
            // Completed, the note is cleared so it stops asking for a person, and Transfers reads it as any
            // other import. Activity already holds the note.
            //
            // The import job may be queued or running at this moment: the film's file arrives a little before the
            // job writes "completed" (the rig, 9 Oct: one second). Dismissing then would lose this exception, so the
            // settler waits for the job to finish and looks again on its next pass (#1182).
            var importJob = handoff.Status == ProcessorHandoffStatuses.Completed && !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                ? await jobs.GetAsync(handoff.ImportJobId, cancellationToken)
                : null;
            if (importJob is not null && Deluno.Integrations.DownloadClients.DownloadAttempts.ImportJobInFlight(importJob.Status))
            {
                continue;
            }

            if (importJob is { Status: "completed" })
            {
                await processors.UpdateProcessorHandoffAsync(
                    handoff.Id, ProcessorHandoffStatuses.Completed, null, null, null, cancellationToken);
                settled++;
                continue;
            }

            await processors.UpdateProcessorHandoffAsync(
                handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, handoff.FailureMessage, cancellationToken);
            settled++;
            await activityFeed.RecordActivityAsync(
                "processing.dismissed",
                $"{handoff.ReleaseName}'s hand-off to {handoff.ProcessorName ?? "the processor"} did not finish, but its title is in the library now, so Deluno closed it.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, PreviousStatus = handoff.Status }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }

        return settled;
    }

    /// <summary>
    /// An outcome already owed for a hand-off the processor itself ended as failed or gone (#1221) is not needed: that
    /// processor handed back no file to release, so repeating it for a week is noise on both sides. It is closed
    /// here, once, with an Activity line, and not sent again.
    /// </summary>
    private async Task<int> SettleOutcomesNotNeededAsync(CancellationToken cancellationToken)
    {
        if (processors is null)
        {
            return 0;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var settled = 0;
        foreach (var owed in await processors.ListDueProcessorOutcomesAsync(now.AddYears(1), cancellationToken) ?? [])
        {
            if (owed.Delivery != ProcessorOutcomeDeliveries.Pending
                || await processors.GetProcessorHandoffAsync(owed.HandoffId, cancellationToken) is not { } handoff)
            {
                continue;
            }

            var imported = !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                && await jobs.GetAsync(handoff.ImportJobId!, cancellationToken) is { Status: "completed" };
            if (imported || !ProcessorHandoffStatuses.ProcessorReportedItFailed(handoff))
            {
                continue;
            }

            var said = $"{owed.ProcessorName} ended this hand-off as failed and handed back no file, so there is nothing for it to release.";
            await processors.SaveProcessorOutcomeAsync(
                owed with
                {
                    Delivery = ProcessorOutcomeDeliveries.GivenUp,
                    NextAttemptUtc = null,
                    ProcessorSaid = said
                },
                cancellationToken);
            settled++;
            await activityFeed.RecordActivityAsync(
                "processing.outcome-not-needed",
                $"Deluno stopped telling {owed.ProcessorName} what became of {owed.ReleaseName}: {said}",
                JsonSerializer.Serialize(new { owed.HandoffId, owed.LibraryId, owed.ReleaseName }),
                null,
                "processor-handoff",
                owed.HandoffId,
                cancellationToken);
        }

        return settled;
    }

    /// <summary>How far back a finished hand-off is looked at for an outcome its processor was never told.</summary>
    private static readonly TimeSpan OutcomeWindow = TimeSpan.FromDays(60);

    /// <summary>What a processor hears when a hand-off was closed with no reason on it.</summary>
    internal const string DefaultNotImportedReason = "The hand-off was closed, so Deluno will not import this file.";

    /// <summary>
    /// Every hand-off Deluno closes tells its processor what became of the file (#578, #1220), whichever way it closed:
    /// a person stopped tracking the download, dismissed the hand-off or removed the title, Deluno stopped waiting, the
    /// download was gone, or another copy took over. All of them end as dismissed, and this is the one place that says
    /// so: one whose import ran to its end is reported as imported (a partial result settled before that report went
    /// out, #1182); every other is reported as final, not imported, with the reason it carries. It is written down and
    /// sent once, through the delivery every other outcome uses, so a processor that does not ask to be told is left
    /// alone and one that is down is told again later. A hand-off with an outcome record is never reported twice, and
    /// closed hand-offs that never had one (older than this rule) are told the first time this runs.
    /// </summary>
    private async Task<int> TellProcessorsOfImportedHandoffsAsync(CancellationToken cancellationToken)
    {
        if (processors is null || processorConnections is null)
        {
            return 0;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var told = 0;
        var finished = (await processors.ListProcessorHandoffsWithStatusAsync(
                [ProcessorHandoffStatuses.Dismissed], cancellationToken) ?? [])
            .Where(item => now - item.UpdatedUtc < OutcomeWindow)
            .ToList();
        if (finished.Count == 0)
        {
            return 0;
        }

        foreach (var handoff in finished)
        {
            if (await processors.GetProcessorOutcomeAsync(handoff.Id, cancellationToken) is not null)
            {
                continue;
            }

            var imported = !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                && await jobs.GetAsync(handoff.ImportJobId!, cancellationToken) is { Status: "completed" };

            // The processor itself said it failed or the download was gone, so it handed back no file and there is
            // nothing for it to let go of (#1221). Nothing is written and nothing is sent.
            // Nothing is sent, but the hand-off is not closed in silence (#1235): it says once what became of it, and
            // the record written is what stops it being said on the next pass.
            if (!imported && ProcessorHandoffStatuses.ProcessorReportedItFailed(handoff))
            {
                await SayNothingWasOwedAsync(handoff, now, cancellationToken);
                continue;
            }

            var sent = await Deluno.Platform.ProcessorOutcomeDelivery.RecordAndSendAsync(
                processors,
                processorConnections,
                activityFeed,
                handoff,
                imported ? ProcessorHandoffOutcome.Imported : ProcessorHandoffOutcome.NotImported,
                imported ? handoff.OutputPath : null,
                imported ? null : string.IsNullOrWhiteSpace(handoff.FailureMessage) ? DefaultNotImportedReason : handoff.FailureMessage,
                now,
                cancellationToken);
            if (sent is not null)
            {
                told++;
            }
        }

        return told;
    }

    /// <summary>
    /// A closed hand-off that handed back no file (it failed at the submit, or the processor ended it as failed or
    /// gone) owes its processor nothing, and says so in Activity once (#1235).
    /// </summary>
    private async Task SayNothingWasOwedAsync(ProcessorHandoffItem handoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (processors is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(handoff.ProcessorName) ? "The processor" : handoff.ProcessorName;
        var said = $"{handoff.ReleaseName}'s hand-off to {name} ended as failed and no file was handed back, so there was nothing to tell it and nothing for it to release.";
        await processors.SaveProcessorOutcomeAsync(
            new ProcessorOutcomeRecord(
                handoff.Id,
                name,
                handoff.LibraryId,
                handoff.ReleaseName,
                ProcessorOutcomeRecord.Word(ProcessorHandoffOutcome.NotImported),
                null,
                string.IsNullOrWhiteSpace(handoff.FailureMessage) ? DefaultNotImportedReason : handoff.FailureMessage,
                now,
                ProcessorOutcomeDeliveries.GivenUp,
                0,
                null,
                null,
                false,
                said,
                now,
                handoff.ProcessorConnectionId),
            cancellationToken);
        await activityFeed.RecordActivityAsync(
            "processing.outcome-not-needed",
            said,
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
    }

    private static string[] IdsOf(IEnumerable<DownloadDispatchItem> items, string entityType)
        => items.Where(item => string.Equals(item.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntityId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
