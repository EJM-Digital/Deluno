using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Media;

/// <summary>What Try again on a recovery case did, or why it did nothing (#558).</summary>
/// <param name="Retried">Whether Deluno acted, and so the case is settled.</param>
/// <param name="Message">What happened, in words a person can act on.</param>
public sealed record RecoveryRetryOutcome(bool Retried, string Message)
{
    public static RecoveryRetryOutcome Done(string message) => new(true, message);

    public static RecoveryRetryOutcome Refused(string message) => new(false, message);
}

/// <summary>
/// Try again on a Transfers recovery case, one action per kind of case (#558).
///
/// <para>The button posted to a route that did not exist, so for as long as it
/// had been on screen it answered every press with a 404. What "again" means
/// depends on what went wrong. A download the client never showed is searched
/// for anew. A download that arrived but was not imported is imported again: a
/// fresh search would only fetch a second copy. When nothing can be done yet
/// (Weir still holds the file, or the import is still running) the answer says
/// so instead of pretending.</para>
/// </summary>
public static class RecoveryCaseRetry
{
    public static async Task<RecoveryRetryOutcome> RetryAsync(
        string failureKind,
        string? detailsJson,
        IDownloadDispatchesRepository dispatches,
        IJobQueueRepository jobs,
        IJobScheduler scheduler,
        IMediaStateRepository mediaState,
        ILibrariesRepository libraries,
        IProcessorRepository processors,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (DispatchIdIn(detailsJson) is not { } dispatchId)
        {
            return RecoveryRetryOutcome.Refused("This case does not say which download it is about, so there is nothing to try again. Dismiss it.");
        }

        var dispatch = await dispatches.GetDispatchIncludingArchivedAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return RecoveryRetryOutcome.Refused("Deluno no longer has the download this case is about. Dismiss it.");
        }

        // The catalogues keep a coarser kind than the poller writes: a download
        // the client never showed and one it showed that was never imported are
        // both "downloadFailed". What the download is now decides which it was,
        // and that is the better guide anyway.
        return failureKind switch
        {
            "downloadFailed" or "grab-timeout" or "detection-timeout" => dispatch.DetectedUtc is null
                ? await SearchAgainAsync(dispatch, dispatches, scheduler, mediaState, libraries, timeProvider, cancellationToken)
                : await HandOverAgainAsync(dispatch, processors, cancellationToken),
            "importFailed" or "import-failed" or "import-timeout" => await ImportAgainAsync(dispatch, jobs, cancellationToken),
            // #927: always ends the download and searches again, whatever
            // DetectedUtc says - a vanished download was never handed to a
            // processor, so there is nothing for HandOverAgainAsync to do.
            "vanished-downloading" => await SearchAgainAsync(dispatch, dispatches, scheduler, mediaState, libraries, timeProvider, cancellationToken),
            // #919: Checkpoint B already showed the processor's own output was
            // less than the original it was made from - "try again" here means
            // asking the same processor to do it over, not fetching a fresh
            // download of a release Checkpoint A already proved good.
            "processedFileIsLess" => await RunProcessorAgainAsync(dispatch, processors, cancellationToken),
            _ => RecoveryRetryOutcome.Refused("This needs a decision rather than another attempt. Dismiss it once it is dealt with.")
        };
    }

    /// <summary>
    /// The kinds Try again can act on: the ones a person sees it offered for.
    /// Quality, unmatched and corrupt files need a decision, not another attempt.
    /// </summary>
    public static bool CanRetry(string? failureKind)
        => failureKind is "downloadFailed" or "importFailed" or "vanished-downloading" or "processedFileIsLess";

    /// <summary>
    /// Import the original, unprocessed download instead of waiting on the
    /// processor again (#919). Offered beside Try again and Search for
    /// another release on a "processed file is less than the original" case -
    /// the only failure kind this handles, since it is the only one where
    /// Checkpoint A already proved the original itself is fine.
    /// </summary>
    public static async Task<RecoveryRetryOutcome> ImportOriginalAsync(
        string failureKind,
        string? detailsJson,
        IDownloadDispatchesRepository dispatches,
        IProcessorRepository processors,
        CancellationToken cancellationToken)
    {
        if (failureKind != "processedFileIsLess")
        {
            return RecoveryRetryOutcome.Refused("Importing the original only applies to a processed file Deluno held back. Dismiss this case if it no longer applies.");
        }

        var (resolved, refused) = await ResolveDispatchAsync(detailsJson, dispatches, cancellationToken);
        if (refused is not null || resolved is null)
        {
            return refused ?? RecoveryRetryOutcome.Refused("Deluno no longer has the download this case is about. Dismiss it.");
        }

        var dispatch = resolved;
        var handoff = await FindHandoffAsync(dispatch, processors, cancellationToken);
        if (handoff is null)
        {
            return RecoveryRetryOutcome.Refused($"Deluno cannot find the hand-off {dispatch.ReleaseName} went through, so there is no original left to import here. Import it from its row in Transfers, or dismiss this.");
        }

        if (ProcessorHandoffStatuses.IsWithProcessor(handoff.Status))
        {
            return RecoveryRetryOutcome.Refused($"{handoff.ProcessorName ?? "The processor"} still has {dispatch.ReleaseName}, so there is nothing to import yet.");
        }

        var released = await processors.UpdateProcessorHandoffAsync(
            handoff.Id,
            ProcessorHandoffStatuses.Released,
            null,
            null,
            $"Importing the original of {dispatch.ReleaseName}, unprocessed, as requested.",
            cancellationToken);
        return released is null
            ? RecoveryRetryOutcome.Refused($"Deluno could not find {dispatch.ReleaseName}'s hand-off any more. Dismiss this.")
            : RecoveryRetryOutcome.Done($"Deluno will import the original of {dispatch.ReleaseName} without processing.");
    }

    /// <summary>
    /// Search for a different release entirely (#919). Unlike #914/#929's
    /// give-up, this never blocklists the release: Checkpoint A already
    /// showed it is a good release, so nothing about it is at fault, only the
    /// processor's own copy of it.
    /// </summary>
    public static async Task<RecoveryRetryOutcome> SearchAgainWithoutBlocklistingAsync(
        string failureKind,
        string? detailsJson,
        IDownloadDispatchesRepository dispatches,
        IJobScheduler scheduler,
        IMediaStateRepository mediaState,
        ILibrariesRepository libraries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (failureKind != "processedFileIsLess")
        {
            return RecoveryRetryOutcome.Refused("Searching for another release only applies to a processed file Deluno held back. Dismiss this case if it no longer applies.");
        }

        var (dispatch, refused) = await ResolveDispatchAsync(detailsJson, dispatches, cancellationToken);
        return refused ?? await SearchAgainAsync(dispatch!, dispatches, scheduler, mediaState, libraries, timeProvider, cancellationToken);
    }

    /// <summary>
    /// Ask the same processor to do the file over (#919). Unlike the ordinary
    /// hand-off /retry action, this also accepts a hand-off the processor
    /// already <em>finished</em> -- exactly the case here, since Checkpoint B
    /// only runs once the processor has delivered something.
    /// </summary>
    private static async Task<RecoveryRetryOutcome> RunProcessorAgainAsync(
        DownloadDispatchItem dispatch,
        IProcessorRepository processors,
        CancellationToken cancellationToken)
    {
        var handoff = await FindHandoffAsync(dispatch, processors, cancellationToken);
        if (handoff is null)
        {
            return RecoveryRetryOutcome.Refused($"Deluno cannot find the hand-off {dispatch.ReleaseName} went through, so there is nothing to run again. Dismiss this.");
        }

        if (ProcessorHandoffStatuses.IsWithProcessor(handoff.Status))
        {
            return RecoveryRetryOutcome.Refused($"{handoff.ProcessorName ?? "The processor"} still has {dispatch.ReleaseName}, so there is nothing to run again until it finishes.");
        }

        var connection = await processors.FindProcessorConnectionAsync(handoff.ProcessorConnectionId, handoff.ProcessorName, cancellationToken);
        if (connection is not { IsEnabled: true })
        {
            return RecoveryRetryOutcome.Refused($"The connection to {handoff.ProcessorName ?? "the processor"} is unavailable. Restore and test it before running {dispatch.ReleaseName} again.");
        }

        var retried = await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, cancellationToken);
        return retried is null
            ? RecoveryRetryOutcome.Refused($"Deluno could not find {dispatch.ReleaseName}'s hand-off any more. Dismiss this.")
            : RecoveryRetryOutcome.Done($"Deluno will run {dispatch.ReleaseName} through {connection.Name} again.");
    }

    /// <summary>The hand-off <paramref name="dispatch"/> went through, if Deluno still has it -- same lookup as the ordinary "hand it over again" retry.</summary>
    private static async Task<ProcessorHandoffItem?> FindHandoffAsync(
        DownloadDispatchItem dispatch,
        IProcessorRepository processors,
        CancellationToken cancellationToken)
        => (await processors.ListProcessorHandoffsAsync(dispatch.LibraryId, 250, cancellationToken))
            .Where(item => string.Equals(item.ClientId, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(item.QueueItemId, dispatch.TorrentHashOrItemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefault();

    private static async Task<(DownloadDispatchItem? Dispatch, RecoveryRetryOutcome? Refused)> ResolveDispatchAsync(
        string? detailsJson,
        IDownloadDispatchesRepository dispatches,
        CancellationToken cancellationToken)
    {
        if (DispatchIdIn(detailsJson) is not { } dispatchId)
        {
            return (null, RecoveryRetryOutcome.Refused("This case does not say which download it is about, so there is nothing Deluno can do here. Dismiss it."));
        }

        var dispatch = await dispatches.GetDispatchIncludingArchivedAsync(dispatchId, cancellationToken);
        return dispatch is null
            ? (null, RecoveryRetryOutcome.Refused("Deluno no longer has the download this case is about. Dismiss it."))
            : (dispatch, null);
    }

    /// <summary>
    /// Stop wanting it (#927): the download vanished from its client and a
    /// person decided the title itself is not wanted right now, rather than
    /// searching for another copy. Ends the download and unmonitors exactly
    /// the title the dispatch names - an episode, not its whole series.
    /// </summary>
    public static async Task<RecoveryRetryOutcome> StopWantingAsync(
        string? detailsJson,
        IDownloadDispatchesRepository dispatches,
        IDispatchTitleUnmonitorService titleUnmonitor,
        CancellationToken cancellationToken)
    {
        if (DispatchIdIn(detailsJson) is not { } dispatchId)
        {
            return RecoveryRetryOutcome.Refused("This case does not say which download it is about, so there is nothing to stop wanting. Dismiss it.");
        }

        var dispatch = await dispatches.GetDispatchIncludingArchivedAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return RecoveryRetryOutcome.Refused("Deluno no longer has the download this case is about. Dismiss it.");
        }

        if (!titleUnmonitor.CanHandle(dispatch.EntityType))
        {
            return RecoveryRetryOutcome.Refused("Deluno cannot unmonitor this by itself. Dismiss this and adjust monitoring by hand.");
        }

        if (!string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {
            await dispatches.ArchiveDispatchAsync(dispatch.Id, "stopped-wanting", cancellationToken);
        }

        var unmonitored = await titleUnmonitor.UnmonitorAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken);
        return RecoveryRetryOutcome.Done(unmonitored
            ? $"Deluno stopped wanting {dispatch.ReleaseName} and ended the download."
            : $"Deluno ended the download; {dispatch.ReleaseName} was not being monitored any more anyway.");
    }

    /// <summary>
    /// The client never showed the download, so it is over: end it, put its
    /// title back on the work list, and search for the title now.
    /// </summary>
    private static async Task<RecoveryRetryOutcome> SearchAgainAsync(
        DownloadDispatchItem dispatch,
        IDownloadDispatchesRepository dispatches,
        IJobScheduler scheduler,
        IMediaStateRepository mediaState,
        ILibrariesRepository libraries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var library = (await libraries.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.Ordinal));
        if (library is null)
        {
            return RecoveryRetryOutcome.Refused("The library this download was for no longer exists. Dismiss this.");
        }

        var now = timeProvider.GetUtcNow();
        if (!string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {
            await dispatches.ArchiveDispatchAsync(dispatch.Id, "retried", cancellationToken);
        }

        // An episode has no downloading mark; a movie or a series does, and while
        // it stands the search would pass the title over.
        MediaKind? kind = dispatch.EntityType switch
        {
            "movie" => MediaKind.Movie,
            "series" => MediaKind.Series,
            _ => null
        };
        if (kind is { } titleKind)
        {
            await mediaState.SetDownloadingAsync(titleKind, dispatch.EntityId, downloading: false, now, cancellationToken);
        }

        var mediaType = string.Equals(dispatch.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movies";
        await scheduler.EnqueueAsync(
            new EnqueueJobRequest(
                LibrarySearchJobTypes.For(mediaType),
                "recovery-case.retry",
                JsonSerializer.Serialize(new
                {
                    libraryId = library.Id,
                    libraryName = library.Name,
                    mediaType,
                    checkMissing = true,
                    checkUpgrades = true,
                    maxItems = 1,
                    retryDelayHours = library.RetryDelayHours,
                    triggeredBy = "recovery-retry",
                    targetEntityId = dispatch.EntityId
                }),
                dispatch.EntityType,
                dispatch.EntityId,
                now),
            cancellationToken);

        return RecoveryRetryOutcome.Done($"Deluno ended the download {dispatch.DownloadClientName} never showed and is searching for {dispatch.ReleaseName} again.");
    }

    /// <summary>The download arrived and its import failed or stalled: run that import again.</summary>
    private static async Task<RecoveryRetryOutcome> ImportAgainAsync(
        DownloadDispatchItem dispatch,
        IJobQueueRepository jobs,
        CancellationToken cancellationToken)
    {
        var requeued = await jobs.RetryFailedImportAsync(dispatch.Id, cancellationToken);
        if (requeued > 0)
        {
            return RecoveryRetryOutcome.Done($"Deluno is importing {dispatch.ReleaseName} again.");
        }

        if (string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
        {
            return RecoveryRetryOutcome.Done($"{dispatch.ReleaseName} was imported after all, so nothing needs doing again.");
        }

        return RecoveryRetryOutcome.Refused(string.Equals(dispatch.ImportStatus, "pending", StringComparison.OrdinalIgnoreCase)
            ? $"The import of {dispatch.ReleaseName} is still queued or running, so there is nothing to try again yet."
            : $"Deluno no longer has a failed import of {dispatch.ReleaseName} to try again. Import it from its row in Transfers, or dismiss this.");
    }

    /// <summary>
    /// The download arrived and was never imported. Where a processor gave up on
    /// it, it goes to the processor again; where the processor still has it,
    /// there is nothing to do yet but wait.
    /// </summary>
    private static async Task<RecoveryRetryOutcome> HandOverAgainAsync(
        DownloadDispatchItem dispatch,
        IProcessorRepository processors,
        CancellationToken cancellationToken)
    {
        var handoff = (await processors.ListProcessorHandoffsAsync(dispatch.LibraryId, 250, cancellationToken))
            .Where(item => string.Equals(item.ClientId, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(item.QueueItemId, dispatch.TorrentHashOrItemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefault();
        if (handoff is null)
        {
            return RecoveryRetryOutcome.Refused($"{dispatch.ReleaseName} never reached an import Deluno can run again. Import it from its row in Transfers, or dismiss this.");
        }

        var processor = handoff.ProcessorName ?? "The processor";
        if (ProcessorHandoffStatuses.IsWithProcessor(handoff.Status))
        {
            return RecoveryRetryOutcome.Refused($"{processor} still has {dispatch.ReleaseName}, so there is nothing to try again until it finishes or gives up.");
        }

        if (!ProcessorHandoffStatuses.CanReleaseOriginal(handoff.Status))
        {
            return RecoveryRetryOutcome.Refused($"{processor} has finished with {dispatch.ReleaseName}; its import follows without anything more from you.");
        }

        var connection = await processors.FindProcessorConnectionAsync(handoff.ProcessorConnectionId, handoff.ProcessorName, cancellationToken);
        if (connection is not { IsEnabled: true })
        {
            return RecoveryRetryOutcome.Refused($"The connection to {processor} is unavailable. Restore and test it before trying {dispatch.ReleaseName} again.");
        }

        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Waiting, null, null, null, cancellationToken);
        return RecoveryRetryOutcome.Done($"Deluno will hand {dispatch.ReleaseName} to {connection.Name} again.");
    }

    private static string? DispatchIdIn(string? detailsJson)
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
                    .Where(property => string.Equals(property.Name, "DispatchId", StringComparison.OrdinalIgnoreCase)
                                       && property.Value.ValueKind == JsonValueKind.String)
                    .Select(property => property.Value.GetString())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
