using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Keeps track of files that are with a processor by asking it, not by timing
/// it (#511).
///
/// <para><b>What replaced the timeout.</b> Deluno used to give up six hours after
/// handing a file over, and an output that arrived later was never imported.
/// The owner's objection was that no fixed limit fits something this dynamic:
/// a processor with an overnight schedule, a queue of 4K files or a patient
/// retry policy legitimately holds a file for a day. So a processor that can
/// answer is asked, every pass, what it is doing with each file:</para>
///
/// <list type="bullet">
/// <item>queued, scheduled, working — keep waiting, and show the answer;</item>
/// <item>never heard of it — stop, and offer to import the original;</item>
/// <item>not answering — keep waiting and say so; the clock does not run while
/// it cannot be asked;</item>
/// <item>no change at all for the library's limit (24 hours to 7 days, 48 by
/// default) while it answers and has no run scheduled ahead — the library's
/// failure rule decides, and the processor is asked to drop the file first.</item>
/// </list>
///
/// <para>A processor that cannot be asked is judged the same way, with the
/// hand-off's own last change standing in for the processor's. And whatever the
/// ending, a processed file that turns up afterwards is still imported.</para>
/// </summary>
public sealed class ProcessorHandoffWatcher(
    IProcessorRepository processorRepository,
    IProcessorConnectionService processorConnections,
    IActivityFeedRepository activityFeed,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IProcessorOutputImportQueue? outputImports = null,
    IDownloadDispatchesRepository? dispatches = null)
{
    /// <summary>
    /// A processor that has just accepted a hand-off may not have written it
    /// down yet. A "never heard of it" inside this window is not believed.
    /// </summary>
    public static readonly TimeSpan NotFoundGrace = TimeSpan.FromMinutes(15);

    /// <summary>How long a hand-off may wait to be sent before it is reported as never picked up (#1155).</summary>
    public static readonly TimeSpan WaitingToBeSentLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a hand-off may sit with a processor that says it is finished, or has never heard of it, before
    /// Deluno stops waiting and settles it itself (#1155): import the output that is on disk, send it again once,
    /// then ask the person.
    /// </summary>
    public static readonly TimeSpan ResolveAfter = WaitingToBeSentLimit;

    /// <summary>Starts the notice saved on a hand-off Deluno has already sent a second time.</summary>
    public const string SentAgainNotice = "Deluno sent this to the processor again because it had no record of a finished copy.";

    /// <summary>How long a processor can be unreachable before Deluno says so.</summary>
    public static readonly TimeSpan UnreachableNoticeAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// How many times the library's limit a processor may hold a file before
    /// Deluno stops waiting whatever it says (#572). Generous, because the
    /// limit is what the person chose and this is only the backstop for a
    /// processor whose answers keep the ordinary rules from ever applying.
    /// </summary>
    private const int HeldTooLongMultiplier = 3;

    /// <param name="libraries">The libraries that can be worked on right now.</param>
    /// <param name="allLibraries">
    /// Every library that exists, reachable or not. A hand-off is orphaned only
    /// when its library is gone from here or no longer processes before import;
    /// a library whose folder is merely unreachable keeps its hand-offs.
    /// </param>
    public async Task WatchAsync(
        IReadOnlyList<LibraryItem> libraries,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<LibraryItem>? allLibraries = null)
    {
        // Hand-offs that are over but still owe something (#1155).
        await SettleReturnedTitlesAsync(allLibraries ?? libraries, now, cancellationToken);
        await SettleFailedImportsAsync(allLibraries ?? libraries, now, cancellationToken);
        await SettleSupersededAsync(allLibraries ?? libraries, now, cancellationToken);

        // Exactly the hand-offs a processor still has, however much history sits
        // in front of them (#571).
        var handoffs = await processorRepository.ListProcessorHandoffsWithStatusAsync(
            ProcessorHandoffStatuses.WithProcessor,
            cancellationToken);
        var connections = new Dictionary<string, ProcessorConnectionItem?>(StringComparer.OrdinalIgnoreCase);
        var support = new Dictionary<string, ProcessorStatusSupport>(StringComparer.OrdinalIgnoreCase);
        var connectionsNoticed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handoff in handoffs.Where(item => ProcessorHandoffStatuses.IsWithProcessor(item.Status)))
        {
            // Nothing is waiting for this hand-off any more. It used to be
            // skipped here, which left it open for ever: no limit applied, no
            // screen could close it, and Transfers went on counting it as work
            // in progress. Seen on the rig after a proof library was removed.
            var owner = (allLibraries ?? libraries).FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (owner is null || !ImportWorkflows.HandsToProcessor(owner.ImportWorkflow))
            {
                await EndAsync(
                    handoff,
                    ProcessorHandoffStatuses.Cancelled,
                    owner is null
                        ? $"The library {handoff.ReleaseName} was downloaded for has been removed, so nothing is waiting for a processed copy."
                        : $"{owner.Name} no longer processes downloads before importing them, so nothing is waiting for a processed copy.",
                    cancellationToken);
                continue;
            }

            // A hand-off that has waited to be sent past the limit is not going
            // to be (#1155): its processor is switched off or no longer
            // connected, and the planner sends one every pass that it can.
            // One clear item for the person, with the retry, instead of
            // "Waiting for processor" for ever.
            if (string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase) &&
                now - handoff.UpdatedUtc >= WaitingToBeSentLimit)
            {
                // The download is gone (Deluno removed it, or it was taken out of the client): nothing is owed a
                // send, so this raises nothing. It closes quietly, as the removal itself would have (#1216).
                if (await DownloadIsGoneAsync(handoff, cancellationToken))
                {
                    await processorRepository.UpdateProcessorHandoffAsync(
                        handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null,
                        $"The download of {handoff.ReleaseName} is gone, so Deluno will not import it.", cancellationToken);
                    continue;
                }

                // The processor may well have done the work already, and its output
                // be sitting in the ready folder: take that before giving up (#1155).
                if (libraries.FirstOrDefault(item => string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase)) is { } waitingLibrary
                    && await TryImportOutputAsync(handoff, waitingLibrary, null, null, cancellationToken))
                {
                    continue;
                }

                var waitingProcessor = string.IsNullOrWhiteSpace(handoff.ProcessorName) ? "The processor" : handoff.ProcessorName;
                var neverSent = $"{waitingProcessor} never picked up {handoff.ReleaseName}: Deluno could not send it. Check the processor's connection, then send it again.";
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Failed, null, null, neverSent, cancellationToken);
                await activityFeed.RecordActivityAsync(
                    "processing.waiting-too-long",
                    neverSent,
                    JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                continue;
            }

            // A hand-off still waiting to be sent is the planner's to send.
            if (!ProcessorHandoffStatuses.IsOpen(handoff.Status))
            {
                continue;
            }

            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                // Its folder cannot be reached right now. That is not an ending.
                continue;
            }

            // The library's link wins, then the hand-off's own; both are ids, so a
            // renamed connection is still this connection (#1077).
            var connectionId = library.ProcessorConnectionId ?? handoff.ProcessorConnectionId;
            var connectionName = library.ProcessorName ?? handoff.ProcessorName ?? string.Empty;
            var connectionKey = connectionId ?? connectionName;
            if (!connections.TryGetValue(connectionKey, out var connection))
            {
                connection = await processorRepository.FindProcessorConnectionAsync(connectionId, connectionName, cancellationToken);
                connections[connectionKey] = connection;
            }

            var limit = TimeSpan.FromMinutes(ProcessorStallLimits.Normalize(library.ProcessorTimeoutMinutes));
            var processorName = connection?.Name ?? library.ProcessorName ?? "The processor";

            if (connection is not { IsEnabled: true })
            {
                await JudgeWithoutAskingAsync(handoff, library, processorName, limit, now, cancellationToken);
                continue;
            }

            if (!support.TryGetValue(connection.Id, out var canAsk))
            {
                canAsk = await processorConnections.GetStatusSupportAsync(connection, cancellationToken);
                support[connection.Id] = canAsk;
            }

            if (canAsk.Answer is ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden)
            {
                await ObserveSilenceAsync(handoff, connection, canAsk.Answer, canAsk.Message, now, connectionsNoticed, cancellationToken);
                continue;
            }

            // It answered. One that was stored as not answering is answering again, and the Dashboard, System and
            // the tray should hear that now, not at the next regular check (#1213).
            if (connection.HealthStatus == "unreachable" && connectionsNoticed.Add($"answering:{connection.Id}"))
            {
                await processorRepository.RecordProcessorConnectionHealthAsync(connection.Id, "healthy", null, cancellationToken);
            }

            if (!canAsk.CanReportStatus)
            {
                await JudgeWithoutAskingAsync(handoff, library, processorName, limit, now, cancellationToken);
                continue;
            }

            var answer = await processorConnections.GetHandoffStatusAsync(connection, handoff.Id, cancellationToken);
            switch (answer.Answer)
            {
                case ProcessorAnswer.Answered:
                    await ObserveAnswerAsync(handoff, library, connection, canAsk, answer, limit, now, cancellationToken);
                    break;

                case ProcessorAnswer.NotFound when now - handoff.UpdatedUtc >= NotFoundGrace:
                    // Its output may be in the ready folder all the same, and a hand-off it
                    // lost is sent again once before a person is asked (#1155).
                    if (!await ResolveStuckAsync(handoff, library, connection, null, now, cancellationToken))
                    {
                        await RecordLostAsync(handoff, library, processorName, now, cancellationToken);
                    }

                    break;

                case ProcessorAnswer.NotFound:
                    break;

                default:
                    await ObserveSilenceAsync(handoff, connection, answer.Answer, answer.Message, now, connectionsNoticed, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>How far back a finished hand-off is looked at again for a title that came back.</summary>
    private static readonly TimeSpan ReturnedTitleWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// A finished hand-off whose import went into a title that was then removed and added again settles itself (#1155):
    /// the processor's output, if it is still on disk, is imported into the title that is there now; if it is gone, the
    /// download is handed over afresh, once. A title that already has its file is left alone.
    /// </summary>
    private async Task SettleReturnedTitlesAsync(IReadOnlyList<LibraryItem> libraries, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (outputImports is null)
        {
            return;
        }

        var finished = await processorRepository.ListProcessorHandoffsWithStatusAsync([ProcessorHandoffStatuses.Completed], cancellationToken);
        foreach (var handoff in finished.Where(item => now - item.UpdatedUtc < ReturnedTitleWindow))
        {
            var library = libraries.FirstOrDefault(item => string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null || !ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                continue;
            }

            // Once: settling changes the hand-off, which puts it after the title's arrival.
            if (await outputImports.TitleCameBackWithoutItsFileAsync(handoff, cancellationToken) is not { } addedUtc
                || handoff.UpdatedUtc >= addedUtc)
            {
                continue;
            }

            var output = !string.IsNullOrWhiteSpace(handoff.OutputPath) && (File.Exists(handoff.OutputPath) || Directory.Exists(handoff.OutputPath))
                ? handoff.OutputPath
                : null;
            var queuedMessage = $"{handoff.ReleaseName} was imported into a title that was since removed and added again, so Deluno queued the processor's finished copy for the title that is there now.";
            if (await TryImportOutputAsync(handoff, library, null, output, cancellationToken, queuedMessage))
            {
                continue;
            }

            var connection = await processorRepository.FindProcessorConnectionAsync(handoff.ProcessorConnectionId ?? library.ProcessorConnectionId, handoff.ProcessorName ?? library.ProcessorName, cancellationToken);
            if (connection is not { IsEnabled: true })
            {
                continue;
            }

            var mappings = await processorRepository.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
            var outbound = await HandoffFileSelection.ForProcessorAsync(handoff, library, connection, mappings, processorConnections, activityFeed, cancellationToken);
            var submission = await processorConnections.SubmitAsync(connection, outbound, cancellationToken);
            if (!submission.IsAccepted)
            {
                continue;
            }

            await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, SentAgainNotice, cancellationToken);
            await activityFeed.RecordActivityAsync(
                "processing.resubmitted",
                $"{handoff.ReleaseName} was imported into a title that was since removed and added again, and {connection.Name} no longer has the finished copy, so Deluno handed the download over again.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    /// <summary>
    /// A finished hand-off whose output is still on disk, while its title (the one that is there now) holds no file
    /// and its import gave up, is imported once more (#1155), whatever the order of the title's return and the
    /// hand-off. The import adopts an identical file already at the destination. Once: the Activity line it writes is
    /// the record that it was tried, and a person's Try again does the rest.
    /// </summary>
    private async Task SettleFailedImportsAsync(IReadOnlyList<LibraryItem> libraries, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (outputImports is null)
        {
            return;
        }

        var finished = await processorRepository.ListProcessorHandoffsWithStatusAsync([ProcessorHandoffStatuses.Completed], cancellationToken);
        foreach (var handoff in finished.Where(item => now - item.UpdatedUtc < ReturnedTitleWindow))
        {
            var library = libraries.FirstOrDefault(item => string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null || !ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(handoff.OutputPath) || !(File.Exists(handoff.OutputPath) || Directory.Exists(handoff.OutputPath)))
            {
                continue;
            }

            if (!await outputImports.OwesItsTitleItsFileAsync(handoff, cancellationToken))
            {
                continue;
            }

            // The second try is made once (the import job it revives remembers that it is the retry), and it is
            // told once: the line the import writes when it ends, not one for each step on the way (#1155).
            await TryImportOutputAsync(
                handoff,
                library,
                null,
                handoff.OutputPath,
                cancellationToken,
                retriedBecause: $"{handoff.ReleaseName} had been processed but its import gave up and the title still had no file, so Deluno tried the import again.");
        }
    }

    /// <summary>
    /// A hand-off that went wrong needs nobody once the same film or episode has been imported through another download
    /// (#1155): it is dismissed, its Needs action entry closes, and Activity says so once.
    /// </summary>
    /// <summary>
    /// Whether the download this hand-off was made for is gone: Deluno has a record of sending it and none of those
    /// records is open any more, because the download was removed (#1216). A download Deluno never sent has no
    /// record and is not judged gone here.
    /// </summary>
    private async Task<bool> DownloadIsGoneAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken)
    {
        if (dispatches is null || string.IsNullOrWhiteSpace(handoff.ClientId) || string.IsNullOrWhiteSpace(handoff.QueueItemId))
        {
            return false;
        }

        if ((await dispatches.FindOpenDispatchesByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken)).Count > 0)
        {
            return false;
        }

        return await dispatches.FindAnyDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken) is not null;
    }

    private async Task SettleSupersededAsync(IReadOnlyList<LibraryItem> libraries, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (outputImports is null)
        {
            return;
        }

        var wrong = await processorRepository.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WentWrong, cancellationToken);
        foreach (var handoff in wrong)
        {
            var library = libraries.FirstOrDefault(item => string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null || !await outputImports.TitleHasFileFromAnotherDownloadAsync(handoff, cancellationToken))
            {
                continue;
            }

            var message = $"{handoff.ReleaseName} did not go through the processor, but the title was imported from another download, so Deluno closed this one.";
            await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, message, cancellationToken);
            var scope = new Deluno.Contracts.ImportRecoveryCaseScope([], []) { HandoffIds = [handoff.Id] };
            if (DelunoValueNormalizers.NormalizeMediaType(library.MediaType) == "tv")
            {
                await seriesCatalogRepository.SettleImportRecoveryCasesAsync(scope, message, cancellationToken);
            }
            else
            {
                await movieCatalogRepository.SettleImportRecoveryCasesAsync(scope, message, cancellationToken);
            }

            await activityFeed.RecordActivityAsync(
                "processing.superseded",
                message,
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    /// <summary>
    /// A hand-off that has waited past its limit settles itself (#1155): the output that is on disk is imported;
    /// failing that the hand-off is sent again, once; failing that the person is asked, with a retry.
    /// </summary>
    /// <returns>True when this dealt with the hand-off (and the caller has nothing more to do).</returns>
    private async Task<bool> ResolveStuckAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem connection,
        ProcessorHandoffStatusResult? answer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (outputImports is null)
        {
            return false;
        }

        var mappings = await processorRepository.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
        var reported = answer?.OutputPath is { Length: > 0 } said
            ? mappings.Count > 0 ? ProcessorPathMappingTranslator.ToDeluno(said, mappings) ?? said : said
            : null;
        if (await TryImportOutputAsync(handoff, library, connection, reported, cancellationToken))
        {
            return true;
        }

        var processorName = connection.Name;
        if (string.IsNullOrEmpty(handoff.FailureMessage) || !handoff.FailureMessage.StartsWith(SentAgainNotice, StringComparison.Ordinal))
        {
            var outbound = await HandoffFileSelection.ForProcessorAsync(handoff, library, connection, mappings, processorConnections, activityFeed, cancellationToken);
            var submission = await processorConnections.SubmitAsync(connection, outbound, cancellationToken);
            if (submission.IsAccepted)
            {
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Submitted, null, null, SentAgainNotice, cancellationToken);
                await activityFeed.RecordActivityAsync(
                    "processing.resubmitted",
                    $"{processorName} had no finished copy of {handoff.ReleaseName} {Describe(now - handoff.UpdatedUtc)} after it was handed over, so Deluno sent it again.",
                    JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                return true;
            }
        }

        var summary = $"{processorName} never picked up {handoff.ReleaseName}: Deluno sent it twice and nothing came back. Send it again once {processorName} is working, or import the original.";
        await EndAsync(handoff, ProcessorHandoffStatuses.Lost, summary, cancellationToken);
        await RaiseRecoveryCaseAsync(
            handoff,
            library,
            "processor-lost",
            summary,
            "Import the original, hand it to the processor again, or search for another release.",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// The processor's finished copy of this download, when it is on disk where Deluno can read it: the path the
    /// processor reported, or the ready folder's own folder for this download. Queued through the one way a
    /// processed file is imported, which checks it is this download's.
    /// </summary>
    private async Task<bool> TryImportOutputAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem? connection,
        string? reportedOutput,
        CancellationToken cancellationToken,
        string? queuedMessage = null,
        string? retriedBecause = null)
    {
        if (outputImports is null || string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
        {
            return false;
        }

        string? output = null;
        if (!string.IsNullOrWhiteSpace(reportedOutput)
            && (File.Exists(reportedOutput) || Directory.Exists(reportedOutput))
            && ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(reportedOutput, library.ProcessorOutputPath)
            && ProcessorOutputPathPolicy.IsOutputAbout(reportedOutput, library.ProcessorOutputPath, handoff.SourcePath))
        {
            output = reportedOutput;
        }

        output ??= WorkPlanner.FindCorrelatedProcessorOutputs(library.ProcessorOutputPath, handoff.SourcePath).FirstOrDefault();
        if (output is null)
        {
            return false;
        }

        var name = connection?.Name ?? handoff.ProcessorName ?? "The processor";
        if (await outputImports.WasAlreadyImportedAsync(handoff, cancellationToken))
        {
            var deleted = ProcessorOutputPathPolicy.DeleteSecondCopy(output, library.ProcessorOutputPath, handoff.SourcePath);
            await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, output, null, null, cancellationToken);
            await activityFeed.RecordActivityAsync(
                "processing.output.already-imported",
                deleted
                    ? $"{name} had a second copy of {handoff.ReleaseName}, which Deluno had already imported. Deluno deleted the second copy."
                    : $"{name} finished {handoff.ReleaseName}, which Deluno had already imported. There was nothing to import.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, OutputPath = output, Deleted = deleted }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
            return true;
        }

        var import = await outputImports.QueueAsync(library, handoff, output, "processor-watch", cancellationToken, retriedBecause);
        var notImported = import.NotImportedBecause is { } removed
            ? $"{name} produced a cleaned file, but {removed}, so Deluno did not import it. The file is still at {output}."
            : null;
        await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, output, import.JobId, notImported, cancellationToken);
        if (retriedBecause is not null && import.NotImportedBecause is null)
        {
            return true;
        }

        await activityFeed.RecordActivityAsync(
            import.NotImportedBecause is not null ? "filesystem.import.not-imported" : "processing.completed.import-queued",
            notImported ?? queuedMessage ?? $"{name} had finished {handoff.ReleaseName} without Deluno hearing of it; Deluno found the cleaned file and queued it for import.",
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, OutputPath = output, import.JobId, import.DispatchId }),
            import.JobId,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
        return true;
    }

    /// <summary>
    /// A processor that cannot be asked. The hand-off's last recorded change -
    /// submission, or an accepted or started event - is the only sign of life,
    /// so the same limit is measured from that.
    /// </summary>
    private async Task JudgeWithoutAskingAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string processorName,
        TimeSpan limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now - handoff.UpdatedUtc <= limit)
        {
            return;
        }

        await GiveUpAsync(
            handoff,
            library,
            ProcessorHandoffStatuses.TimedOut,
            $"{handoff.ReleaseName}: no word from {processorName} for {Describe(now - handoff.UpdatedUtc)}, and it cannot be asked what it is doing.",
            cancellationToken);
    }

    private async Task ObserveAnswerAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem connection,
        ProcessorStatusSupport canAsk,
        ProcessorHandoffStatusResult answer,
        TimeSpan limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previous = handoff.Processor;
        var answeringSince = previous is null || previous.UnreachableSinceUtc is not null || previous.AnsweringSinceUtc is null
            ? now
            : previous.AnsweringSinceUtc;
        var observation = new ProcessorHandoffObservation(
            answer.State,
            answer.Message,
            answer.QueuePosition,
            answer.ScheduledForUtc,
            answer.LastChangedUtc,
            now,
            null,
            answeringSince);
        await processorRepository.RecordProcessorObservationAsync(handoff.Id, observation, cancellationToken);

        switch (answer.State)
        {
            case ProcessorHandoffStates.Completed or ProcessorHandoffStates.PassedThrough:
                // Its report or the output folder brings the file in, and either
                // one closes the hand-off. If neither has by the library's limit,
                // nothing will: the output went somewhere Deluno does not look,
                // or an earlier download of the same release already imported
                // it. Left open, the hand-off also claimed every later download
                // into the same folder, and those never reached the processor
                // (#561). The processor's own "finished at" is the clock:
                // Deluno's answering streak restarts whenever the processor
                // does, which would put the limit off every time.
                var finishedFor = now - (answer.LastChangedUtc ?? answeringSince ?? handoff.UpdatedUtc);
                if (finishedFor >= ResolveAfter && await ResolveStuckAsync(handoff, library, connection, answer, now, cancellationToken))
                {
                    return;
                }

                if (finishedFor > limit)
                {
                    await GiveUpAsync(
                        handoff,
                        library,
                        ProcessorHandoffStatuses.TimedOut,
                        $"{connection.Name} finished {handoff.ReleaseName} {Describe(finishedFor)} ago, but no processed file arrived for Deluno to import.",
                        cancellationToken);
                }

                return;

            case ProcessorHandoffStates.Queued or ProcessorHandoffStates.Scheduled or ProcessorHandoffStates.Working:
                break;

            case ProcessorHandoffStates.Cancelled:
                await EndAsync(handoff, ProcessorHandoffStatuses.Cancelled, $"{connection.Name} stopped work on {handoff.ReleaseName}.", cancellationToken);
                return;

            default:
                // failed, rejected, or a word Deluno does not know: the processor
                // is not going to deliver. Its own report normally closes the
                // hand-off first; this catches a report that never arrived.
                var reason = answer.Message is { Length: > 0 } said
                    ? $"{connection.Name} reports {answer.State} for {handoff.ReleaseName}: {said}"
                    : $"{connection.Name} reports {answer.State} for {handoff.ReleaseName}.";
                // #890: the processor just told Deluno the file is done for -
                // one of the two moments this is allowed to act on a partial
                // output (the other is GiveUpAsync below). Never on time alone.
                reason += await DeleteCrashedPartialOutputAsync(handoff, library, cancellationToken);
                await EndAsync(handoff, ProcessorHandoffStatuses.Failed, reason, cancellationToken);
                await RaiseRecoveryCaseAsync(
                    handoff,
                    library,
                    "processor-failed",
                    reason,
                    "Open Queue to import the original download, or retry the hand-off once the processor problem is fixed.",
                    cancellationToken);
                return;
        }

        // However politely a processor is behaving, a file it has held for days
        // is a problem for a person (#572).
        //
        // Every rule below measures from the processor's own last change, or
        // from it answering again after being unreachable - so a processor that
        // goes quiet for one pass and comes back resets the clock to zero, and
        // one that keeps rescheduling, keeps saying "working", or attaches a
        // note to "queued" is exempt from the rules entirely. Each of those
        // holds a file for the whole of a long run with nothing on any screen
        // saying so, because a hand-off still with a processor is deliberately
        // not counted. This is the one measurement none of that can move.
        var heldFor = now - handoff.CreatedUtc;
        if (heldFor > TimeSpan.FromTicks(limit.Ticks * HeldTooLongMultiplier))
        {
            await GiveUpAsync(
                handoff,
                library,
                ProcessorHandoffStatuses.TimedOut,
                $"{connection.Name} has had {handoff.ReleaseName} for {Describe(heldFor)} and still says {Say(answer)}. Deluno stopped waiting.",
                cancellationToken);
            return;
        }

        if (answer.State == ProcessorHandoffStates.Scheduled && answer.ScheduledForUtc > now)
        {
            return;
        }

        // Queued with an explanation is the processor waiting on Deluno - it
        // holds a file while Deluno is mid-import - which is never a stall.
        if (answer.State == ProcessorHandoffStates.Queued && !string.IsNullOrWhiteSpace(answer.Message))
        {
            return;
        }

        var clockStart = Latest(answer.LastChangedUtc ?? handoff.UpdatedUtc, answeringSince ?? now);
        var quiet = now - clockStart;
        if (quiet <= limit)
        {
            return;
        }

        if (answer.State == ProcessorHandoffStates.Working)
        {
            // It says it is working. It will not be interrupted mid-run, so the
            // most Deluno can do is say, once, that nothing has moved.
            if (handoff.FailureMessage is null)
            {
                var notice = $"{connection.Name} has been working on {handoff.ReleaseName} for {Describe(quiet)} with no change reported.";
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, handoff.Status, null, null, notice, cancellationToken);
                await activityFeed.RecordActivityAsync(
                    "processing.no-progress",
                    notice,
                    Details(handoff, library, answer.State),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
            }

            return;
        }

        if (canAsk.CanCancel)
        {
            var cancel = await processorConnections.CancelHandoffAsync(connection, handoff.Id, cancellationToken);
            if (cancel.Answer is ProcessorAnswer.Busy or ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden)
            {
                // Started in the meantime, or could not be told: waiting on is
                // the only answer that cannot import a file twice.
                return;
            }
        }

        await GiveUpAsync(
            handoff,
            library,
            ProcessorHandoffStatuses.Stalled,
            $"{handoff.ReleaseName}: {connection.Name} reported no change for {Describe(quiet)} ({Say(answer)}).",
            cancellationToken);
    }

    /// <summary>
    /// A processor that could not be asked. The file is not given up on: the
    /// record says when the silence started, and the no-progress clock restarts
    /// from the moment it answers again.
    /// </summary>
    private async Task ObserveSilenceAsync(
        ProcessorHandoffItem handoff,
        ProcessorConnectionItem connection,
        ProcessorAnswer answer,
        string? message,
        DateTimeOffset now,
        ISet<string> connectionsNoticed,
        CancellationToken cancellationToken)
    {
        var previous = handoff.Processor;
        var silentSince = previous?.UnreachableSinceUtc ?? now;
        await processorRepository.RecordProcessorObservationAsync(
            handoff.Id,
            new ProcessorHandoffObservation(
                previous?.State,
                message,
                previous?.QueuePosition,
                previous?.ScheduledForUtc,
                previous?.LastChangedUtc,
                now,
                silentSince,
                null),
            cancellationToken);

        var justCrossed = now - silentSince >= UnreachableNoticeAfter &&
                          (previous?.CheckedUtc is null || previous.CheckedUtc - silentSince < UnreachableNoticeAfter);
        var forbidden = answer == ProcessorAnswer.Forbidden;
        if ((forbidden || justCrossed) && connectionsNoticed.Add(connection.Id))
        {
            var text = forbidden
                ? message ?? $"{connection.Name} will not describe its hand-offs to Deluno."
                : $"{connection.Name} has not answered for {Describe(now - silentSince)}. Deluno is still waiting for the files it handed over and will not give up on them while it cannot ask.";
            await processorRepository.RecordProcessorConnectionHealthAsync(
                connection.Id,
                forbidden ? "degraded" : "unreachable",
                text,
                cancellationToken);
            if (!forbidden || previous?.Message != message)
            {
                await activityFeed.RecordActivityAsync(
                    forbidden ? "processing.status-forbidden" : "processing.processor-unreachable",
                    text,
                    JsonSerializer.Serialize(new { ConnectionId = connection.Id, connection.Name, HandoffId = handoff.Id }),
                    null,
                    "processor-connection",
                    connection.Id,
                    cancellationToken);
            }
        }
    }

    private async Task RecordLostAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string processorName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The grace window (NotFoundGrace) is measured between exactly these
        // two instants -- the hand-off's last real change, and now -- so this
        // says the two moments the grace period itself compares, in the same
        // words the person asked for on the cleanup sheet: "asked 14:00 and
        // 14:15" (#929).
        var firstAsked = FormatClock(handoff.UpdatedUtc);
        var lastAsked = FormatClock(now);
        var summary = $"{processorName} says it never received {handoff.ReleaseName} (asked {firstAsked} and {lastAsked}).";
        await EndAsync(handoff, ProcessorHandoffStatuses.Lost, summary, cancellationToken);
        await RaiseRecoveryCaseAsync(
            handoff,
            library,
            "processor-lost",
            summary,
            "Import the original, hand it to the processor again, or search for another release.",
            cancellationToken);
    }

    /// <summary>
    /// The library's failure rule, applied. "Import original" hands the untouched
    /// download to the ordinary import path; the other two leave it for a person
    /// and say what to do.
    /// </summary>
    private async Task GiveUpAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string endStatus,
        string summary,
        CancellationToken cancellationToken)
    {
        // #890: giving up is one of the only two moments (the other is the
        // processor reporting failure outright, above) a partial output is
        // allowed to be treated as abandoned - never by time alone, only
        // because the hand-off itself is ending right here.
        summary += await DeleteCrashedPartialOutputAsync(handoff, library, cancellationToken);

        if (string.Equals(library.ProcessorFailureMode, "import-original", StringComparison.OrdinalIgnoreCase))
        {
            await EndAsync(
                handoff,
                ProcessorHandoffStatuses.Released,
                $"{summary} {library.Name} imports the original when processing stalls, so Deluno is importing it unprocessed.",
                cancellationToken);
            return;
        }

        await EndAsync(handoff, endStatus, summary, cancellationToken);
        await RaiseRecoveryCaseAsync(
            handoff,
            library,
            endStatus == ProcessorHandoffStatuses.TimedOut ? "processor-timeout" : "processor-stalled",
            summary,
            string.Equals(library.ProcessorFailureMode, "manual-review", StringComparison.OrdinalIgnoreCase)
                ? "Open Queue to import the original, retry the hand-off, or dismiss it."
                : "Check the processor, then retry the hand-off. Queue can import the original if it is acceptable.",
            cancellationToken);
    }

    /// <summary>
    /// #890/#953 (cleanup sheet F7): deletes a processor's crashed, partial
    /// output, and everything else it left scattered around that hand-off's
    /// own output folder, once the hand-off is actually over - never on time
    /// alone. Called from exactly the two places a hand-off is allowed to
    /// end that way: the processor reporting failure outright, and
    /// <see cref="GiveUpAsync"/>. A complete file sitting unclaimed is a
    /// different problem (#891/#929/#921) and is left alone here, together
    /// with whatever folder holds it.
    /// </summary>
    /// <returns>
    /// A sentence to fold into the same timeout/failure entry, or empty when
    /// there was nothing to delete.
    /// </returns>
    private async Task<string> DeleteCrashedPartialOutputAsync(ProcessorHandoffItem handoff, LibraryItem library, CancellationToken cancellationToken)
    {
        var summary = string.Empty;
        var path = handoff.OutputPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            if (ProcessorOutputReadiness.IsReady(path))
            {
                // A complete file is not what crashed - leave it for the loose-
                // output safety net to name instead of deleting it here.
            }
            else
            {
                try
                {
                    File.Delete(path);
                    summary += " Deleted the processor's partial output so a re-run starts clean.";
                }
                catch (Exception exception)
                {
                    summary += $" Its partial output could not be deleted: {exception.Message}";
                }
            }
        }

        // The single output path above only ever names one file. A
        // folder-shaped hand-off (#915's documented shape) can leave other
        // scraps and now-empty subfolders alongside it, under this hand-off's
        // own working folder -- clean those up too, but never a complete file
        // #921's Health row still offers.
        var scrapsRemoved = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            WorkPlanner.ExpectedOutputDirectoryNames(handoff.SourcePath),
            library.ProcessorOutputPath,
            ProcessorOutputReadiness.IsReady);
        if (scrapsRemoved > 0)
        {
            summary += scrapsRemoved == 1
                ? " Cleared 1 other leftover item the processor left behind."
                : $" Cleared {scrapsRemoved} other leftover items the processor left behind.";
        }

        return summary;
    }

    private async Task EndAsync(ProcessorHandoffItem handoff, string status, string message, CancellationToken cancellationToken)
    {
        await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, status, null, null, message, cancellationToken);
        await activityFeed.RecordActivityAsync(
            $"processing.{status}",
            message,
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath, Status = status }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
    }

    private async Task RaiseRecoveryCaseAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string kind,
        string summary,
        string recommended,
        CancellationToken cancellationToken)
    {
        var details = Details(handoff, library, handoff.Processor?.State);
        if (DelunoValueNormalizers.NormalizeMediaType(library.MediaType) == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
        else
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
    }

    private static string Details(ProcessorHandoffItem handoff, LibraryItem library, string? processorState)
        => JsonSerializer.Serialize(new
        {
            libraryId = library.Id,
            handoffId = handoff.Id,
            // The download behind it, so this case and the hand-off, the dispatch and the client's own entry
            // for the same download share a key and show once (#1167).
            clientId = handoff.ClientId,
            queueItemId = handoff.QueueItemId,
            releaseName = handoff.ReleaseName,
            sourcePath = handoff.SourcePath,
            processorState
        });

    private static DateTimeOffset Latest(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static string Say(ProcessorHandoffStatusResult answer)
        => answer.State switch
        {
            ProcessorHandoffStates.Queued when answer.QueuePosition is { } position => $"still queued, position {position}",
            ProcessorHandoffStates.Scheduled => "waiting for a scheduled run that has passed",
            _ => $"still {answer.State}"
        };

    private static string FormatClock(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm");

    internal static string Describe(TimeSpan span)
        => span.TotalHours >= 48
            ? $"{Math.Floor(span.TotalDays):0} days"
            : $"{Math.Floor(span.TotalHours):0} hours";
}
