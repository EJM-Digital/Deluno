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
    TimeProvider? clock = null)
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
            + await TellProcessorsOfImportedHandoffsAsync(cancellationToken);
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
                    ProcessorHandoffStatuses.Completed
                ],
                cancellationToken) ?? [])
            .Where(handoff => ProcessorHandoffStatuses.NeedsPerson(handoff.Status, handoff.FailureMessage))
            .ToList();
        if (open.Count == 0)
        {
            return 0;
        }

        var byHandoff = new List<(Deluno.Platform.Contracts.ProcessorHandoffItem Handoff, DownloadDispatchItem Dispatch)>();
        foreach (var handoff in open)
        {
            var dispatch = await dispatches.FindAnyDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken);
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

        var settled = 0;
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
            if (handoff.Status == ProcessorHandoffStatuses.Completed
                && !string.IsNullOrWhiteSpace(handoff.ImportJobId)
                && await jobs.GetAsync(handoff.ImportJobId, cancellationToken) is { Status: "completed" })
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

    /// <summary>How far back a finished hand-off is looked at for an outcome its processor was never told.</summary>
    private static readonly TimeSpan OutcomeWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// A hand-off whose import ran to its end has told its processor so (#578). One that was settled as dismissed
    /// before that report went out (a partial result, #1182) never did, and the processor kept waiting for its media
    /// manager. Written down and sent now, once: a hand-off with an outcome record is left alone.
    /// </summary>
    private async Task<int> TellProcessorsOfImportedHandoffsAsync(CancellationToken cancellationToken)
    {
        if (processors is null || processorConnections is null)
        {
            return 0;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var told = 0;
        var finished = await processors.ListProcessorHandoffsWithStatusAsync(
            [ProcessorHandoffStatuses.Dismissed], cancellationToken) ?? [];
        foreach (var handoff in finished.Where(item => !string.IsNullOrWhiteSpace(item.ImportJobId) && now - item.UpdatedUtc < OutcomeWindow))
        {
            if (await jobs.GetAsync(handoff.ImportJobId!, cancellationToken) is not { Status: "completed" }
                || await processors.GetProcessorOutcomeAsync(handoff.Id, cancellationToken) is not null)
            {
                continue;
            }

            var sent = await Deluno.Platform.ProcessorOutcomeDelivery.RecordAndSendAsync(
                processors,
                processorConnections,
                activityFeed,
                handoff,
                ProcessorHandoffOutcome.Imported,
                handoff.OutputPath,
                null,
                now,
                cancellationToken);
            if (sent is not null)
            {
                told++;
            }
        }

        return told;
    }

    private static string[] IdsOf(IEnumerable<DownloadDispatchItem> items, string entityType)
        => items.Where(item => string.Equals(item.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntityId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
