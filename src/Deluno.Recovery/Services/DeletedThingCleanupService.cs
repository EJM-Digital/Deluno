using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Recovery.Services;

/// <summary>
/// One place that decides what a deleted library, download client or processor
/// leaves behind, and ends it (#567, #568, #569).
///
/// <para>It lives here because this is the only module that can see all of it:
/// downloads and jobs in Jobs, hand-offs and download health in Platform, and
/// the recovery cases in the two catalogues, reached through the composite
/// recovery handler they register themselves with.</para>
///
/// <para>Nothing here touches the blocklist. A release was refused because of
/// what it did, not because of the client it came through, and the blocklist is
/// a screen a person can clear one row at a time (DESIGN-007).</para>
/// </summary>
public sealed class DeletedThingCleanupService(
    IDownloadDispatchesRepository dispatches,
    IDispatchAlertRepository alerts,
    IProcessorRepository processors,
    IDownloadHealthRepository downloadHealth,
    IJobQueueRepository jobs,
    IActivityFeedRepository activityFeed,
    IDispatchRecoveryHandler recovery,
    IProcessorConnectionService? processorConnections = null)
    : IDeletedThingCleanup
{
    public async Task<DeletionCleanup> LibraryDeletedAsync(
        string libraryId,
        string libraryName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) return DeletionCleanup.Nothing;
        var why = $"{Name(libraryName, "The library")} was removed from Deluno";

        var cancelledJobs = await jobs.CancelPendingForRelatedEntityAsync("library", libraryId, cancellationToken);
        var downloads = await EndDownloadsAsync(
            new DispatchQueryFilter { LibraryId = libraryId, IncludeArchived = true },
            why,
            cancellationToken);
        var handoffs = await EndHandoffsAsync(
            handoff => string.Equals(handoff.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase),
            why,
            cancellationToken);
        var settled = await SettleAsync(downloads, handoffs, why, cancellationToken);

        return new DeletionCleanup(cancelledJobs, downloads.Archived, handoffs.HandoffIds.Count, settled, 0);
    }

    public async Task<DeletionCleanup> DownloadClientDeletedAsync(
        string clientId,
        string clientName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return DeletionCleanup.Nothing;
        var why = $"{Name(clientName, "The download client")} was removed from Deluno";

        var downloads = await EndDownloadsAsync(
            new DispatchQueryFilter { ClientId = clientId, IncludeArchived = true },
            why,
            cancellationToken);
        var handoffs = await EndHandoffsAsync(
            handoff => string.Equals(handoff.ClientId, clientId, StringComparison.OrdinalIgnoreCase),
            why,
            cancellationToken);
        var settled = await SettleAsync(downloads, handoffs, why, cancellationToken);
        var forgotten = await downloadHealth.ForgetDownloadClientAsync(clientId, cancellationToken);

        return new DeletionCleanup(0, downloads.Archived, handoffs.HandoffIds.Count, settled, forgotten);
    }

    public async Task<DeletionCleanup> ProcessorDeletedAsync(string processorName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(processorName)) return DeletionCleanup.Nothing;
        var why = $"{processorName} was removed from Deluno";

        var handoffs = await EndHandoffsAsync(
            handoff => string.Equals(handoff.ProcessorName, processorName, StringComparison.OrdinalIgnoreCase),
            why,
            cancellationToken);
        var settled = await SettleAsync(Downloads.None, handoffs, why, cancellationToken);

        return new DeletionCleanup(0, 0, handoffs.HandoffIds.Count, settled, 0);
    }

    /// <param name="Archived">How many were still open and were archived now.</param>
    private sealed record Downloads(IReadOnlyCollection<string> Ids, IReadOnlyCollection<string> ReleaseNames, int Archived)
    {
        public static readonly Downloads None = new([], [], 0);
    }

    /// <summary>
    /// Archives the downloads that are still open and acknowledges their alerts,
    /// and reads the archived ones too.
    ///
    /// <para>A download that can never be detected now is not a download to keep
    /// polling: the poller has no idea its client or library has gone, and would
    /// go on raising fresh cases about it. The archived ones are only read —
    /// a successful import archives its dispatch at once, and a case raised
    /// about one of those is still open and still counted.</para>
    /// </summary>
    private async Task<Downloads> EndDownloadsAsync(
        DispatchQueryFilter filter,
        string why,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var releaseNames = new HashSet<string>(StringComparer.Ordinal);
        var archived = 0;
        var pagination = new DispatchPaginationOptions { PageSize = 200 };
        do
        {
            var (items, next) = await dispatches.QueryDispatchesAsync(filter, pagination, cancellationToken);
            foreach (var dispatch in items)
            {
                ids.Add(dispatch.Id);
                if (dispatch.ReleaseName is { Length: > 0 } releaseName) releaseNames.Add(releaseName);
                if (string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)) continue;

                await dispatches.ArchiveDispatchAsync(dispatch.Id, $"{why}.", cancellationToken);
                await alerts.AcknowledgeAlertsForDispatchAsync(dispatch.Id, cancellationToken);
                archived++;
            }

            pagination = new DispatchPaginationOptions { PageSize = 200, PageToken = next };
        }
        while (pagination.PageToken is not null);

        return new Downloads(ids, releaseNames, archived);
    }

    /// <summary>
    /// Ends the hand-offs still with a processor that this deletion concerns,
    /// asking the processor to drop each file while there is still something to
    /// ask. A processor mid-run is left alone: its hand-off ends the ordinary
    /// way, when it answers.
    /// </summary>
    private async Task<ProcessorHandoffsEnded> EndHandoffsAsync(
        Func<ProcessorHandoffItem, bool> concerns,
        string why,
        CancellationToken cancellationToken)
    {
        var handoffIds = new List<string>();
        var releaseNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handoff in await processors.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken))
        {
            if (!concerns(handoff)) continue;
            var result = await ProcessorHandoffCancellation.StopWaitingAsync(
                processors,
                processorConnections,
                activityFeed,
                handoff,
                _ => $"{why}, so Deluno stopped waiting",
                cancellationToken,
                // Not "cancelled": that is a row on Transfers waiting for a
                // person, and what it was about is gone.
                ProcessorHandoffStatuses.Dismissed);
            if (result is not { Busy: false, Ended: not null }) continue;

            handoffIds.Add(handoff.Id);
            if (handoff.ReleaseName is { Length: > 0 } releaseName) releaseNames.Add(releaseName);
        }

        return new ProcessorHandoffsEnded(handoffIds, releaseNames);
    }

    private async Task<int> SettleAsync(
        Downloads downloads,
        ProcessorHandoffsEnded handoffs,
        string why,
        CancellationToken cancellationToken)
    {
        var scope = ImportRecoveryCaseScope.ForDeleted(
            downloads.Ids,
            [.. downloads.ReleaseNames.Concat(handoffs.ReleaseNames).Distinct(StringComparer.Ordinal)],
            handoffs.HandoffIds);
        if (scope.IsEmpty) return 0;
        return await recovery.HandleScopeSettledAsync(scope, $"{why}, so this is no longer something to answer.", cancellationToken);
    }

    private static string Name(string? given, string fallback)
        => string.IsNullOrWhiteSpace(given) ? fallback : given;
}
