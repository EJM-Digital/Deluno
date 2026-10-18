using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientTelemetryService(
    IPlatformSettingsRepository platformRepository,
    IDownloadHealthRepository healthRepository,
    ILibrariesRepository librariesRepository,
    IConnectionsRepository connectionsRepository,
    IJobQueueRepository jobQueueRepository,
    IDownloadClientRegistry downloadClientRegistry,
    TimeProvider timeProvider,
    IIntegrationResiliencePolicy resiliencePolicy,
    IJobScheduler jobScheduler,
    IDownloadDispatchesRepository dispatchesRepository,
    IActivityFeedRepository activityFeedRepository,
    IRecoveryHealthEvaluator healthEvaluator,
    IProcessorRepository processorRepository,
    IConnectivityMonitor connectivityMonitor,
    IBlockedReleaseRepository? blockedReleases = null,
    IDownloadHealthConnectivityCheck? connectivityCheck = null,
    IDispatchTitleUnmonitorService? titleUnmonitorService = null,
    IDispatchProblemLog? dispatchProblemLog = null,
    IDispatchRecoveryHandler? recoveryHandler = null,
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationPreferences = null,
    IDownloadSharingRepository? sharingRepository = null,
    // #990: resolves a dispatch's movie/series tags, so a tagged
    // notification connection fires for a cleanup event scoped to it.
    IMediaTagStore? mediaTagStore = null,
    // #1062: which clients have had their one look for dispatches still filed
    // under Transmission's old session numbers. A singleton in the app.
    LegacyItemIdUpgradeMemory? legacyUpgradeMemory = null,
    // #1081: the last capture, so a screen never waits on a live call. Null in a
    // service built by hand, which then reads the clients itself.
    IDownloadTelemetrySnapshotStore? snapshotStore = null,
    // #1117: where the version a client reports is kept for the Connections screens.
    IConnectionVersionStore? connectionVersions = null,
    // The live verdict is written back to the stored client, so the list and the drawer say what the Dashboard says.
    IRealtimeEventPublisher? realtimeEventPublisher = null,
    // #1240: a person's keep-seeding categories and the moves that did not work. Null in a service built by hand, which
    // then never moves a torrent: nothing is keeping anything seeding until this is wired.
    IKeepSeedingStore? keepSeedingStore = null,
    // #1240: a tracker definition's own privacy, for a site added before Deluno stored it.
    ITrackerDefinitionNames? trackerDefinitionNames = null)
    : IDownloadClientTelemetryService
{
    private readonly LegacyItemIdUpgradeMemory legacyUpgrades = legacyUpgradeMemory ?? new LegacyItemIdUpgradeMemory();

    /// <summary>
    /// Deluno's own recovery/cleanup actions (#858) already land in Activity;
    /// this also sends them outbound, gated by the matching preference, the
    /// same shape as every other outbound dispatch. Never lets a
    /// misconfigured webhook affect the cleanup action itself.
    /// </summary>
    private async Task DispatchCleanupNotificationAsync(
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        Func<NotificationPreferences, bool> isEnabled,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? titleTags = null)
    {
        if (outboundNotificationService is null)
        {
            return;
        }

        if (notificationPreferences is not null)
        {
            var preferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (!isEnabled(preferences))
            {
                return;
            }
        }

        await outboundNotificationService.DispatchAsync(eventCategory, title, message, detailsJson, cancellationToken, titleTags);
    }

    /// <summary>
    /// #990: a dispatch's own tags, from its EntityType/EntityId when that
    /// names a movie, series or episode (an episode uses its show's tags,
    /// #1036). Null only when the dispatch names no resolvable title.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveDispatchTitleTagsAsync(string? entityType, string? entityId, CancellationToken cancellationToken)
    {
        return await mediaTagStore.ListTitleTagNamesAsync(entityType, entityId, cancellationToken);
    }

    // #824 is off by default, so most callers - including every existing
    // test - never touch this; when a caller does turn it on without wiring
    // a real check, assuming reachable is the safe failure mode (never
    // wrongly skips a cleanup pass).
    private readonly IDownloadHealthConnectivityCheck _connectivityCheck = connectivityCheck ?? AlwaysReachableConnectivityCheck.Instance;

    public async Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        // Deluno only ever sees the downloads it sent (#1062): the outside
        // items are dropped as each client's answer arrives, before anything
        // is counted, evaluated or shown, so every screen, health check and
        // API built on this overview agrees.
        var built = await BuildAnnotatedSnapshotsAsync(includeOutsideItems: false, cancellationToken);

        return new DownloadTelemetryOverview(
            Summary: built.Summary,
            Clients: built.Snapshots,
            CapturedUtc: built.CapturedUtc);
    }

    /// <summary>
    /// The same Deluno-only view as <see cref="GetOverviewAsync"/>, plus the
    /// paths of the items Deluno did not send
    /// (<see cref="DownloadClientTelemetrySnapshot.OutsideSourcePaths"/>).
    /// Used only by the leftover-download scan (#958/#1062): those items are
    /// never listed, counted or acted on - no strike, progress record or
    /// action is ever based on them - but their files are really on disk and
    /// must never be swept up.
    /// </summary>
    public async Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetSnapshotsForLeftoverScanAsync(CancellationToken cancellationToken)
    {
        var built = await BuildAnnotatedSnapshotsAsync(includeOutsideItems: true, cancellationToken);
        return built.Snapshots;
    }

    /// <summary>
    /// One real pass over the clients, for the overview and the leftover scan
    /// together (#1081). The outside items are read in the same pass, so the
    /// second live pass the leftover scan used to make is gone; the overview is
    /// the same answer without the outside paths.
    /// </summary>
    public async Task<DownloadTelemetryCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        var built = await BuildAnnotatedSnapshotsAsync(includeOutsideItems: true, cancellationToken);
        var overviewClients = built.Snapshots
            .Select(snapshot => snapshot.OutsideSourcePaths.Count == 0 ? snapshot : snapshot with { OutsideSourcePaths = [] })
            .ToArray();
        return new DownloadTelemetryCapture(
            new DownloadTelemetryOverview(built.Summary, overviewClients, built.CapturedUtc),
            built.Snapshots);
    }

    public async Task<DownloadTelemetryOverview> GetLatestOverviewAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (snapshotStore is null)
        {
            return await GetOverviewAsync(cancellationToken);
        }

        var entry = await snapshotStore.GetLatestAsync(maxAge, cancellationToken);
        return entry?.Value.Overview ?? new DownloadTelemetryOverview(Summarize([]), [], timeProvider.GetUtcNow());
    }

    public async Task<DownloadTelemetryOverview> GetFreshOverviewAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
    {
        if (snapshotStore is null)
        {
            return await GetOverviewAsync(cancellationToken);
        }

        return (await snapshotStore.RefreshAsync(reuseWithin, cancellationToken)).Value.Overview;
    }

    public async Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetFreshSnapshotsForLeftoverScanAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
    {
        if (snapshotStore is null)
        {
            return await GetSnapshotsForLeftoverScanAsync(cancellationToken);
        }

        return (await snapshotStore.RefreshAsync(reuseWithin, cancellationToken)).Value.LeftoverSnapshots;
    }

    /// <summary>How many download clients are asked at the same moment.</summary>
    private const int MaxParallelClientCalls = 6;

    private static async Task<T> RunBoundedAsync<T>(SemaphoreSlim gate, Func<Task<T>> work)
    {
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(
        IReadOnlyList<DownloadClientItem> OrderedClients,
        IReadOnlyList<DownloadClientTelemetrySnapshot> Snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<DownloadDispatchItem>> SettledDispatchesByClientId,
        DownloadTelemetrySummary Summary,
        DateTimeOffset CapturedUtc)> BuildAnnotatedSnapshotsAsync(bool includeOutsideItems, CancellationToken cancellationToken)
    {
        var capturedUtc = timeProvider.GetUtcNow();
        var platformSettings = await platformRepository.GetAsync(cancellationToken);
        var downloadHealthRules = await healthRepository.GetDownloadHealthRulesAsync(platformSettings, cancellationToken);
        var downloadHealthIgnoreList = await healthRepository.ListDownloadHealthIgnoreEntriesAsync(cancellationToken);
        var contentBlocklist = await healthRepository.GetContentBlocklistSettingsAsync(cancellationToken);
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var pathMappings = await connectionsRepository.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var routeCategoriesByLibrary = await LoadRouteCategoriesAsync(libraries, cancellationToken);
        IReadOnlyList<JobQueueItem> importJobs = await jobQueueRepository.ListAsync(200, cancellationToken);
        // The hand-off is the only thing that knows a download in
        // C:\...\Downloads-Complete and a refined file in C:\...\Refined are
        // the same item; without it the queue never learns its import finished
        // (#280).
        var handoffs = await processorRepository.ListProcessorHandoffsAsync(null, 250, cancellationToken);
        // A dismissed hand-off reads as an import that ran when its job completed (#1182); the recent-jobs window
        // may no longer hold an older one, so those are fetched by id.
        var knownJobIds = importJobs.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<JobQueueItem>? extraJobs = null;
        foreach (var dismissed in handoffs.Where(item => item.Status == ProcessorHandoffStatuses.Dismissed
                     && !string.IsNullOrWhiteSpace(item.ImportJobId) && !knownJobIds.Contains(item.ImportJobId)))
        {
            if (await jobQueueRepository.GetAsync(dismissed.ImportJobId!, cancellationToken) is { } extra)
            {
                (extraJobs ??= []).Add(extra);
                knownJobIds.Add(extra.Id);
            }
        }

        if (extraJobs is not null)
        {
            importJobs = importJobs.Concat(extraJobs).ToList();
        }

        var snapshots = new List<DownloadClientTelemetrySnapshot>();
        var liveAnswered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedQueueItemIdsByClientId = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        // #1223: only a client that answered is in here, with everything it holds (queue and history). A
        // client that did not answer is absent, so its downloads are never read as gone.
        var heldItemIdsByClientId = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        var outsideByClientId = new Dictionary<string, (IReadOnlyList<DownloadQueueItem> Queue, IReadOnlyList<DownloadClientHistoryItem> History)>(StringComparer.OrdinalIgnoreCase);
        var orderedClients = clients
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Scoped per client, not one query shared across all of them (#933).
        // DownloadClientTelemetryLimits.HistoryWindow is a recent-activity cap,
        // not an archive limit, and a shared cap ordered by recency across
        // every client's dispatches together let a client that grabs often -
        // Usenet's one-dispatch-per-episode habit, say - crowd a quieter
        // torrent client's own dispatches out of the window entirely. On the
        // lab rig that hid qBittorrent's and Transmission's own downloads from
        // the Queue outright: their real dispatches existed, just past
        // position 200 in the combined list once NZBGet's volume was mixed
        // in. Fetching per client keeps each client's window its own.
        var dispatchesByClientId = new Dictionary<string, IReadOnlyList<DownloadDispatchItem>>(StringComparer.OrdinalIgnoreCase);
        var settledDispatchesByClientId = new Dictionary<string, IReadOnlyList<DownloadDispatchItem>>(StringComparer.OrdinalIgnoreCase);
        // Every client is asked at once (bounded), not one after another (#1081):
        // the live call is by far the slowest step, and one slow client must not
        // make the others wait behind it. The dispatch reads for each client run
        // beside its live call. Everything that writes stays in the ordered loop
        // below, so the results are applied exactly as before.
        var liveByClientId = new Dictionary<string, Task<DownloadClientTelemetrySnapshot?>>(StringComparer.OrdinalIgnoreCase);
        using var liveGate = new SemaphoreSlim(MaxParallelClientCalls);
        foreach (var client in orderedClients.Where(item => item.IsEnabled))
        {
            liveByClientId[client.Id] = RunBoundedAsync(liveGate, () => TryGetLiveSnapshotAsync(client, capturedUtc, cancellationToken));
        }

        var dispatchReads = orderedClients.Select(async client =>
        {
            var recent = await jobQueueRepository.ListDownloadDispatchesAsync(
                DownloadClientTelemetryLimits.HistoryWindow,
                null,
                cancellationToken,
                clientId: client.Id);
            // Housekeeping archives a finished record after a week, while the
            // torrent it describes can go on seeding for far longer.
            var settled = await jobQueueRepository.ListDownloadDispatchesAsync(
                DownloadClientTelemetryLimits.HistoryWindow,
                null,
                cancellationToken,
                includeArchived: true,
                clientId: client.Id);
            return (client.Id, recent, settled);
        }).ToArray();
        foreach (var (clientId, recent, settled) in await Task.WhenAll(dispatchReads))
        {
            dispatchesByClientId[clientId] = recent;
            settledDispatchesByClientId[clientId] = settled;
        }

        foreach (var client in orderedClients)
        {
            if (!client.IsEnabled)
            {
                snapshots.Add(CreateSnapshot(client, [], capturedUtc, "paused", "Client is disabled."));
                continue;
            }

            var liveSnapshot = await liveByClientId[client.Id];
            if (liveSnapshot is not null)
            {
                liveAnswered.Add(client.Id);
                // Ownership is asked about the ids this client actually
                // reports, archived dispatches included, so it never depends
                // on how many newer dispatches came after Deluno sent a
                // long-seeding torrent (#1062). The recent window stays as
                // the base for everything that is about recent activity.
                await UpgradeLegacyItemIdsAsync(client.Id, liveSnapshot, cancellationToken);
                var reported = await FindDispatchesForReportedIdsAsync(client.Id, liveSnapshot, cancellationToken);
                var settledForClient = MergeDispatches(
                    settledDispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>()),
                    reported);
                settledDispatchesByClientId[client.Id] = settledForClient;
                var clientDispatches = MergeDispatches(
                    dispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>()),
                    reported.Where(dispatch => !string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)));
                // Deluno only ever sees what it sent (#1062): what the client
                // holds that Deluno did not send is set aside here, before
                // any stall clock, count, strike or action can look at it.
                var split = SplitOutsideItems(liveSnapshot, settledForClient);
                // Every id the client lists, Deluno's or not: an id listed without a
                // finding reads as "seen and clean", which is what lets a strike
                // recorded for an outside download before #1062 finally clear.
                listedQueueItemIdsByClientId[client.Id] = split.WholeAnswer.Queue.Select(item => item.Id).ToArray();
                heldItemIdsByClientId[client.Id] = split.WholeAnswer.Queue.Select(item => item.Id)
                    .Concat(split.WholeAnswer.History.Select(item => item.Id))
                    .ToArray();
                if (includeOutsideItems && (split.OutsideQueue.Count > 0 || split.OutsideHistory.Count > 0))
                {
                    outsideByClientId[client.Id] = (split.OutsideQueue, split.OutsideHistory);
                }

                liveSnapshot = await ApplyDownloadProgressStallAsync(split.Own, capturedUtc, downloadHealthRules, TimeSpan.FromMinutes(Math.Clamp(platformSettings.DownloadHealthDefaultStrikeWindowMinutes, 1, 1440)), cancellationToken);
                // The vanished-download check needs everything the client
                // lists (an empty own list is not "the client lost it"), so it
                // reads the whole answer while only Deluno's own is kept.
                liveSnapshot = await RecordWhatTheClientShowsAsync(liveSnapshot, clientDispatches, cancellationToken, split.WholeAnswer);
                var withHistory = EnrichWithDispatchHistory(
                    liveSnapshot,
                    clientDispatches,
                    capturedUtc);
                var settledForThisClient = settledDispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>());
                var enriched = EnrichQueueImportState(
                    withHistory,
                    libraries,
                    clientDispatches,
                    settledForThisClient,
                    importJobs,
                    handoffs,
                    routeCategoriesByLibrary,
                    await EntitiesHoldingAFileAsync(withHistory, settledForThisClient, titleUnmonitorService, cancellationToken),
                    capturedUtc);
                snapshots.Add(DownloadClientHelpers.NormalizeSnapshotFailures(
                    await DemoteImportedWithoutItsFileAsync(
                        enriched,
                        settledDispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>()),
                        titleUnmonitorService,
                        cancellationToken,
                        handoffs,
                        importJobs)));
                continue;
            }

            var dispatchHistory = dispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>())
                .Select(dispatch => CreateDispatchHistoryItem(client, dispatch, capturedUtc))
                .ToArray();

            snapshots.Add(CreateSnapshot(
                client,
                [],
                capturedUtc,
                NormalizeHealth(client.HealthStatus),
                client.LastHealthMessage ?? "Live telemetry unavailable; showing Deluno dispatch history only.",
                dispatchHistory,
                client.LastHealthFailure));
        }

        var clientHostsById = orderedClients.ToDictionary(
            item => item.Id,
            item => string.IsNullOrWhiteSpace(item.Host) ? null : item.Host.Trim().ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase);
        var mappedSnapshots = snapshots
            .Select(snapshot => MarkDownloadsWhoseFilesAreGone(
                ApplyPathMappings(snapshot, pathMappings, clientHostsById.GetValueOrDefault(snapshot.ClientId)),
                path => File.Exists(path) || Directory.Exists(path),
                handoffs,
                settledDispatchesByClientId.GetValueOrDefault(snapshot.ClientId, Array.Empty<DownloadDispatchItem>())))
            .ToArray();

        // Ownership is decided once, here, and carried into everything below
        // it rather than re-derived per concern (cleanup sheet row M2, #933).
        // A download somebody else sent, or added by hand, still counts
        // towards the client's own free-space and connectivity health - it is
        // really there, using real disk - but it is never struck, never
        // remediated (removed, deleted, blocklisted, replacement-searched)
        // and never shown as a Queue row. Only #560's cleanup still treats it
        // as a holding file, by design (M1): that pass reads the client's
        // download folder directly, not this ownership test.
        //
        // #824: one probe per poll, shared across every client, rather than
        // once per client - the person's own URLs answer "is the internet up
        // at all", not "is this one client reachable".
        var connectivityCheckOk = !platformSettings.DownloadHealthConnectivityCheckEnabled ||
            await _connectivityCheck.AnyReachableAsync(platformSettings.DownloadHealthConnectivityCheckUrls, cancellationToken);

        // settledDispatchesByClientId (built above, per client) is what
        // ownership is matched against - it includes archived dispatches,
        // which a finished-but-still-seeding download still needs to be
        // recognised by.
        var healthAnnotatedSnapshots = await Task.WhenAll(orderedClients.Zip(mappedSnapshots, (client, snapshot) => AttachHealthFindingsAsync(
            client,
            snapshot,
            settledDispatchesByClientId.GetValueOrDefault(client.Id, Array.Empty<DownloadDispatchItem>()),
            platformSettings,
            downloadHealthRules,
            downloadHealthIgnoreList,
            connectivityCheckOk,
            contentBlocklist,
            listedQueueItemIdsByClientId.GetValueOrDefault(client.Id, Array.Empty<string>()),
            heldItemIdsByClientId.GetValueOrDefault(client.Id),
            cancellationToken)));

        // Counts are of Deluno's own downloads only (#1062).
        var overviewSummary = Summarize(healthAnnotatedSnapshots.SelectMany(snapshot => snapshot.Queue));

        IReadOnlyList<DownloadClientTelemetrySnapshot> result = healthAnnotatedSnapshots;
        if (includeOutsideItems && outsideByClientId.Count > 0)
        {
            // Only the leftover-download scan asks for these, to know which
            // files a download somebody else added is still holding. Added
            // last, path-mapped like the rest, and never evaluated.
            result = healthAnnotatedSnapshots.Select(snapshot =>
            {
                if (!outsideByClientId.TryGetValue(snapshot.ClientId, out var outside))
                {
                    return snapshot;
                }

                var mappedOutside = ApplyPathMappings(
                    snapshot with { Queue = outside.Queue, History = outside.History },
                    pathMappings,
                    clientHostsById.GetValueOrDefault(snapshot.ClientId));
                return snapshot with
                {
                    OutsideSourcePaths = mappedOutside.Queue.Select(item => item.SourcePath)
                        .Concat(mappedOutside.History.Select(item => item.SourcePath))
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => path!)
                        .ToArray()
                };
            }).ToArray();
        }

        await SyncStoredClientHealthAsync(orderedClients, healthAnnotatedSnapshots, liveAnswered, cancellationToken);

        return (orderedClients, result, settledDispatchesByClientId, overviewSummary, capturedUtc);
    }

    /// <summary>
    /// One verdict for "is this client answering": the live reading. A client whose login test passed but whose
    /// polls now fail (Deluge hung on a magnet, 9 Oct) used to stay "Healthy" in Download Clients, whose status is
    /// the last test, while the Dashboard, which reads the live telemetry, said "not answering". The live verdict
    /// is written to the stored client when it changes: a failing poll (after the grace the telemetry already gives
    /// a single timeout) marks it unreachable, and a clean poll clears an unreachable mark. A "degraded" stored
    /// by a test (for instance a Usenet client with no provider) is not touched by a clean poll.
    /// </summary>
    internal static (string Status, string Message, IntegrationFailure? Failure)? LiveHealthToStore(string? storedStatus, DownloadClientTelemetrySnapshot snapshot)
    {
        var storedUnreachable = string.Equals(storedStatus, "unreachable", StringComparison.OrdinalIgnoreCase);
        if (snapshot.LastFailure is { } liveFailure)
        {
            return storedUnreachable ? null : ("unreachable", liveFailure.Summary, liveFailure);
        }

        return storedUnreachable && string.Equals(snapshot.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
            ? ("healthy", "Answering again.", null)
            : null;
    }

    private async Task SyncStoredClientHealthAsync(
        IReadOnlyList<DownloadClientItem> clients,
        IReadOnlyList<DownloadClientTelemetrySnapshot> snapshots,
        IReadOnlySet<string> liveAnswered,
        CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            if (!client.IsEnabled || !liveAnswered.Contains(client.Id))
            {
                continue;
            }

            var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.ClientId, client.Id, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null)
            {
                continue;
            }

            var verdict = LiveHealthToStore(client.HealthStatus, snapshot);
            if (verdict is null)
            {
                continue;
            }

            var (status, message, failure) = verdict.Value;
            try
            {
                await connectionsRepository.UpdateDownloadClientHealthAsync(
                    client.Id, status, message, failure?.LegacyCategory, null, cancellationToken, failure);
                if (realtimeEventPublisher is not null)
                {
                    await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", client.Id, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Writing the verdict down is a courtesy to the other screens; the poll itself carries on.
            }
        }
    }

    /// <summary>
    /// The dispatches (archived included) behind every id this client reports
    /// in its queue and history - what ownership is decided from (#1062).
    /// </summary>
    private async Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesForReportedIdsAsync(
        string clientId,
        DownloadClientTelemetrySnapshot live,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in live.Queue)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                ids.Add(item.Id.Trim());
            }
        }

        foreach (var item in live.History)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                ids.Add(item.Id.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.ExternalId))
            {
                ids.Add(item.ExternalId.Trim());
            }
        }

        return ids.Count == 0
            ? []
            : await dispatchesRepository.FindDispatchesByClientItemIdsAsync(clientId, ids, cancellationToken);
    }

    /// <summary>
    /// A dispatch recorded before Deluno keyed Transmission on the torrent's
    /// hash holds the session's numeric id. On the first poll of each client
    /// after Deluno starts, an item that still carries that number as its old
    /// id, under the same release name as a NON-archived dispatch holding that
    /// number (case, punctuation and separators ignored), moves the dispatch to
    /// the item's hash so it stays Deluno's, and its health records with it.
    ///
    /// <para>Only that once: a session number is only good for the session that
    /// handed it out, so after the first poll a number can already name a
    /// different torrent, and nothing is ever re-bound on it. An archived
    /// dispatch with a number is never re-bound at all. Nothing is claimed that
    /// has no dispatch of its own (#1062).</para>
    ///
    /// <para><b>Known limit:</b> if Transmission itself restarts before this
    /// first poll, its numbers change, and an old in-flight grab recorded under
    /// a number stays unlinked; it then shows the existing "vanished" issue
    /// rather than being matched to a torrent that merely has the same number.</para>
    /// </summary>
    private async Task UpgradeLegacyItemIdsAsync(
        string clientId,
        DownloadClientTelemetrySnapshot live,
        CancellationToken cancellationToken)
    {
        if (!legacyUpgrades.TryBegin(clientId))
        {
            return;
        }

        var legacy = live.Queue.Where(item => !string.IsNullOrWhiteSpace(item.LegacyId)).ToArray();
        if (legacy.Length == 0)
        {
            return;
        }

        try
        {
            var found = await dispatchesRepository.FindDispatchesByClientItemIdsAsync(
                clientId, legacy.Select(item => item.LegacyId!).ToArray(), cancellationToken);
            foreach (var item in legacy)
            {
                var dispatch = found.FirstOrDefault(candidate =>
                    !string.Equals(candidate.Status, "archived", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.TorrentHashOrItemId?.Trim(), item.LegacyId!.Trim(), StringComparison.Ordinal)
                    && SameRelease(candidate.ReleaseName, item.ReleaseName));
                if (dispatch is null)
                {
                    continue;
                }

                var moved = await dispatchesRepository.ReplaceClientItemIdAsync(
                    dispatch.Id, dispatch.TorrentHashOrItemId!, item.Id, cancellationToken);
                if (moved)
                {
                    await healthRepository.MoveDownloadHealthItemIdAsync(clientId, dispatch.TorrentHashOrItemId!, item.Id, cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing is claimed meanwhile, and nothing is tried again this run.
        }
    }

    /// <summary>A release name with case, punctuation, separators and a trailing ".nzb" ignored, for comparing an item with its dispatch.</summary>
    internal static string PlainReleaseName(string? name)
    {
        var plain = string.Concat((name ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return plain.EndsWith("nzb", StringComparison.Ordinal) && plain.Length > 3 ? plain[..^3] : plain;
    }

    internal static bool SameRelease(string? dispatchName, string? itemName)
    {
        var left = PlainReleaseName(dispatchName);
        return left.Length > 0 && left == PlainReleaseName(itemName);
    }

    /// <summary>
    /// An id that is only a small number (NZBGet's, Transmission's fallback):
    /// a counter the client can reuse or reset, unlike a hash or GUID.
    /// </summary>
    internal static bool IsNumericClientId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.Trim().Length <= 12 && id.Trim().All(char.IsDigit);

    /// <summary>
    /// Whether a dispatch is proof this client item is Deluno's: its recorded id
    /// (or its own id) equals the item's. For a numeric id an ARCHIVED dispatch
    /// is proof only when the release name agrees as well, since an old number
    /// can name a different download by now. That is an additional check, never
    /// a way in by name alone (#1062).
    /// </summary>
    internal static bool DispatchProvesItem(DownloadDispatchItem dispatch, string? itemId, string? itemReleaseName)
    {
        static bool Same(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

        // Moved to keep seeding and no longer tracked (#1240): the record is history, not proof the download is Deluno's.
        if (KeptSeedingDispatches.IsReleased(dispatch))
        {
            return false;
        }

        if (Same(dispatch.Id, itemId))
        {
            return true;
        }

        if (!Same(dispatch.TorrentHashOrItemId, itemId))
        {
            return false;
        }

        return !IsNumericClientId(itemId)
            || !string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)
            || SameRelease(dispatch.ReleaseName, itemReleaseName);
    }

    // The newer read (second) wins for a dispatch that is in both.
    private static IReadOnlyList<DownloadDispatchItem> MergeDispatches(
        IEnumerable<DownloadDispatchItem> first,
        IEnumerable<DownloadDispatchItem> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<DownloadDispatchItem>();
        foreach (var dispatch in second.Concat(first))
        {
            if (seen.Add(dispatch.Id))
            {
                merged.Add(dispatch);
            }
        }

        return merged;
    }

    private readonly record struct OutsideSplit(
        DownloadClientTelemetrySnapshot Own,
        DownloadClientTelemetrySnapshot WholeAnswer,
        IReadOnlyList<DownloadQueueItem> OutsideQueue,
        IReadOnlyList<DownloadClientHistoryItem> OutsideHistory);

    /// <summary>
    /// Separates what Deluno sent from everything else the client holds
    /// (#1062). A download is Deluno's only when a dispatch Deluno recorded carries its
    /// id (see <see cref="IsDelunosOwnDownload"/>); a same-named item with no
    /// dispatch of its own is never claimed.
    /// </summary>
    private OutsideSplit SplitOutsideItems(
        DownloadClientTelemetrySnapshot live,
        IReadOnlyList<DownloadDispatchItem> settledDispatches)
    {
        var ownQueue = new List<DownloadQueueItem>(live.Queue.Count);
        var outsideQueue = new List<DownloadQueueItem>();
        foreach (var item in live.Queue)
        {
            if (!IsDelunosOwnDownload(item, settledDispatches))
            {
                outsideQueue.Add(item);
                continue;
            }

            ownQueue.Add(item);
        }

        var ownHistory = new List<DownloadClientHistoryItem>(live.History.Count);
        var outsideHistory = new List<DownloadClientHistoryItem>();
        foreach (var item in live.History)
        {
            if (IsDelunosOwnHistoryItem(item, settledDispatches))
            {
                ownHistory.Add(item);
            }
            else
            {
                outsideHistory.Add(item);
            }
        }

        var own = live with
        {
            Queue = ownQueue,
            History = ownHistory,
            Summary = Summarize(ownQueue)
        };
        return new OutsideSplit(own, live, outsideQueue, outsideHistory);
    }

    /// <summary>
    /// The client's own history row for something Deluno sent: its id, or the
    /// id it is filed under elsewhere, is the one Deluno recorded (or the
    /// dispatch's own id, for a row Deluno derived from its records). Never the
    /// release name: an item with no dispatch of its own is not Deluno's.
    /// </summary>
    internal static bool IsDelunosOwnHistoryItem(
        DownloadClientHistoryItem item,
        IReadOnlyList<DownloadDispatchItem> clientDispatches)
    {
        static bool Same(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

        return clientDispatches.Any(dispatch =>
            DispatchProvesItem(dispatch, item.Id, item.ReleaseName) ||
            (Same(dispatch.TorrentHashOrItemId, item.ExternalId)
                && DispatchProvesItem(dispatch, item.ExternalId, item.ReleaseName)));
    }

    /// <summary>
    /// Whether a client queue row is a download Deluno itself sent (cleanup
    /// sheet row M2, #933). Matched first against the dispatch Deluno
    /// recorded when it grabbed the release - the hash, NZB id or client item
    /// id the client gave back. There is no fallback by release name: a same-named
    /// item with no dispatch carrying its id was added outside Deluno and is
    /// never claimed (James, 30 Sep 2026).
    ///
    /// <para>Deliberately never falls back to category or label alone (that
    /// was the defect #933 fixed): a category is a convention a person can
    /// reuse by hand, so it proves nothing about who actually sent an item.
    /// Neither test matching is what leaves an item added by another tool, or
    /// dropped into Deluno's own category by hand, off the Queue - and,
    /// because this is the only place that reads
    /// <see cref="DownloadClientTelemetrySnapshot.Queue"/> for display, never
    /// touched by anything downstream of it either.</para>
    /// </summary>
    internal static bool IsDelunosOwnDownload(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> clientDispatches)
        => clientDispatches.Any(dispatch => DispatchProvesItem(dispatch, item.Id, item.ReleaseName));

    public async Task<DownloadOwnership> CheckOwnershipAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        // Ownership is only ever decided from Deluno's own dispatch records,
        // matched on the client's id for the item (#1062): never on anything a
        // caller says. Archived dispatches count: Deluno archives a dispatch
        // as it finishes with it, and the download can still be sitting in the
        // client.
        var dispatch = await dispatchesRepository.FindAnyDispatchByHashAsync(clientId, queueItemId, cancellationToken);
        if (dispatch is null || KeptSeedingDispatches.IsReleased(dispatch))
        {
            return new DownloadOwnership(false, NotSentMessage(queueItemId));
        }

        // A small number is a counter the client can reuse, so an ARCHIVED
        // dispatch holding one proves the item is Deluno's only when the item
        // the client shows under that number carries the same release name (an
        // additional check, never a way in by name alone).
        if (IsNumericClientId(queueItemId)
            && string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)
            && !SameRelease(dispatch.ReleaseName, await ReadClientItemNameAsync(clientId, queueItemId, cancellationToken)))
        {
            return new DownloadOwnership(false, NotSentMessage(queueItemId));
        }

        return new DownloadOwnership(true, null);
    }

    /// <summary>The release name the client itself shows for an item id, or null when it will not say.</summary>
    private async Task<string?> ReadClientItemNameAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
    {
        try
        {
            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                return null;
            }

            var snapshot = await TryGetLiveSnapshotAsync(client, timeProvider.GetUtcNow(), cancellationToken);
            var id = queueItemId.Trim();
            return snapshot?.Queue.FirstOrDefault(item => string.Equals(item.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase))?.ReleaseName
                ?? snapshot?.History.FirstOrDefault(item => string.Equals(item.Id?.Trim(), id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ExternalId?.Trim(), id, StringComparison.OrdinalIgnoreCase))?.ReleaseName;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string NotSentMessage(string queueItemId)
        => $"Deluno did not send the download \"{queueItemId}\", so it will not remove or change it. Manage it in the download client itself.";

    /// <summary>
    /// A caller-held dispatch counts as proof only when it is for this client
    /// and describes this very item: its recorded id equals the item's id. A
    /// dispatch with no id proves nothing about any item, and is never matched
    /// by release name (James, 30 Sep 2026).
    /// </summary>
    private static DownloadOwnership CheckDispatchOwnership(
        DownloadClientItem client,
        string queueItemId,
        DownloadDispatchItem dispatch)
    {
        var notSent = new DownloadOwnership(false, NotSentMessage(queueItemId));
        if (!string.Equals(dispatch.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)
            || KeptSeedingDispatches.IsReleased(dispatch))
        {
            return notSent;
        }

        return !string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId)
            && string.Equals(dispatch.TorrentHashOrItemId.Trim(), queueItemId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? new DownloadOwnership(true, null)
            : notSent;
    }

    public Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken)
        => ExecuteActionWithOwnershipAsync(clientId, request, null, cancellationToken);

    public Task<DownloadClientActionResult> ExecuteActionForDispatchAsync(
        string clientId,
        DownloadClientActionRequest request,
        DownloadDispatchItem dispatch,
        CancellationToken cancellationToken)
        => ExecuteActionWithOwnershipAsync(clientId, request, dispatch, cancellationToken);

    private async Task<DownloadClientActionResult> ExecuteActionWithOwnershipAsync(
        string clientId,
        DownloadClientActionRequest request,
        DownloadDispatchItem? dispatch,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, false, "Download client was not found.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    clientId,
                    clientId,
                    $"action:{request.Action}",
                    "notFound",
                    "Download client was not found.")
            };
        }

        var action = NormalizeAction(request.Action);
        if (action is null)
        {
            return new DownloadClientActionResult(client.Id, request.QueueItemId, request.Action, false, "Unsupported action.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{request.Action}",
                    "rejected",
                    "Unsupported action.")
            };
        }

        // #810/#933: queue priority/reorder and force-start act on a download
        // Deluno itself dispatched, never a foreign item merely sharing the
        // client — the Queue screen already only lists Deluno's own
        // downloads, but this is the backend's own guarantee of it, not only
        // a UI convention.
        // #1062: removal goes through the same check.
        if (DownloadClientActions.RequiresDelunoOwnership(action))
        {
            var ownership = dispatch is not null
                ? CheckDispatchOwnership(client, request.QueueItemId, dispatch)
                : await CheckOwnershipAsync(client.Id, request.QueueItemId, cancellationToken);
            if (!ownership.Owned)
            {
                return new DownloadClientActionResult(client.Id, request.QueueItemId, action, false, ownership.Message!)
                {
                    Refused = true,
                    Failure = IntegrationFailureFactory.FromLegacy(
                        "download-client",
                        client.Id,
                        client.Name,
                        $"action:{action}",
                        "rejected",
                        "Not a Deluno dispatch.")
                };
            }
        }

        // Queue removal used to be gated behind "Allow removing client queue
        // entries", a setting that hid removal entirely rather than asking
        // anything. Cleanup sheet J1/J2 (#924) replaced it: the Queue's remove
        // dialog is now the confirmation, asking why in plain words, and every
        // choice acts — there is no second toggle left for it to silently
        // fail against. Adapters that support deletion are still asked to
        // retain payload files wherever their client API permits it, and an
        // item owned by another downloader can still be shared or
        // cross-seeded, which is exactly what the dialog's "just stop
        // tracking it" choice is for.
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "action"),
                "action",
                MaxAttempts: 1,
                FailureThreshold: 3,
                ServiceType: "download-client",
                ServiceId: client.Id,
                ServiceName: client.Name),
            token => ExecuteActionCoreAsync(client, action, request.QueueItemId, token),
            value => value.Succeeded
                ? IntegrationResilienceOutcome.Success
                : IntegrationResilienceOutcome.RetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            return new DownloadClientActionResult(
                client.Id,
                request.QueueItemId,
                action,
                false,
                "Deluno paused queue actions for this client after repeated failures. Test the client connection before trying again.")
            {
                Failure = result.Failure ?? IntegrationFailureFactory.CircuitOpen(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    result.RetryAfterUtc)
            };
        }

        var actionResult = result.Value ?? new DownloadClientActionResult(
            client.Id,
            request.QueueItemId,
            action,
            false,
            result.FailureMessage ?? "Download client action failed.")
        {
            Failure = result.Failure
        };
        return !actionResult.Succeeded && actionResult.Failure is null
            ? actionResult with
            {
                Failure = result.Failure ?? IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    "failed",
                    actionResult.Message,
                    attempts: result.Attempts)
            }
            : actionResult;
    }

    public async Task<QueueRemovalResult> RemoveWithReasonAsync(
        string clientId,
        string queueItemId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!QueueRemovalReasons.IsKnown(reason))
        {
            return new QueueRemovalResult(clientId, queueItemId, reason, false, "Unsupported reason.", false, false, null, null)
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client", clientId, clientId, "queue:remove", "rejected", "Unsupported removal reason.")
            };
        }

        // The three plain-word choices are each one fixed combination of what a
        // removal can do, so the dialog and the flag-based API call below share
        // the one removal path.
        var options = reason switch
        {
            QueueRemovalReasons.Failed => new QueueRemovalOptions(true, true, true, false),
            QueueRemovalReasons.Unwanted => new QueueRemovalOptions(true, false, false, true),
            QueueRemovalReasons.Reclaim => new QueueRemovalOptions(true, false, false, false),
            QueueRemovalReasons.GaveUp => new QueueRemovalOptions(true, false, false, false),
            _ => new QueueRemovalOptions(false, false, false, false)
        };

        return await RemoveCoreAsync(clientId, queueItemId, reason, options, fromReason: true, cancellationToken);
    }

    public Task<QueueRemovalResult> RemoveWithOptionsAsync(
        string clientId,
        string queueItemId,
        bool removeFromClient,
        bool blocklist,
        bool skipRedownload,
        CancellationToken cancellationToken)
    {
        // Radarr's own meaning of the three flags (QueueController.Remove):
        // blocklisting marks the download failed, and that searches again
        // unless skipRedownload says not to; nothing else searches.
        var reason = blocklist
            ? QueueRemovalReasons.Failed
            : removeFromClient ? "removed" : QueueRemovalReasons.Forget;
        return RemoveCoreAsync(
            clientId,
            queueItemId,
            reason,
            new QueueRemovalOptions(removeFromClient, blocklist, blocklist && !skipRedownload, false),
            fromReason: false,
            cancellationToken);
    }

    private async Task<QueueRemovalResult> RemoveCoreAsync(
        string clientId,
        string queueItemId,
        string reason,
        QueueRemovalOptions options,
        bool fromReason,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new QueueRemovalResult(clientId, queueItemId, reason, false, "Download client was not found.", false, false, null, null)
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client", clientId, clientId, "queue:remove", "notFound", "Download client was not found.")
            };
        }

        // The dispatch is how a queue item becomes a title: the queue item
        // itself carries a release name, not an entity id. Absent for a
        // download Deluno never grabbed (added by hand, or older than this
        // dispatch), in which case only the client action below happens —
        // there is no title to blocklist for or search again.
        var ownership = await CheckOwnershipAsync(client.Id, queueItemId, cancellationToken);
        if (!ownership.Owned)
        {
            return new QueueRemovalResult(client.Id, queueItemId, reason, false, ownership.Message!, false, false, null, null)
            {
                Refused = true,
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client", client.Id, client.Name, "queue:remove", "rejected", "Not a Deluno dispatch.")
            };
        }

        var dispatch = await dispatchesRepository.FindAnyDispatchByHashAsync(client.Id, queueItemId, cancellationToken);
        var title = dispatch?.ReleaseName ?? queueItemId;

        // "Stop tracking" is Deluno's own: the client is sent nothing, not even a remove that keeps the files,
        // because the promise is that the client keeps the download untouched (#1241). Deluno only closes its
        // own records of it.
        var actionResult = options.RemoveFromClient
            ? await ExecuteActionAsync(
                client.Id,
                new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, queueItemId),
                cancellationToken)
            : null;
        if (actionResult is null)
        {
            await NoteRemovedByDelunoAsync(client, queueItemId, stoppedTracking: true, cancellationToken);
        }

        // What the Activity line may claim about the files: only "deleted" when the client (or Deluno) deleted them.
        var outcome = actionResult is null
            ? RemovalOutcome.NotTouched
            : actionResult.Succeeded && actionResult.FilesNote is null ? RemovalOutcome.Deleted : RemovalOutcome.FilesKept;

        if (actionResult is { Succeeded: false })
        {
            // The client answered but no longer holds it (removed by hand, or by an earlier pass): there is
            // nothing left to remove, so the record is closed and the removal carries on instead of failing.
            //
            // Only while Deluno still has an open record to close. A second removal of an id whose record is
            // already closed has nothing to do, and says so as a failure instead of "Done" again (#1212).
            if (!actionResult.Refused && dispatch is not null
                && (await dispatchesRepository.FindOpenDispatchesByHashAsync(client.Id, queueItemId, cancellationToken)).Count > 0
                && await ClientAnswersWithoutItemAsync(client, queueItemId, cancellationToken))
            {
                await NoteRemovedByDelunoAsync(client, queueItemId, stoppedTracking: false, cancellationToken);
                outcome = RemovalOutcome.AlreadyGone;
            }
            else
            {
                return new QueueRemovalResult(client.Id, queueItemId, reason, false, actionResult.Message, false, false, dispatch?.EntityId, dispatch?.MediaType)
                {
                    Failure = actionResult.Failure,
                    Refused = actionResult.Refused
                };
            }
        }

        var blocklisted = false;
        var searchStarted = false;
        var unmonitored = false;

        // "I don't want this title any more" (#924): stop wanting it too, so
        // an automatic search never brings back the thing just deleted. Done
        // here rather than left to the caller so an API caller that skips the
        // web client's own follow-up call gets the same outcome, and so this
        // is the one place Activity has to say it happened.
        if (options.Unmonitor && dispatch is not null && titleUnmonitorService is not null)
        {
            unmonitored = await titleUnmonitorService.UnmonitorAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken);
        }

        if (options.Blocklist && blockedReleases is not null && dispatch is not null)
        {
            await blockedReleases.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                    dispatch.ReleaseName,
                    dispatch.IndexerName,
                    dispatch.MediaType,
                    dispatch.EntityId,
                    title,
                    ImportFailurePolicy.ManualFailure,
                    "Marked as failed from the Queue.",
                    dispatch.TorrentHashOrItemId ?? queueItemId,
                    client.Id,
                    client.Name,
                    DateTimeOffset.MinValue,
                    Origin: BlockedReleaseOrigins.Manual),
                cancellationToken);
            blocklisted = true;

            if (options.Search && SupportsReplacementSearch(dispatch.EntityType))
            {
                var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                    .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
                await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
                searchStarted = true;
            }
        }

        var message = fromReason
            ? BuildQueueRemovalMessage(reason, title, client.Name, blocklisted, searchStarted, unmonitored, outcome)
            : BuildQueueRemovalMessage(title, client.Name, options.RemoveFromClient, blocklisted, searchStarted, outcome);
        if (outcome == RemovalOutcome.FilesKept && actionResult?.FilesNote is { Length: > 0 } filesNote)
        {
            message = $"{message} {filesNote}";
        }
        await activityFeedRepository.RecordActivityAsync(
            "download.queue.removed",
            message,
            null,
            null,
            dispatch is not null ? dispatch.EntityType : "download_dispatch",
            dispatch?.EntityId ?? dispatch?.Id ?? queueItemId,
            cancellationToken);
        var queueRemovalTags = await ResolveDispatchTitleTagsAsync(dispatch?.EntityType, dispatch?.EntityId, cancellationToken);
        await DispatchCleanupNotificationAsync(
            "download.queue.removed",
            $"Queue item removed: {title}",
            message,
            null,
            prefs => prefs.QueueItemRemovedEnabled,
            cancellationToken,
            queueRemovalTags);

        return new QueueRemovalResult(client.Id, queueItemId, reason, true, message, blocklisted, searchStarted, dispatch?.EntityId, dispatch?.MediaType);
    }

    /// <summary>True when the client answers a live look and shows no queue or history item with this id.</summary>
    private async Task<bool> ClientAnswersWithoutItemAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await GetLiveSnapshotCoreAsync(client, timeProvider.GetUtcNow(), cancellationToken);
            return snapshot is { LastFailure: null }
                && string.Equals(snapshot.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                && !snapshot.Queue.Any(item => string.Equals(item.Id, queueItemId, StringComparison.OrdinalIgnoreCase))
                && !snapshot.History.Any(item => string.Equals(item.Id, queueItemId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>What a removal did to the download's files, for the Activity line (#1212).</summary>
    private enum RemovalOutcome
    {
        Deleted,
        FilesKept,
        AlreadyGone,

        /// <summary>Stopped tracking: Deluno sent the client nothing, so it still has the download and its files.</summary>
        NotTouched
    }

    /// <summary>"removed from X and deleted its files", but only as far as that is true.</summary>
    private static string RemovedFrom(string clientName, RemovalOutcome outcome, string joiner)
        => outcome switch
        {
            RemovalOutcome.AlreadyGone => $"already gone from {clientName}, so nothing was deleted",
            RemovalOutcome.NotTouched => $"left in {clientName}, untouched",
            RemovalOutcome.FilesKept => $"removed from {clientName}{joiner}left its files on disk",
            _ => $"removed from {clientName}{joiner}deleted its files"
        };

    /// <summary>The Activity line for a removal asked for by flags: it says what was actually done, part by part.</summary>
    private static string BuildQueueRemovalMessage(string title, string clientName, bool removedFromClient, bool blocklisted, bool searchStarted, RemovalOutcome outcome)
    {
        var done = new List<string>
        {
            removedFromClient
                ? RemovedFrom(clientName, outcome, " and ")
                : $"stopped tracking it, and {clientName} still has it, untouched"
        };
        if (blocklisted)
        {
            done.Add("blocklisted the release");
        }

        if (searchStarted)
        {
            done.Add("started a new search");
        }

        return $"An outside program removed \"{title}\" from the Queue: {string.Join(", ", done)}.";
    }

    private static string BuildQueueRemovalMessage(string reason, string title, string clientName, bool blocklisted, bool searchStarted, bool unmonitored, RemovalOutcome outcome)
        => reason switch
        {
            QueueRemovalReasons.Failed => searchStarted
                ? $"You marked \"{title}\" as bad: {RemovedFrom(clientName, outcome, ", ")}, blocklisted the release, and started a new search."
                : $"You marked \"{title}\" as bad: {RemovedFrom(clientName, outcome, ", ")}, and blocklisted the release.",
            QueueRemovalReasons.Unwanted => unmonitored
                ? $"You said you don't want \"{title}\" any more: {RemovedFrom(clientName, outcome, ", ")}, and stopped wanting it."
                : $"You said you don't want \"{title}\" any more: {RemovedFrom(clientName, outcome, " and ")}.",
            QueueRemovalReasons.GaveUp => $"You gave up on \"{title}\": {RemovedFrom(clientName, outcome, " and ")}.",
            QueueRemovalReasons.Reclaim => outcome == RemovalOutcome.Deleted
                ? $"You removed the leftover copy of \"{title}\" from {clientName} and deleted its files. The library file was not touched."
                : $"You removed the leftover copy of \"{title}\": {RemovedFrom(clientName, outcome, " and ")}. The library file was not touched.",
            _ => $"You told Deluno to stop tracking \"{title}\"; {clientName} still has it, untouched."
        };

    private async Task<DownloadClientTelemetrySnapshot?> TryGetLiveSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "telemetry"),
                "telemetry",
                FailureThreshold: 2,
                ServiceType: "download-client",
                ServiceId: client.Id,
                ServiceName: client.Name),
            token => GetLiveSnapshotCoreAsync(client, capturedUtc, token),
            value => value is null
                ? IntegrationResilienceOutcome.NonRetryableFailure
                : value.HealthStatus == "healthy"
                    ? IntegrationResilienceOutcome.Success
                    : IntegrationResilienceOutcome.NonRetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            return CreateSnapshot(
                client,
                [],
                capturedUtc,
                "degraded",
                "Live telemetry is temporarily paused after repeated connection failures.",
                failure: result.Failure ?? IntegrationFailureFactory.CircuitOpen(
                    "download-client",
                    client.Id,
                    client.Name,
                    "telemetry",
                    result.RetryAfterUtc));
        }

        var reading = result.Value ??
            (result.FailureMessage is null
                ? null
                : CreateSnapshot(
                    client,
                    [],
                    capturedUtc,
                    "degraded",
                    result.FailureMessage,
                    failure: result.Failure));
        return HoldLastGoodReading(client.Id, reading);
    }

    /// <summary>How many polls in a row must fail before the client is shown as not answering.</summary>
    internal const int PollFailuresBeforeClientError = 2;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DownloadClientTelemetrySnapshot Snapshot, int Failures)> LastGoodReadings
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A single timed-out or refused poll is not a client error (rig, 8 Oct:
    /// "Talking to Deluge ... timed out" while a download ran at 8 MB/s). The
    /// last good reading is kept and shown until the failure repeats on
    /// consecutive polls; only then does the client read as not answering. A
    /// client that has never answered has no reading to hold, so it fails at
    /// once as before.
    /// </summary>
    internal static DownloadClientTelemetrySnapshot? HoldLastGoodReading(string clientId, DownloadClientTelemetrySnapshot? reading)
    {
        var failed = reading is null
            ? false
            : reading.LastFailure is not null && string.Equals(reading.HealthStatus, "degraded", StringComparison.OrdinalIgnoreCase);
        if (reading is not null && !failed)
        {
            LastGoodReadings[clientId] = (reading, 0);
            return reading;
        }

        if (reading is null || !LastGoodReadings.TryGetValue(clientId, out var held))
        {
            return reading;
        }

        var failures = held.Failures + 1;
        LastGoodReadings[clientId] = (held.Snapshot, failures);
        return failures < PollFailuresBeforeClientError ? held.Snapshot : reading;
    }

    private async Task<DownloadClientActionResult> ExecuteActionCoreAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var outcome = await RunActionCoreAsync(client, action, queueItemId, cancellationToken);
        if (outcome.Succeeded && IsRemovalAction(action))
        {
            await NoteRemovedByDelunoAsync(client, queueItemId, stoppedTracking: false, cancellationToken);
        }

        return outcome;
    }

    private static bool IsRemovalAction(string action)
        => action.StartsWith("delete", StringComparison.OrdinalIgnoreCase)
           || action.StartsWith("remove", StringComparison.OrdinalIgnoreCase)
           // How the refused-download cleanup and a forced re-download remove a copy (#1167).
           || string.Equals(action, DownloadClientActions.RemoveRefusedCopy, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A removal Deluno made itself (the stall rules, Remove failed, a person
    /// pressing Remove in the Queue) is never then reported as the download
    /// having "vanished from the client" (#1167). The vanished notice is
    /// marked as already seen and nobody is waiting on it.
    /// </summary>
    private async Task NoteRemovedByDelunoAsync(
        DownloadClientItem client,
        string queueItemId,
        bool stoppedTracking,
        CancellationToken cancellationToken,
        string? movedToCategory = null)
    {
        if (string.IsNullOrWhiteSpace(queueItemId))
        {
            return;
        }

        try
        {
            // Every open record for this download closes, not only the newest: one release sent twice shares
            // its hash, and the older record left open kept the title "already downloading" (#1190).
            var openDispatches = await dispatchesRepository.FindOpenDispatchesByHashAsync(client.Id, queueItemId, cancellationToken);
            foreach (var open in openDispatches)
            {
                // Deluno took this download out of the client itself, so its record is closed with it. Left
                // open, the title kept reading "downloading ... on its way", a new pick said "already
                // downloading" and the activity row never ended (golden path, rc.30.9). Closed first, so a
                // later step that fails cannot leave the title looking busy.
                await dispatchesRepository.ArchiveDispatchAsync(
                    open.Id,
                    movedToCategory is null
                        ? $"Deluno removed this download from {client.Name}."
                        : $"Deluno moved this download to {movedToCategory} on {client.Name} so it keeps seeding.",
                    cancellationToken);

                try
                {
                    if (dispatchProblemLog is not null)
                    {
                        await dispatchProblemLog.RecordAcknowledgedAsync(open.Id, "vanished-downloading", cancellationToken);
                    }

                    // What was open about a download that is gone has nothing left to answer: the recovery cases
                    // written for it close, and a hand-off that ended badly stops asking for a decision.
                    if (recoveryHandler is not null)
                    {
                        await recoveryHandler.HandleDispatchSettledAsync(
                            open.Id,
                            movedToCategory is null
                                ? $"Deluno removed this download from {client.Name} itself."
                                : $"Deluno moved this download to {movedToCategory} on {client.Name} so it keeps seeding.",
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Bookkeeping only: the record is already closed.
                }
            }

            // The hand-off for this download closes at the same moment, whatever state it is in (#1216): one
            // that is still waiting to be sent or with the processor has nothing left to send or wait for, and
            // one that failed has nothing left to ask. Left open, the waiting-too-long check raised "never picked
            // up" for a download that was gone, forty minutes later. Found by the download, not by an open send,
            // so it also closes when an earlier removal already closed the send.
            var handoff = await processorRepository.FindProcessorHandoffForDownloadAsync(client.Id, queueItemId, cancellationToken);
            if (handoff is not null
                && (ProcessorHandoffStatuses.IsWithProcessor(handoff.Status)
                    || ProcessorHandoffStatuses.NeedsPerson(handoff.Status, handoff.FailureMessage)))
            {
                // The reason stays on the record and is what the processor is told: a person who stopped tracking
                // the download is not told "the download was gone" (#1220).
                var reason = movedToCategory is not null
                    ? ProcessorHandoffRemovalReasons.MovedToKeepSeeding(handoff.ReleaseName, movedToCategory, client.Name)
                    : stoppedTracking
                        ? ProcessorHandoffRemovalReasons.StoppedTracking(handoff.ReleaseName)
                        : ProcessorHandoffRemovalReasons.Removed(handoff.ReleaseName, client.Name);
                await processorRepository.UpdateProcessorHandoffAsync(
                    handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, reason, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "processing.dismissed",
                    $"{handoff.ReleaseName}'s hand-off to {handoff.ProcessorName ?? "the processor"} was closed, because Deluno {(movedToCategory is null ? "removed the download" : "moved the download to keep it seeding")}.",
                    System.Text.Json.JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, PreviousStatus = handoff.Status }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bookkeeping only: the removal itself already happened.
        }
    }

    private async Task<DownloadClientActionResult> RunActionCoreAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        try
        {
            return !downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? new DownloadClientActionResult(client.Id, queueItemId, action, false, $"'{client.Protocol}' is not a supported download client protocol. Supported protocols: {string.Join(", ", downloadClientRegistry.KnownProtocols)}.")
                {
                    Failure = IntegrationFailureFactory.FromLegacy(
                        "download-client",
                        client.Id,
                        client.Name,
                        $"action:{action}",
                        "configuration",
                        $"'{client.Protocol}' is not a supported download client protocol.")
                }
                : await implementation.ExecuteActionAsync(client, action, queueItemId, cancellationToken);
        }
        catch (Exception exception) when (ClientCredentials.MessageFor(client, exception) is not null)
        {
            // #1251: a key or login the client refused, said as that on every path.
            return ClientCredentials.Failure(client, queueItemId, action, ClientCredentials.MessageFor(client, exception)!);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or JsonException)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, exception.Message)
            {
                Failure = IntegrationFailureFactory.FromException(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    exception,
                    retryScheduled: true)
            };
        }
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetLiveSnapshotCoreAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? await implementation.GetSnapshotAsync(client, capturedUtc, cancellationToken)
                : null;
            if (snapshot?.ClientVersion is { Length: > 0 } version && connectionVersions is not null)
            {
                await connectionVersions.RecordAsync(
                    ConnectionKinds.DownloadClient, client.Id, Deluno.Connections.Versions.ConnectionProjects.ForDownloadClient(client.Protocol), version, cancellationToken);
            }

            return snapshot;
        }
        catch (Exception exception) when (ClientCredentials.MessageFor(client, exception) is not null)
        {
            // #1251: a key or login the client refused reads as that, not as "403".
            var refusal = ClientCredentials.MessageFor(client, exception)!;
            return CreateSnapshot(
                client,
                [],
                capturedUtc,
                "degraded",
                refusal,
                failure: IntegrationFailureFactory.FromLegacy("download-client", client.Id, client.Name, "telemetry", "auth", refusal));
        }
        catch (Exception exception) when (exception is not HttpRequestException and not TaskCanceledException and not IOException)
        {
            return CreateSnapshot(
                client,
                [],
                capturedUtc,
                "degraded",
                exception.Message,
                failure: IntegrationFailureFactory.FromException(
                    "download-client",
                    client.Id,
                    client.Name,
                    "telemetry",
                    exception,
                    retryScheduled: true));
        }
    }

    private DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null,
        IntegrationFailure? failure = null)
    {
        var normalizedQueue = (queue ?? [])
            .Select(DownloadClientHelpers.NormalizeQueueFailure)
            .ToArray();
        var historyItems = (history ?? CreateHistoryFromQueue(client, normalizedQueue, capturedUtc))
            .Select(DownloadClientHelpers.NormalizeHistoryFailure)
            .ToArray();
        return new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Capabilities: downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? implementation.Capabilities
                : new DownloadClientTelemetryCapabilities(false, false, false, false, false, false, "unknown"),
            Summary: Summarize(normalizedQueue),
            Queue: normalizedQueue,
            History: historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            CapturedUtc: capturedUtc,
            HistoryTruncated: historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow)
        {
            LastFailure = failure
        };
    }

    /// <summary>
    /// The persisted twin of <see cref="DownloadClientHelpers.ReconcileTorrentStatus"/>'s
    /// no-progress rule (#659): known size and connected peers can still mean
    /// nothing is arriving. This re-applies that same shared rule armed with
    /// each item's downloaded-bytes history from
    /// <see cref="IDownloadHealthRepository.RecordDownloadProgressAsync"/>,
    /// which is restart-safe rather than an in-memory dict a restart would
    /// wipe. Every protocol client's live snapshot passes through here, so
    /// Transmission, qBittorrent and Deluge all get the same answer from the
    /// one rule.
    /// </summary>
    private async Task<DownloadClientTelemetrySnapshot> ApplyDownloadProgressStallAsync(
        DownloadClientTelemetrySnapshot snapshot,
        DateTimeOffset capturedUtc,
        IReadOnlyList<DownloadHealthRule> rules,
        TimeSpan checkInterval,
        CancellationToken cancellationToken)
    {
        if (snapshot.Queue.Count == 0)
        {
            return snapshot;
        }

        // Status text uses whichever no-progress rule is enabled, regardless
        // of its completion-range/protocol scope - it is a display hint, not
        // itself a finding (the evaluator below re-checks scope precisely).
        var noProgressRule = rules.FirstOrDefault(rule => rule.Enabled && rule.Kind == DownloadHealthRuleKinds.NoProgress);

        // Bytes are still watched even with the rule off (#659): switching it
        // on later should read "no gain since the window opened", not "no
        // gain since the setting changed" - the memory has to already be
        // there. #951: once a rule is configured, its own "reset strikes on
        // progress" switch and minimum-gain option decide whether a gain
        // moves the clock; with no enabled rule, every gain still resets it,
        // exactly as before this option existed.
        var progressByQueueItemId = await healthRepository.ObserveDownloadProgressAsync(
            snapshot.ClientId,
            snapshot.Queue.Select(item => (item.Id, item.DownloadedBytes)).ToArray(),
            cancellationToken,
            allowReset: noProgressRule?.ResetStrikesOnProgress ?? true,
            minimumGainBytes: noProgressRule?.ResetStrikesMinimumProgressBytes ?? 0);

        // A download is Stalled once a whole check interval passes with no byte gain, rule or no rule (#1193, #1194).
        var statusWindow = checkInterval;

        var reconciled = new List<DownloadQueueItem>(snapshot.Queue.Count);
        foreach (var item in snapshot.Queue)
        {
            // #901: the stall clock itself must stop during an outage. Both
            // timestamps ReconcileTorrentStatus measures from - AddedUtc for
            // the no-metadata branch, the last-progress mark for the
            // no-progress branch - are pushed forward by whatever outage time
            // they overlap before the comparison, then the real AddedUtc is
            // restored so nothing downstream sees a fabricated arrival time.
            var observed = progressByQueueItemId.TryGetValue(item.Id, out var progress) ? progress : null;
            var since = observed?.LastIncreaseUtc;
            var effectiveAddedUtc = item.AddedUtc + await connectivityMonitor.OutageOverlapAsync(item.AddedUtc, capturedUtc, cancellationToken);
            var effectiveSince = since is { } sinceValue
                ? sinceValue + await connectivityMonitor.OutageOverlapAsync(sinceValue, capturedUtc, cancellationToken)
                : (DateTimeOffset?)null;

            var reconciledItem = DownloadClientHelpers.ReconcileTorrentStatus(
                item with { AddedUtc = effectiveAddedUtc },
                capturedUtc,
                effectiveSince,
                statusWindow);
            // #1207: the No progress rule reads the bytes against the previous check, never the reported speed.
            reconciled.Add(item with { Status = reconciledItem.Status, BytesGrewSinceLastCheck = observed?.BytesGrew });
        }

        return snapshot with { Queue = reconciled };
    }

    /// <summary>
    /// Whether the ignore list exempts this download (#817, #1194): by hash, client category, a tracker domain (the
    /// host of the torrent's announce URL, or the indexer's name), or a tag (the client's own on the torrent, or
    /// Deluno's on the title or indexer it came from).
    /// </summary>
    private async Task<bool> IsOnTheIgnoreListAsync(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> clientDispatches,
        IReadOnlyList<DownloadHealthIgnoreEntry> ignoreList,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? indexerTags,
        CancellationToken cancellationToken)
    {
        if (ignoreList.Count == 0) return false;
        IReadOnlyList<string>? delunoTags = null;
        if (ignoreList.Any(entry => entry.NamesATag))
        {
            var dispatch = clientDispatches.FirstOrDefault(candidate => DispatchProvesItem(candidate, item.Id, item.ReleaseName));
            var tags = new List<string>();
            if (indexerTags is not null && indexerTags.TryGetValue(dispatch?.IndexerName ?? item.IndexerName, out var forIndexer)) tags.AddRange(forIndexer);
            if (dispatch is not null && await mediaTagStore.ListTitleTagNamesAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken) is { } forTitle) tags.AddRange(forTitle);
            delunoTags = tags;
        }

        return ignoreList.Any(entry => entry.Matches(item.Id, item.Category, item.IndexerName, item.Tags, item.TrackerHosts, delunoTags));
    }

    /// <summary>
    /// #1312: one client failing its file list (a refused login, a dropped connection) is "unknown" for that download, not a
    /// broken pass for every client. A refused login is already said once, by that client's snapshot.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> TryGetFilesAsync(
        IDownloadClient downloadClient,
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await downloadClient.GetFilesAsync(client, queueItemId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<DownloadClientTelemetrySnapshot> AttachHealthFindingsAsync(
        DownloadClientItem client,
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<DownloadDispatchItem> clientDispatches,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DownloadHealthRule> rules,
        IReadOnlyList<DownloadHealthIgnoreEntry> ignoreList,
        bool connectivityCheckOk,
        ContentBlocklistSettings contentBlocklist,
        IReadOnlyCollection<string> listedQueueItemIds,
        IReadOnlyCollection<string>? everythingTheClientHolds,
        CancellationToken cancellationToken)
    {
        // #816: off by default, and only fetched for a download Deluno owns
        // and is actually about to evaluate - every other item in the loop
        // below skips straight past this, so a feature nobody turned on costs
        // nothing extra per poll, and one that is on does not fetch a file
        // listing for a download nobody asked Deluno to watch.
        var downloadClient = contentBlocklist.Enabled && downloadClientRegistry.TryGet(client.Protocol, out var resolvedClient)
            ? resolvedClient
            : null;
        var strikeThreshold = settings.DownloadHealthStrikeThreshold;
        var blockReleaseAfterThreshold = settings.CleanupBlockReleaseAfterThreshold;
        var defaultStrikeWindow = TimeSpan.FromMinutes(Math.Clamp(settings.DownloadHealthDefaultStrikeWindowMinutes, 1, 1440));
        var strikeInactivityWindow = TimeSpan.FromHours(Math.Clamp(settings.DownloadHealthStrikeInactivityHours, 1, 720));
        var annotated = new List<(DownloadQueueItem Item, IReadOnlyList<RecoveryHealthFinding> Findings)>(snapshot.Queue.Count);
        // #1194: Deluno's own indexer tags are only read when an ignore entry names a tag.
        var ignoredIndexerTags = ignoreList.Any(entry => entry.NamesATag)
            ? (await connectionsRepository.ListIndexersAsync(cancellationToken)).ToDictionary(
                indexer => indexer.Name, indexer => DownloadClientHelpers.SplitTags(indexer.Tags), StringComparer.OrdinalIgnoreCase)
            : null;

        // #824: a failed connectivity check skips this poll's evaluation
        // entirely for every item on this client - not adjusted the way an
        // #901 outage adjusts elapsed time, but skipped outright, because a
        // person's own probe URLs are opt-in and its failure must never be
        // read as "every download here just started stalling". Nothing is
        // recorded this poll (no RecordDownloadHealthObservationsAsync call
        // below), so an already-accruing strike is frozen, never cleared and
        // never advanced, while it lasts.
        if (connectivityCheckOk)
        {
            foreach (var item in snapshot.Queue)
            {
                // A download nobody at Deluno sent is never struck (cleanup sheet
                // row M2, #933): no findings are evaluated for it, so it can never
                // reach the strike threshold that "Force remediation" acts on
                // below, and no observation is recorded for it either.
                //
                // #817: a download on the ignore list is treated the same way -
                // exempt from every health rule, with no finding and no strike.
                if (!IsDelunosOwnDownload(item, clientDispatches) ||
                    await IsOnTheIgnoreListAsync(item, clientDispatches, ignoreList, ignoredIndexerTags, cancellationToken))
                {
                    annotated.Add((item, []));
                    continue;
                }

                // #901: every timed rule measures "how long since AddedUtc" - an
                // outage stretches that clock exactly as long as it lasted unless
                // the added time itself is pushed forward by whatever it
                // overlapped, so a download added before an outage is not judged
                // as though it sat idle through it. Only the value fed to the
                // evaluator moves; item.AddedUtc keeps showing when it was
                // actually added.
                var effectiveAddedUtc = item.AddedUtc + await connectivityMonitor.OutageOverlapAsync(item.AddedUtc, snapshot.CapturedUtc, cancellationToken);
                // #816: only asked for once a download is actually being
                // evaluated (Deluno's own, not ignored) and the feature is on -
                // an extra HTTP round trip per download per poll that nobody
                // asked for is exactly the kind of automatic behaviour #936
                // says stays off until a person turns the check on. Null (not
                // one word about it) means "unknown", not "empty" - Evaluate
                // then falls back to matching the release name itself.
                var files = downloadClient is not null && contentBlocklist.Enabled
                    ? await TryGetFilesAsync(downloadClient, client, item.Id, cancellationToken)
                    : null;
                var findings = healthEvaluator.Evaluate(
                    new RecoveryQueueSnapshot(
                        item.Status,
                        item.ErrorMessage,
                        item.SourcePath,
                        item.SpeedMbps,
                        effectiveAddedUtc,
                        item.EtaSeconds,
                        item.ReleaseName,
                        item.Peers,
                        item.IsSetupProblem,
                        item.Progress,
                        DownloadClientHelpers.IsUsenetProtocol(item.Protocol),
                        item.IsPrivate,
                        item.TrackerStatus,
                        files,
                        item.SizeBytes,
                        snapshot.AlternateSpeedLimitEnabled == true,
                        item.IsFetchingMetadata,
                        item.Availability,
                        item.BytesGrewSinceLastCheck),
                    snapshot.CapturedUtc,
                    rules,
                    contentBlocklist);
                annotated.Add((item, findings));
            }
        }
        else
        {
            foreach (var item in snapshot.Queue)
            {
                annotated.Add((item, []));
            }
        }

        var records = Array.Empty<DownloadHealthRecord>();
        if (connectivityCheckOk)
        {
            var observations = annotated
                .SelectMany(entry => entry.Findings.Select(finding => new DownloadHealthObservation(
                    entry.Item.ClientId, entry.Item.Id, entry.Item.ReleaseName, finding.Kind, finding.Severity, finding.Evidence,
                    finding.RuleId, finding.RuleStrikeLimit, finding.RuleTimerMinutes, finding.RuleActionPreset)))
                .ToArray();
            // The full id list here (not only Deluno's own) is what lets a stale
            // strike recorded before this guard existed finally clear: an id that
            // is listed but produced no observation this pass reads as "seen and
            // clean", the same way a genuinely recovered download does.
            records = (await healthRepository.RecordDownloadHealthObservationsAsync(
                snapshot.ClientId,
                listedQueueItemIds,
                observations,
                defaultStrikeWindow,
                strikeInactivityWindow,
                cancellationToken,
                everythingTheClientHolds)).ToArray();
        }
        var recordsByFinding = records.ToDictionary(
            record => $"{record.ClientId}\u001f{record.QueueItemId}\u001f{record.Kind}",
            StringComparer.OrdinalIgnoreCase);

        // #881: a setup problem never strikes, so nothing is removed, deleted,
        // blocked or searched for. Say so once, when the spell starts, so it
        // is never a mystery why Deluno left the download alone.
        foreach (var record in records.Where(record =>
                     string.Equals(record.Kind, DownloadHealthStrikes.ClientSetupProblem, StringComparison.OrdinalIgnoreCase) &&
                     record.FirstObservedUtc == record.LastObservedUtc))
        {
            await activityFeedRepository.RecordActivityAsync(
                "download.client.setup-problem",
                $"{snapshot.ClientName} has a problem on this machine with {record.ReleaseName}: {record.Evidence} " +
                "Deluno left the download alone (nothing removed, deleted or blocked) because the release isn't at fault.",
                null,
                null,
                "download_client",
                snapshot.ClientId,
                cancellationToken);
        }

        var queue = annotated.Select(entry => entry.Item with
        {
            HealthFindings = entry.Findings.Select(finding =>
            {
                recordsByFinding.TryGetValue($"{entry.Item.ClientId}\u001f{entry.Item.Id}\u001f{finding.Kind}", out var record);
                // A rule-engine finding's own action preset decides
                // whether it can block a candidate on its own; a finding
                // outside the rule engine (client-stalled, and so on)
                // still falls back to the platform-wide switch (#905).
                var candidateBlocked = record is not null &&
                    (record.RuleActionPreset is { } preset
                        ? preset != DownloadHealthActionPresets.JustTellMe
                        : blockReleaseAfterThreshold) &&
                    record.BlocksCandidate(snapshot.CapturedUtc, strikeThreshold);
                return new DownloadHealthFinding(
                    finding.Severity,
                    finding.Kind,
                    finding.Summary,
                    finding.Evidence,
                    finding.RecommendedAction,
                    finding.CanSafelyRetry,
                    finding.CanSafelyRemove,
                    StrikeCount: record?.StrikeCount ?? 0,
                    CandidateBlocked: candidateBlocked,
                    IgnoredUntilUtc: record?.IgnoredUntilUtc,
                    RuleId: record?.RuleId,
                    RuleActionPreset: record?.RuleActionPreset,
                    NextStrikeUtc: record?.NextStrikeUtc(defaultStrikeWindow.Minutes),
                    RuleStrikeLimit: record?.RuleStrikeLimit,
                    SpellStartedUtc: record?.FirstObservedUtc);
            }).ToArray()
        }).ToArray();

        // #813: SABnzbd's own queue-level warning (disk-full, a failed
        // unpack, and the like) is not about any one release, so it never
        // goes through the strike/rule engine above -- it would otherwise
        // eventually block a candidate for a reason that has nothing to do
        // with that candidate. Folded onto every one of Deluno's own items
        // for this client instead, using the same DownloadHealthFinding
        // shape the Transfers screen's Issues view already renders, so it
        // needs no bespoke presentation there. Clearing it is the same
        // mode=warnings&name=clear call already wired from the Connections
        // screen's client row.
        if (snapshot.ClientWarnings is { Count: > 0 } clientWarnings && queue.Length > 0)
        {
            var warningText = string.Join(" ", clientWarnings);
            queue = queue.Select(item => IsDelunosOwnDownload(item, clientDispatches)
                ? item with
                {
                    HealthFindings = [
                        .. item.HealthFindings ?? [],
                        new DownloadHealthFinding(
                            Severity: "warning",
                            Kind: "sabnzbd-client-warning",
                            Summary: $"{snapshot.ClientName} reported: {warningText}",
                            Evidence: warningText,
                            RecommendedAction: "Fix it in the client, then clear this warning from this client's row on Connections.",
                            CanSafelyRetry: false,
                            CanSafelyRemove: false)
                    ]
                }
                : item).ToArray();
        }

        // #981: the sharing pass (WorkPlanner) already checked the real files
        // and knows which of this client's items lost their hardlink into the
        // library - upgraded or deleted out from under a still-seeding
        // download. Folded onto the Transfers row the same way the SABnzbd
        // warning is above, using the same DownloadHealthFinding shape, so it
        // needs no bespoke presentation and no re-derivation of what the
        // sharing pass already decided. Kept a distinct Kind from any
        // library-reconciliation finding: this download is still on disk and
        // safe to reclaim, a missing library file has nothing to reclaim from.
        if (sharingRepository is not null && queue.Length > 0)
        {
            var sharingSnapshot = await sharingRepository.GetSnapshotAsync(cancellationToken);
            var lostHardlinksByQueueItemId = sharingSnapshot.Holds
                .Where(hold => hold.LostHardlink && string.Equals(hold.ClientId, snapshot.ClientId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(hold => hold.QueueItemId, StringComparer.OrdinalIgnoreCase);
            if (lostHardlinksByQueueItemId.Count > 0)
            {
                queue = queue.Select(item => lostHardlinksByQueueItemId.TryGetValue(item.Id, out var hold)
                    ? item with
                    {
                        HealthFindings = [
                            .. item.HealthFindings ?? [],
                            new DownloadHealthFinding(
                                Severity: "warning",
                                Kind: "hardlink-lost",
                                Summary: "Unlinked from your library",
                                Evidence: hold.Detail,
                                RecommendedAction: "It's safe to reclaim: use Remove this copy below to take it out of the client and free its space. The library file is not touched.",
                                CanSafelyRetry: false,
                                CanSafelyRemove: true)
                        ]
                    }
                    : item).ToArray();
            }
        }

        return snapshot with { Queue = queue };
    }

    public async Task<DownloadCleanupPreview?> PreviewCleanupAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var overview = await GetOverviewAsync(cancellationToken);
        var item = overview.Clients
            .Where(client => string.Equals(client.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(client => client.Queue)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, queueItemId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return null;
        var settings = await platformRepository.GetAsync(cancellationToken);
        return DownloadCleanupPreviewBuilder.Create(item, settings);
    }

    public async Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(
        DownloadTelemetryOverview overview,
        CancellationToken cancellationToken)
    {
        // #901: refusing a release, removing it from the client, purging its
        // payload and queuing a replacement search are all judgements that a
        // release itself has gone bad. During an outage nothing here can tell
        // a bad release apart from every release simply being unable to make
        // progress, so the whole pass is skipped rather than acting on
        // findings it cannot trust - the connectivity banner already says
        // why, so this stays quiet rather than repeating it per client.
        var connectivity = await connectivityMonitor.GetStatusAsync(cancellationToken);
        if (connectivity.IsOutage)
        {
            return new DownloadHealthRemediationReport(0, 0, 0, 0, 0, [connectivity.Message]);
        }

        var settings = await platformRepository.GetAsync(cancellationToken);

        // #824: a second, user-opted-in reachability guard on top of #901's
        // own outage detection - a person's own probe URLs, checked before
        // every pass, with the same "skip entirely, strike nothing" outcome.
        if (settings.DownloadHealthConnectivityCheckEnabled)
        {
            var reachable = await _connectivityCheck.AnyReachableAsync(settings.DownloadHealthConnectivityCheckUrls, cancellationToken);
            if (!reachable)
            {
                const string skipMessage = "Connectivity check found no reachable URL; this pass was skipped and nothing was struck.";
                await activityFeedRepository.RecordActivityAsync(
                    "download.health.connectivity-check-skipped",
                    skipMessage,
                    null,
                    null,
                    "download_client",
                    "connectivity-check",
                    cancellationToken);
                return new DownloadHealthRemediationReport(0, 0, 0, 0, 0, [skipMessage]);
            }
        }

        var dryRun = settings.DownloadHealthDryRunEnabled;
        var rules = await healthRepository.GetDownloadHealthRulesAsync(settings, cancellationToken);
        var anyRuleActs = rules.Any(rule => rule.Enabled && rule.ActionPreset != DownloadHealthActionPresets.JustTellMe);
        var legacyActs = settings.CleanupBlockReleaseAfterThreshold ||
            settings.CleanupQueueReplacementAfterThreshold ||
            settings.CleanupRemoveClientEntryAfterThreshold ||
            settings.CleanupPurgePayloadAfterThreshold;
        if (!anyRuleActs && !legacyActs)
        {
            return new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []);
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var routeCategoriesByLibrary = await LoadRouteCategoriesAsync(libraries, cancellationToken);
        var notes = new List<string>();
        var evaluated = 0;
        var refused = 0;
        var replacements = 0;
        var removed = 0;
        var purged = 0;
        var skipped = 0;
        var moved = 0;

        // #1240: read only when a rule asks to move a private torrent, so with the feature off (a new install, an
        // upgraded one) a pass costs nothing more than it did.
        var movesAnything = keepSeedingStore is not null && rules.Any(rule => rule.Enabled && DownloadHealthPrivateActions.Moves(rule.PrivateAction));
        var keepSeeding = movesAnything ? await keepSeedingStore!.GetAsync(cancellationToken) : KeepSeedingSettings.Empty;
        var knownProblems = keepSeedingStore is null
            ? new Dictionary<string, KeepSeedingProblem>(StringComparer.OrdinalIgnoreCase)
            : (await keepSeedingStore.ListProblemsAsync(cancellationToken)).ToDictionary(problem => problem.Key, StringComparer.OrdinalIgnoreCase);
        var sites = movesAnything ? await connectionsRepository.ListIndexersAsync(cancellationToken) : [];
        var openProblems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in overview.Clients)
        {
            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, snapshot.ClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null) continue;

            foreach (var item in snapshot.Queue)
            {
                // Only a download that has stopped is struck (#560): a failed
                // import or processing run has its own rules about the release.
                // A rule-engine finding (#905) reads its own StrikeLimit here,
                // not the platform-wide fallback, so a rule set to "instant"
                // (1 strike) acts on its first sighting even while the
                // fallback threshold - used only by findings outside the
                // rule engine, like client-stalled - stays at its own value.
                // A tracker-unregistered finding (#878) carries a StrikeLimit
                // of one directly (no RuleId needed), so it strikes here on
                // its first sighting the same way.
                var struck = (item.HealthFindings ?? []).FirstOrDefault(finding =>
                    DownloadHealthStrikes.Counts(finding.Kind) &&
                    finding.StrikeCount >= Math.Clamp(finding.RuleStrikeLimit ?? settings.DownloadHealthStrikeThreshold, 1, 100) &&
                    (finding.IgnoredUntilUtc is null || finding.IgnoredUntilUtc <= overview.CapturedUtc));
                if (struck is null)
                {
                    continue;
                }

                // A finished download, or one in or past Deluno's own processing
                // (waiting on a hand-off, or failed at one), is never struck,
                // refused, removed or purged by a stall, error or no-progress
                // finding (#1154). A person, or the library's "if processing
                // fails" choice, is the only thing that acts on it.
                if (DownloadHealthStrikes.IsFinishedOrHandedOff(item.Status, item.Progress) &&
                    !DownloadHealthStrikes.MayActOnFinishedDownload(struck.Kind))
                {
                    skipped++;
                    continue;
                }

                evaluated++;
                var struckRule = struck.RuleId is null ? null : rules.FirstOrDefault(candidate => string.Equals(candidate.Id, struck.RuleId, StringComparison.OrdinalIgnoreCase));
                var dispatch = await dispatchesRepository.FindDispatchByHashAsync(client.Id, item.Id, cancellationToken);
                // A dry run never marks a dispatch handled (#814): nothing
                // really happened, so a later real pass must still be free to
                // act, and this same preview keeps showing every pass.
                if (dispatch is null || (!dryRun && (string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dispatch.ImportFailureCode, HealthRemediationIncomplete, StringComparison.OrdinalIgnoreCase))))
                {
                    skipped++;
                    continue;
                }

                var library = libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
                var routeCategory = routeCategoriesByLibrary.TryGetValue(dispatch.LibraryId, out var categories) &&
                    categories.TryGetValue(client.Id, out var configuredRouteCategory)
                    ? configuredRouteCategory
                    : null;
                var categoryOwned = IsDelunoCategory(client, item.Category, dispatch.MediaType, routeCategory);
                // #1325: a payload is deleted only while no folder overlaps a library, and never when it is, holds or sits inside a library's root.
                var payloadOwned = library is not null && IsPathWithinApprovedDownloadRoot(item.SourcePath, library.DownloadsPath)
                    && Deluno.Libraries.LibraryFolderLayout.CleanupBlockedReason(libraries) is null
                    && !Deluno.Libraries.LibraryFolderLayout.ReachesALibrary(libraries, item.SourcePath);
                var applied = new List<string>();
                var notesBefore = notes.Count;

                // #905: a rule-engine finding decides its own actions from
                // its configured preset; a finding outside the rule engine
                // (client-stalled, and so on) falls back to the platform-wide
                // switches, exactly as before #905.
                var (wantBlock, wantReplacement, wantRemove, wantPurge) = struck.RuleActionPreset is { } preset
                    ? ActionsFor(preset)
                    : (settings.CleanupBlockReleaseAfterThreshold, settings.CleanupQueueReplacementAfterThreshold, settings.CleanupRemoveClientEntryAfterThreshold, settings.CleanupPurgePayloadAfterThreshold);

                // Cleanup rules (26 Sep): a refused or remediated download is
                // removed AND its files deleted. Taking the client entry away
                // and leaving the payload behind left a 908 MB fake release on
                // disk (#1167), so removal always takes the files with it: the
                // client deletes its own data, and the residual purge below
                // sweeps what is left inside Deluno's own download root.
                if (wantRemove)
                {
                    wantPurge = true;
                }

                // A release carrying an executable or script is never left on
                // disk, whatever the switches say: once a rule that acts on it
                // fires, it is refused, removed and deleted.
                if (string.Equals(struck.Kind, DownloadHealthStrikes.DangerousFile, StringComparison.OrdinalIgnoreCase) &&
                    (wantBlock || wantReplacement || wantRemove || wantPurge))
                {
                    wantBlock = true;
                    wantRemove = true;
                    wantPurge = true;
                }

                // #1192: a stalled or slow extra copy of a release that another send is
                // downloading, or has imported, is not a reason to refuse the release,
                // delete anything or search again. The release is fine; only this copy is.
                if ((wantBlock || wantReplacement || wantRemove || wantPurge)
                    && !string.Equals(struck.Kind, DownloadHealthStrikes.DangerousFile, StringComparison.OrdinalIgnoreCase)
                    && await FindLiveSendOfSameReleaseAsync(dispatch, cancellationToken) is { } otherSend)
                {
                    wantBlock = wantReplacement = wantRemove = wantPurge = false;
                    notes.Add($"{item.ReleaseName}: left alone, because the same release is {(otherSend.ImportStatus == "imported" ? "already imported" : "still downloading")} from {otherSend.DownloadClientName}.");
                }

                // #1240: a rule set to keep a private (or tagged) torrent seeding moves it instead of removing it. It
                // takes the place of the removal: nothing is deleted on this path, and a public torrent is removed as before.
                KeepSeedingDecision? keep = null;
                IndexerItem? site = null;
                if (movesAnything
                    && (wantRemove || wantReplacement)
                    && struckRule is { Enabled: true } movingRule
                    && DownloadHealthPrivateActions.Moves(movingRule.PrivateAction)
                    && string.Equals(movingRule.ActionPreset, DownloadHealthActionPresets.Replace, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(struck.Kind, DownloadHealthStrikes.DangerousFile, StringComparison.OrdinalIgnoreCase)
                    && !DownloadClientHelpers.IsUsenetProtocol(client.Protocol))
                {
                    site = SiteOf(sites, dispatch, item);
                    var decided = KeepSeedingResolver.Decide(
                        keepSeeding, client.Id, site?.Id, item.IsPrivate,
                        site is not null && IndexerPrivacy.ExpectsSharing(EffectivePrivacy(site)));
                    if (decided.KeepsSeeding)
                    {
                        keep = decided;
                        wantRemove = false;
                        wantPurge = false;
                        wantReplacement = true;
                        wantBlock = movingRule.BlocklistWhenMoved;
                    }
                }

                var problemKey = KeepSeedingProblem.KeyFor(client.Id, item.Id);
                string? movedTo = null;
                if (keep is not null)
                {
                    var wanted = keep.Category;
                    var alreadyThere = !string.IsNullOrWhiteSpace(wanted) && ClientCategoryMove.SameCategory(item.Category, wanted);
                    if (!categoryOwned && !alreadyThere)
                    {
                        notes.Add($"{item.ReleaseName}: left alone, because its Deluno category could not be proven.");
                        skipped++;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(wanted) || IsDelunoCategory(client, wanted, dispatch.MediaType, routeCategory))
                    {
                        // A private torrent with nowhere to go is left exactly as it is: no removal, no block, no search.
                        var reason = string.IsNullOrWhiteSpace(wanted)
                            ? $"{item.ReleaseName} is from a private tracker and its rule moves it to keep seeding, but no keep-seeding category is set for it. Set one on its tag, its site or {client.Name}. Until then it is left alone."
                            : $"{item.ReleaseName} is from a private tracker and its rule moves it to keep seeding, but {wanted} is the category Deluno uses for downloads on {client.Name}. Choose a different keep-seeding category. Until then it is left alone.";
                        if (!dryRun)
                        {
                            await NoteKeepSeedingProblemAsync(knownProblems, openProblems, client, item, KeepSeedingProblemKinds.NeedsCategory, reason, wanted, cancellationToken);
                        }
                        else
                        {
                            applied.Add("would leave it alone, because no keep-seeding category is set for it");
                        }

                        notes.Add(reason);
                        skipped++;
                        continue;
                    }

                    if (dryRun)
                    {
                        applied.Add($"would move it to {wanted} on {client.Name}");
                    }
                    else
                    {
                        var moveResult = await MoveToKeepSeedingAsync(client, item.Id, wanted, item.Category, cancellationToken);
                        if (!moveResult.Succeeded)
                        {
                            // Refused: nothing else happens. No block, no search, no handled stamp, nothing deleted; the
                            // next check tries again and the reason is on System, Health until it works.
                            var reason = $"Deluno could not move {item.ReleaseName} to {wanted} in {client.Name}: {moveResult.Message} It left the download alone and will try again at the next check.";
                            await NoteKeepSeedingProblemAsync(knownProblems, openProblems, client, item, KeepSeedingProblemKinds.Refused, reason, wanted, cancellationToken);
                            notes.Add(reason);
                            skipped++;
                            continue;
                        }

                        movedTo = wanted;
                        moved++;
                        applied.Add($"moved to {wanted}");
                    }
                }

                var canRefuse = wantBlock && blockedReleases is not null;

                // "Do not select the same failed release again": on the one
                // blocklist, which searches skip, the title page counts, and the
                // blocklist screen lists and lifts. It used to be a separate list
                // only the grab checked, so the search kept choosing the release
                // and nothing on screen said why (#560). Refused before the
                // replacement is queued, so that search already skips it.
                if (canRefuse)
                {
                    if (!dryRun)
                    {
                        await blockedReleases!.BlockAsync(
                            new BlockedRelease(
                                Guid.NewGuid().ToString("n"),
                                BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                                dispatch.ReleaseName,
                                dispatch.IndexerName,
                                dispatch.MediaType,
                                dispatch.EntityId,
                                item.Title,
                                ImportFailurePolicy.DownloadStalled,
                                StalledReason(struck, client.Name, overview.CapturedUtc, struckRule),
                                dispatch.TorrentHashOrItemId ?? item.Id,
                                client.Id,
                                client.Name,
                                DateTimeOffset.MinValue),
                            cancellationToken);
                    }
                    refused++;
                    applied.Add(dryRun ? "would refuse the release" : "release refused");
                }

                if (wantReplacement && SupportsReplacementSearch(dispatch.EntityType))
                {
                    if (!dryRun)
                    {
                        await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
                    }
                    replacements++;
                    applied.Add(dryRun ? "would queue a replacement search" : "replacement search queued");
                }
                else if (wantReplacement)
                {
                    notes.Add($"{item.ReleaseName}: replacement was not queued because this dispatch is not a movie, series or episode library search.");
                }

                // The move worked, the release is blocked if the rule says so and the search is queued: only now does
                // Deluno stop tracking the torrent. It stays in the client, seeding, outside Deluno's view; its record
                // and its hand-off close with a plain reason, and the processor is told through the same outcome path.
                if (movedTo is not null)
                {
                    await NoteRemovedByDelunoAsync(client, item.Id, stoppedTracking: false, cancellationToken, movedTo);
                    openProblems.Remove(problemKey);
                    if (keepSeedingStore is not null && await keepSeedingStore.ClearProblemAsync(problemKey, cancellationToken))
                    {
                        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishDownloadHealthChangedAsync(client.Id, cancellationToken));
                    }
                }

                if (wantRemove)
                {
                    if (categoryOwned)
                    {
                        if (dryRun)
                        {
                            removed++;
                            applied.Add("would remove the client entry and delete its files");
                        }
                        else
                        {
                            // This bypasses the interactive external-client toggle only after
                            // the durable dispatch, Deluno category, and selected health policy
                            // have all matched. The public queue-action endpoint retains its
                            // stricter manual guard.
                            var result = await ExecuteOwnedRemediationRemovalAsync(client, item.Id, cancellationToken);
                            if (result.Succeeded)
                            {
                                removed++;
                                applied.Add("client entry removed with its files");
                            }
                            else
                            {
                                notes.Add($"{item.ReleaseName}: client entry was not removed: {result.Message}");
                            }
                        }
                    }
                    else
                    {
                        notes.Add($"{item.ReleaseName}: client entry was retained because its Deluno category could not be proven.");
                    }
                }

                if (wantPurge)
                {
                    var purgeable = payloadOwned && !string.IsNullOrWhiteSpace(item.SourcePath) &&
                        (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath));
                    if (dryRun && purgeable)
                    {
                        purged++;
                        applied.Add("would purge approved residual payload");
                    }
                    else if (!dryRun && payloadOwned && TryPurgePayload(item.SourcePath!))
                    {
                        purged++;
                        applied.Add("approved residual payload purged");
                    }
                    else if (!purgeable)
                    {
                        notes.Add($"{item.ReleaseName}: payload was retained because it is absent, inaccessible, or outside the configured download root.");
                    }
                }

                if (applied.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var details = JsonSerializer.Serialize(new
                {
                    clientId = client.Id,
                    queueItemId = item.Id,
                    releaseName = item.ReleaseName,
                    actions = applied,
                    categoryOwned,
                    payloadOwned,
                    dryRun,
                    movedTo,
                    keepSeedingSource = keep?.CategorySource
                });
                // #905: name the rule that caused this, in plain words, so
                // Activity never just says "something happened" - a person
                // reading it later can tell which of their own rules fired.
                var ruleReason = struck.RuleId is not null
                    ? $" ({DownloadHealthRuleValidator.DescribeKind(struck.Kind)})"
                    : string.Empty;
                var activityMessage = dryRun
                    ? $"[Dry run] Would apply failed-download handling to {item.ReleaseName}{ruleReason}: {string.Join(", ", applied)}."
                    : movedTo is not null
                        ? KeepSeedingActivity(item, client, movedTo, site, applied, struck, struckRule)
                        : $"{item.ReleaseName}{ruleReason}: {StalledReason(struck, client.Name, overview.CapturedUtc, struckRule)} {PlainRemediation(applied, client.Name)}";
                if (dryRun)
                {
                    // No timeline/import-outcome write: a dry run must leave
                    // no trace a real pass, or the person, would ever act on.
                    await activityFeedRepository.RecordActivityAsync(
                        "download.health.remediation-previewed",
                        activityMessage,
                        details,
                        null,
                        "download_dispatch",
                        dispatch.Id,
                        cancellationToken);
                    continue;
                }

                // Only a download Deluno finished handling is Activity's story. When a
                // step it meant to take could not be taken (the client entry would not
                // go, no replacement search was possible), the reason is the failure
                // and it stays on Needs action with that reason (#1167).
                var unfinished = notes.Skip(notesBefore).Where(IsUnfinishedNote).ToList();
                // A moved torrent is Deluno's no more whatever else could not finish (no replacement search, say): it is
                // closed as handled, and the record says what could not be done.
                var outcomeCode = unfinished.Count == 0 || movedTo is not null ? "health-remediation-applied" : HealthRemediationIncomplete;
                var outcomeMessage = unfinished.Count == 0
                    ? string.Join("; ", applied)
                    : string.Join("; ", applied.Concat(unfinished));
                if (movedTo is not null)
                {
                    outcomeMessage = KeptSeedingDispatches.Message(
                        dispatch.ReleaseName, movedTo, client.Name,
                        unfinished.Count == 0 ? string.Join("; ", applied.Skip(1)) : string.Join("; ", applied.Skip(1).Concat(unfinished)));
                }
                if (unfinished.Count > 0)
                {
                    activityMessage += $" It could not finish: {string.Join(" ", unfinished)}";
                }

                await dispatchesRepository.RecordTimelineEventAsync(dispatch.Id, "health-remediation-applied", details, cancellationToken);
                await dispatchesRepository.RecordImportOutcomeAsync(
                    dispatch.Id,
                    "failed",
                    null,
                    outcomeCode,
                    outcomeMessage,
                    cancellationToken,
                    failure: IntegrationFailureFactory.FromLegacy(
                        "deluno",
                        dispatch.LibraryId,
                        "Deluno health remediation",
                        "remediate",
                        "rejected",
                        outcomeMessage,
                        code: outcomeCode,
                        externalId: dispatch.Id));
                await activityFeedRepository.RecordActivityAsync(
                    "download.health.remediated",
                    activityMessage,
                    details,
                    null,
                    "download_dispatch",
                    dispatch.Id,
                    cancellationToken);
                var remediatedTags = await ResolveDispatchTitleTagsAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken);
                await DispatchCleanupNotificationAsync(
                    "download.health.remediated",
                    $"Stalled/slow download handled: {dispatch.ReleaseName}",
                    activityMessage,
                    details,
                    prefs => prefs.StalledDownloadHandledEnabled,
                    cancellationToken,
                    remediatedTags);
            }
        }

        // #1240: a problem ends on its own when its torrent recovered, left the client or was moved. Only a client that
        // answered can say so; one that did not keeps its problems.
        if (keepSeedingStore is not null && !dryRun)
        {
            var answered = overview.Clients
                .Where(snapshot => string.Equals(snapshot.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => snapshot.ClientId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var problem in knownProblems.Values)
            {
                if (answered.Contains(problem.ClientId) && !openProblems.Contains(problem.Key)
                    && await keepSeedingStore.ClearProblemAsync(problem.Key, cancellationToken))
                {
                    await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishDownloadHealthChangedAsync(problem.ClientId, cancellationToken));
                }
            }
        }

        return new DownloadHealthRemediationReport(evaluated, replacements, removed, purged, skipped, notes, refused) { TorrentsMoved = moved };
    }

    internal const string HealthRemediationIncomplete = "health-remediation-incomplete";

    private static bool IsUnfinishedNote(string note)
        => note.Contains("client entry was not removed", StringComparison.OrdinalIgnoreCase)
           || note.Contains("client entry was retained", StringComparison.OrdinalIgnoreCase)
           || note.Contains("replacement was not queued", StringComparison.OrdinalIgnoreCase);

    /// <summary>What Deluno did about a failed download, in a sentence a person can read in Activity (#1167).</summary>
    internal static string PlainRemediation(IReadOnlyList<string> applied, string clientName)
    {
        var steps = new List<string>();
        foreach (var step in applied)
        {
            if (step.Contains("refused", StringComparison.OrdinalIgnoreCase)) steps.Add("blocked this release");
            else if (step.Contains("replacement", StringComparison.OrdinalIgnoreCase)) steps.Add("is looking for another copy");
            else if (step.Contains("client entry", StringComparison.OrdinalIgnoreCase)) steps.Add($"removed it from {clientName}");
            else if (step.Contains("purge", StringComparison.OrdinalIgnoreCase)) steps.Add("deleted the leftover files");
        }

        if (steps.Count == 0) return "Deluno handled it.";
        var joined = steps.Count == 1 ? steps[0] : string.Join(", ", steps.Take(steps.Count - 1)) + " and " + steps[^1];
        return $"Deluno {joined}.";
    }

    /// <summary>
    /// Why the release was refused, for the blocklist row and the Activity line. Each strike rule says in its own plain
    /// words why it removed the download (#1206), using the person's own numbers, so no rule borrows another's
    /// sentence. Only the platform-wide fallback (no rule) says how long the client showed it stalled, because
    /// "stalled" on its own reads like a moment.
    /// </summary>
    internal static string StalledReason(DownloadHealthFinding finding, string clientName, DateTimeOffset? nowUtc = null, DownloadHealthRule? rule = null)
    {
        if (RuleReason(finding, rule) is { } ruleReason)
        {
            return $"{ruleReason}, so Deluno stopped waiting for it and will not pick it again.";
        }

        // The spell's real start says how long it has lasted. Counting strikes of the default half hour said "4 hours"
        // for a rule set to strike every minute, which had been going for fifteen.
        var lasted = finding.SpellStartedUtc is { } started && nowUtc is { } now
            ? now - started
            : DownloadHealthStrikes.DefaultWindow * Math.Max(0, finding.StrikeCount - 1);
        var forHowLong = PlainDuration(lasted) is { Length: > 0 } words ? $" for {words}" : string.Empty;
        return $"{clientName} showed it stalled or in error{forHowLong}, so Deluno stopped waiting for it and will not pick it again.";
    }

    /// <summary>
    /// One strike rule's own reason for removing a download (#1206): what the person set, and for how many checks.
    /// Null for a finding outside the rule engine, which has no rule of its own to speak for.
    /// </summary>
    internal static string? RuleReason(DownloadHealthFinding finding, DownloadHealthRule? rule)
    {
        var count = Math.Max(1, finding.RuleStrikeLimit ?? rule?.StrikeLimit ?? finding.StrikeCount);
        var checks = count == 1 ? PluralWords.Count(1, "check") : $"{PluralWords.Count(count, "check")} in a row";
        var kind = finding.Kind;
        if (string.Equals(kind, DownloadHealthStrikes.NoThroughput, StringComparison.OrdinalIgnoreCase))
        {
            return $"It made no progress for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.LowPeers, StringComparison.OrdinalIgnoreCase))
        {
            var floor = rule?.Threshold ?? 0;
            return floor > 0
                ? $"It had fewer than {floor:0.#} {(Math.Abs(floor - 1) < 0.001 ? "peer" : "peers")} for {checks}"
                : $"It had too few peers for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.LowSpeed, StringComparison.OrdinalIgnoreCase))
        {
            if (finding.Evidence.Contains("maximum total time", StringComparison.OrdinalIgnoreCase) && rule?.TooSlowMaxTotalMinutes is { } maxMinutes)
            {
                return $"It was still downloading after {PlainDuration(TimeSpan.FromMinutes(maxMinutes))} for {checks}";
            }

            var floor = rule?.Threshold ?? 0;
            return floor > 0
                ? $"It stayed under {floor:0.#} KB/s for {checks}"
                : $"It was too slow for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.DeadTorrent, StringComparison.OrdinalIgnoreCase))
        {
            return finding.Evidence.Contains("unregistered", StringComparison.OrdinalIgnoreCase)
                ? $"Its tracker said it was unregistered or removed for {checks}"
                : $"It had no seeds or peers left for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.TrackerUnregistered, StringComparison.OrdinalIgnoreCase))
        {
            return "Its tracker said it was unregistered or removed";
        }

        if (string.Equals(kind, DownloadHealthStrikes.MagnetMetadata, StringComparison.OrdinalIgnoreCase))
        {
            return $"It was still fetching its magnet metadata for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.ClientFailure, StringComparison.OrdinalIgnoreCase))
        {
            return $"Its download client reported it as stalled or in error for {checks}";
        }

        if (string.Equals(kind, DownloadHealthStrikes.DangerousFile, StringComparison.OrdinalIgnoreCase))
        {
            return "It contained a file that matches your content blocklist";
        }

        return null;
    }

    /// <summary>How long, in everyday words: nothing under a minute, then minutes, an hour, hours, days.</summary>
    internal static string PlainDuration(TimeSpan lasted)
        => lasted.TotalMinutes switch
        {
            < 1 => string.Empty,
            < 1.5 => "a minute",
            < 59.5 => PluralWords.Minutes(lasted.TotalMinutes),
            < 90 => "an hour",
            < 48 * 60 => $"{lasted.TotalHours:0.#} hours",
            _ => $"{Math.Round(lasted.TotalDays):0} days"
        };

    /// <summary>
    /// Queues the retry search that follows a failed grab, scoped to exactly
    /// what that grab covered.
    ///
    /// <para>A season-pack dispatch's <c>EntityId</c> is the series, not any
    /// one episode — the whole season shares one dispatch because the
    /// download itself belongs to no single episode. Targeting the series id
    /// used to mean the retry search's <c>targetEntityId</c> made the job
    /// <c>IsTargeted</c>, which lifts the retry window (see
    /// <c>LibrarySearchJobHandler.IgnoresRetryWindow</c>) for every missing
    /// episode of the whole series, not just the season that failed. Every
    /// one of those became due at once; the search grabbed the next single
    /// episode it found, that grab failed the same way, and its own
    /// replacement search repeated the same series-wide lift — a cascade of
    /// one grab per season inside a minute (#653, #646). The episodes this
    /// dispatch actually covered are the ones <see cref="SeasonSearchExecutor"/>
    /// recorded in its own dispatch notes; retrying each of those by its own
    /// episode id keeps the lift scoped to exactly what failed.</para>
    /// </summary>
    private Task QueueReplacementSearchAsync(
        DownloadDispatchItem dispatch,
        LibraryItem? library,
        CancellationToken cancellationToken)
        // Shared with ProcessorHandoffWatcher's and UnmatchedImportGiveUpService's
        // own "search for another release" give-up paths (#914, #929, #888), so
        // every one of them points a new search at the same title the same way.
        => ReplacementSearchQueuer.EnqueueAsync(jobScheduler, dispatch, library?.Name ?? dispatch.LibraryId, cancellationToken);

    public async Task<DownloadClientActionResult> ReclaimCompletedAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var client = clients.FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, queueItemId, "delete-with-data", false, "Download client was not found.");
        }

        return await ExecuteActionCoreAsync(client, "delete-with-data", queueItemId, cancellationToken);
    }

    public async Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null || !downloadClientRegistry.TryGet(client.Protocol, out var implementation))
        {
            return false;
        }

        bool cleared;
        try
        {
            cleared = await implementation.ClearWarningsAsync(client, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }

        if (!cleared)
        {
            return false;
        }

        await activityFeedRepository.RecordActivityAsync(
            "download.client.warnings-cleared",
            $"Cleared the warnings {client.Name} was showing, in {client.Name} itself.",
            null,
            null,
            "download_client",
            client.Id,
            cancellationToken);
        // The bell, the tray and the screens read the stored snapshot, so bring it up to date now (#1193).
        if (snapshotStore is not null)
        {
            try
            {
                await snapshotStore.RefreshAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // The warnings are cleared; the next poll shows it.
            }
        }

        return true;
    }

    public async Task<DownloadClientActionResult> ResumeClientAsync(string clientId, CancellationToken cancellationToken)
    {
        const string action = "resume-client";
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, string.Empty, action, false, "Download client was not found.");
        }

        if (!downloadClientRegistry.TryGet(client.Protocol, out var implementation))
        {
            return new DownloadClientActionResult(client.Id, string.Empty, action, false, $"{client.Name} is not a client Deluno knows how to talk to.");
        }

        ClientResumeResult outcome;
        try
        {
            outcome = await implementation.ResumeClientAsync(client, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            outcome = new ClientResumeResult(true, false, $"Deluno could not reach {client.Name} to resume it.");
        }

        if (!outcome.Supported)
        {
            return new DownloadClientActionResult(client.Id, string.Empty, action, false, $"{client.Name} does not pause as a whole, so there is nothing to resume.");
        }

        if (!outcome.Succeeded)
        {
            return new DownloadClientActionResult(client.Id, string.Empty, action, false, outcome.Message);
        }

        var message = $"Resumed {client.Name}: it is downloading again.";
        await activityFeedRepository.RecordActivityAsync(
            "download.client.resumed",
            message,
            null,
            null,
            "download_client",
            client.Id,
            cancellationToken);
        // The screen shows what the stored snapshot says, so bring it up to date now.
        if (snapshotStore is not null)
        {
            try
            {
                await snapshotStore.RefreshAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // The resume happened; the next poll shows it.
            }
        }

        return new DownloadClientActionResult(client.Id, string.Empty, action, true, message);
    }

    private Task<DownloadClientActionResult> ExecuteOwnedRemediationRemovalAsync(
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken)
        => ExecuteActionCoreAsync(client, DownloadClientActions.DeleteWithData, queueItemId, cancellationToken);

    private static bool IsDelunoCategory(DownloadClientItem client, string category, string mediaType, string? routeCategory = null)
    {
        if (!string.IsNullOrWhiteSpace(routeCategory) && string.Equals(routeCategory, category, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expected = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase)
            ? client.TvCategory
            : client.MoviesCategory;
        return !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, category, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithinApprovedDownloadRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            return !Deluno.Contracts.SafePath.HasDotSegments(path)
                && Deluno.Contracts.SafePath.IsInside(Path.GetFullPath(root), Path.GetFullPath(path), comparison: StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryPurgePayload(string sourcePath)
    {
        try
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            else if (Directory.Exists(sourcePath)) Directory.Delete(sourcePath, recursive: true);
            else return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Tell each download record what its client is showing (#554).
    ///
    /// <para>Every pass read each client's queue and history, and nothing wrote
    /// down that a download was there: only a client's completion webhook did.
    /// Without one, a download was "never shown by the client" two hours after
    /// its grab whatever the client said. On the rig, eight downloads SABnzbd had
    /// completed, and Deluno had itself handed to Weir, were failed that way and
    /// grabbed again.</para>
    /// </summary>
    internal async Task<DownloadClientTelemetrySnapshot> RecordWhatTheClientShowsAsync(
        DownloadClientTelemetrySnapshot live,
        IReadOnlyList<DownloadDispatchItem> clientDispatches,
        CancellationToken cancellationToken,
        DownloadClientTelemetrySnapshot? wholeAnswer = null)
    {
        // #927: a download the client no longer lists at all - not in the
        // queue, not in history - used to be noticed only hours later, by
        // the grab/detection timeouts ageing out. Comparing against what the
        // client actually shows, on every poll, notices it in minutes.
        var recovered = new List<DownloadQueueItem>();
        if (dispatchProblemLog is not null)
        {
            foreach (var dispatch in VanishedFromClient(wholeAnswer ?? live, clientDispatches))
            {
                try
                {
                    // Stage 2: the client had already finished with it before
                    // it disappeared (SABnzbd and NZBGet both clear finished
                    // items from their own history quickly). Deluno looks for
                    // the file where the client would have saved it, and
                    // imports it exactly like an ordinary completed download,
                    // rather than asking a person about a file that is
                    // already there.
                    var foundOnDisk = dispatch.ClientCompletedUtc is not null
                        ? await FindVanishedFileOnDiskAsync(dispatch, cancellationToken)
                        : null;
                    if (foundOnDisk is not null)
                    {
                        recovered.Add(foundOnDisk);
                        await RecordFoundOnDiskAsync(dispatch, foundOnDisk.SourcePath!, cancellationToken);
                        continue;
                    }

                    // Stage 1 (or stage 2 with nothing found on disk): only a
                    // person decides what happens next. No automatic search,
                    // no blocklist - it may have been removed on purpose.
                    await RecordClientVanishedAsync(dispatch, live.ClientName, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The screen this pass feeds matters more than one record:
                    // the next pass tries again.
                }
            }
        }

        foreach (var sighting in ShownByClient(live, clientDispatches))
        {
            try
            {
                await dispatchesRepository.RecordDetectionAsync(
                    sighting.Dispatch.Id,
                    sighting.Dispatch.TorrentHashOrItemId,
                    sighting.DownloadedBytes,
                    sighting.Finished,
                    cancellationToken);

                if (sighting.Failed)
                {
                    await RecordClientReportedFailureAsync(sighting.Dispatch, sighting.FailureReason, cancellationToken);
                }
                else if (sighting.Duplicate)
                {
                    await RecordClientDuplicateAsync(sighting.Dispatch, sighting.DuplicateReason, cancellationToken);
                }
                else if (sighting.NeedsAttention)
                {
                    await RecordClientNeedsAttentionAsync(sighting.Dispatch, sighting.AttentionReason, cancellationToken);
                }
                else if (sighting.ScriptProblem)
                {
                    await RecordClientScriptProblemAsync(sighting.Dispatch, sighting.IsScriptFailure, sighting.ScriptMessage, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The screen this pass feeds matters more than one record: the
                // next pass tries again, and the grab timeout is still there.
            }
        }

        // A file stage 2 found on disk joins this snapshot's queue exactly
        // like any other completed download, so the ordinary import pass
        // (which reads this same snapshot) hands it through catalogue naming,
        // processor routing and replacement checks unchanged - nothing here
        // duplicates that logic.
        return recovered.Count == 0 ? live : live with { Queue = live.Queue.Concat(recovered).ToArray() };
    }

    /// <summary>
    /// #927 stage 2: looks in the library's own download folder for a file or
    /// folder matching the release this dispatch grabbed - the same name
    /// matching <see cref="OrphanedDownloads.WasGrabbed"/> uses, so a client's
    /// own duplicate suffix (<c>.1</c>, <c>.2</c>) is still recognised. A
    /// synthetic queue item pointing at it, or null when nothing matches.
    /// </summary>
    private async Task<DownloadQueueItem?> FindVanishedFileOnDiskAsync(DownloadDispatchItem dispatch, CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null || string.IsNullOrWhiteSpace(library.DownloadsPath))
        {
            return null;
        }

        var grabbed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dispatch.ReleaseName.Trim() };
        string? sourcePath = null;
        try
        {
            if (!Directory.Exists(library.DownloadsPath))
            {
                return null;
            }

            foreach (var entry in new DirectoryInfo(library.DownloadsPath).EnumerateFileSystemInfos())
            {
                var name = entry is DirectoryInfo ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name);
                if (OrphanedDownloads.WasGrabbed(name, grabbed))
                {
                    sourcePath = entry.FullName;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (sourcePath is null)
        {
            return null;
        }

        return new DownloadQueueItem(
            Id: dispatch.TorrentHashOrItemId ?? dispatch.Id,
            ClientId: dispatch.DownloadClientId,
            ClientName: dispatch.DownloadClientName,
            Protocol: "unknown",
            MediaType: dispatch.MediaType,
            Title: dispatch.ReleaseName,
            ReleaseName: dispatch.ReleaseName,
            Category: library.MediaType,
            Status: DownloadQueueStatuses.Completed,
            Progress: 100,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: 0,
            DownloadedBytes: 0,
            Peers: 0,
            IndexerName: dispatch.IndexerName,
            ErrorMessage: null,
            AddedUtc: dispatch.CreatedUtc,
            SourcePath: sourcePath,
            LibraryId: library.Id);
    }

    /// <summary>
    /// Told once: the client cleared this download from its own list before
    /// Deluno imported it, but the file was still exactly where the client
    /// would have saved it, so Deluno imported it without asking (#927 stage 2).
    /// </summary>
    private async Task RecordFoundOnDiskAsync(DownloadDispatchItem dispatch, string sourcePath, CancellationToken cancellationToken)
        => await activityFeedRepository.RecordActivityOnChangeAsync(
            "download.vanished.recovered",
            $"{dispatch.ReleaseName} was no longer in {dispatch.DownloadClientName}'s list, but Deluno found its files at {sourcePath} and is importing them as normal.",
            null,
            null,
            dispatch.EntityType,
            dispatch.EntityId,
            cancellationToken);

    /// <summary>
    /// The client's own history said this release failed — NZBGet's FAILURE,
    /// DELETED or WARNING statuses, SABnzbd's Failed — read from a poll rather
    /// than a push (#647).
    ///
    /// <para>Before this, a native history failure was read exactly like a
    /// native history success: it marked the dispatch detected and completed,
    /// and nothing else. The dispatch then wore "sent" until a generic
    /// detection timeout fired hours later, with no failure reason, no
    /// blocklist entry and no replacement search — even though the client had
    /// already said, in its own words, that the download was not coming.</para>
    ///
    /// <para>This is the poll-driven twin of
    /// <see cref="DownloadClientWebhookService.HandleFailureAsync"/>: same
    /// dispatch outcome, same failure code
    /// (<see cref="DownloadClientFailureCodes.ClientReported"/>), so a
    /// dispatch either path already handled is recognised by the other rather
    /// than recorded, blocked and searched for twice.</para>
    /// </summary>
    private async Task RecordClientReportedFailureAsync(
        DownloadDispatchItem dispatch,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{dispatch.DownloadClientName} reported this download as failed."
            : reason;

        await dispatchesRepository.RecordImportOutcomeAsync(
            dispatch.Id,
            importStatus: "failed",
            importedFilePath: null,
            importFailureCode: DownloadClientFailureCodes.ClientReported,
            importFailureMessage: message,
            cancellationToken,
            failure: IntegrationFailureFactory.FromLegacy(
                "download-client",
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                "download",
                "failed",
                message,
                code: DownloadClientFailureCodes.ClientReported,
                externalId: dispatch.Id));

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "client-reported-failed",
            JsonSerializer.Serialize(new { reason = message, source = "history" }),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.failed",
            $"{dispatch.DownloadClientName} could not download {dispatch.ReleaseName}: {message}",
            null,
            null,
            "download_dispatch",
            dispatch.Id,
            cancellationToken);

        // #1192: a copy of this release that was sent to a second client fails on its own
        // terms (NZBGet and SABnzbd both wanted the same folder name). While another send
        // of the same release is downloading, or has imported, the release is not bad: it
        // is not blocklisted, no files are deleted, and no new search is queued.
        var sibling = await FindLiveSendOfSameReleaseAsync(dispatch, cancellationToken);
        if (sibling is not null)
        {
            await dispatchesRepository.RecordTimelineEventAsync(
                dispatch.Id,
                "duplicate-send-failed-kept-release",
                JsonSerializer.Serialize(new { otherDispatchId = sibling.Id, otherClient = sibling.DownloadClientName, otherImportStatus = sibling.ImportStatus }),
                cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "download.duplicate.failed",
                sibling.ImportStatus == "imported"
                    ? $"The extra copy of {dispatch.ReleaseName} in {dispatch.DownloadClientName} failed, but the release was already imported from {sibling.DownloadClientName}. Deluno kept the release and deleted nothing."
                    : $"The extra copy of {dispatch.ReleaseName} in {dispatch.DownloadClientName} failed, but the same release is still downloading in {sibling.DownloadClientName}. Deluno kept the release, deleted nothing and started no new search.",
                null,
                null,
                "download_dispatch",
                dispatch.Id,
                cancellationToken);
            return;
        }

        if (blockedReleases is not null)
        {
            // Blocked the same way a stalled torrent is: on the spot, not
            // behind a settings toggle. A queue item stalling might still
            // recover; a client that has already filed the download under
            // FAILURE, DELETED or WARNING never will.
            await blockedReleases.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                    dispatch.ReleaseName,
                    dispatch.IndexerName,
                    dispatch.MediaType,
                    dispatch.EntityId,
                    dispatch.ReleaseName,
                    ImportFailurePolicy.ClientReportedFailure,
                    message,
                    dispatch.TorrentHashOrItemId,
                    dispatch.DownloadClientId,
                    dispatch.DownloadClientName,
                    DateTimeOffset.MinValue),
                cancellationToken);
        }

        // #806: Remove failed is a per-client choice about the client's own
        // queue entry, separate from ImportFailurePolicy above, which decides
        // whether the release is blocklisted and searched again. A client
        // with the toggle off keeps the failed entry exactly as it does
        // today; nothing here changes unless the person turned this on for
        // this specific client.
        if (dispatch.TorrentHashOrItemId is { Length: > 0 })
        {
            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase));
            if (client is { RemoveFailed: true })
            {
                // Direct to the core action rather than the public
                // ExecuteActionAsync: this runs from inside a poll pass that
                // already found the failure, the same way the health
                // remediation removal above (ExecuteOwnedRemediationRemovalAsync)
                // does not go through the resilience/circuit-breaker wrapper
                // meant for a person-initiated action.
                await ExecuteActionCoreAsync(client, "delete-with-data", dispatch.TorrentHashOrItemId, cancellationToken);
            }
        }

        if (SupportsReplacementSearch(dispatch.EntityType))
        {
            var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
        }
    }

    /// <summary>
    /// The client's own duplicate check declined this release — NZBGet's
    /// DELETED/DUPE, DELETED/COPY and DELETED/GOOD, read from a poll (#663).
    ///
    /// <para><b>Not a failure, so it takes none of the failure path's
    /// consequences.</b> Nothing is blocklisted and nothing is searched for
    /// again: the client already has this download, or a copy that satisfies
    /// it, and treating that as an error is exactly the misread this method
    /// exists to avoid ("NZBGet deleted it: it was a duplicate of one NZBGet
    /// already held" used to file the release under `failed` and blocklist
    /// it).</para>
    ///
    /// <para>Where a more recent dispatch for the same title already imported
    /// successfully, this dispatch is resolved by following it — the same
    /// content already reached the library, so there is nothing left for this
    /// dispatch to do. Otherwise the dispatch is left without an import
    /// outcome (recorded only in its timeline and the activity feed), because
    /// there is no import to point it at, but it will not be reprocessed:
    /// <see cref="RecordDetectionAsync"/> already marked it detected and
    /// finished, which is what keeps <see cref="ShownByClient"/> from surfacing
    /// the same sighting again next poll.</para>
    /// </summary>
    private async Task RecordClientDuplicateAsync(
        DownloadDispatchItem dispatch,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{dispatch.DownloadClientName} already has this download."
            : reason;

        var existing = await FindExistingImportForSameEntityAsync(dispatch, cancellationToken);

        if (existing is not null)
        {
            await dispatchesRepository.RecordImportOutcomeAsync(
                dispatch.Id,
                importStatus: "imported",
                importedFilePath: existing.ImportedFilePath,
                importFailureCode: null,
                importFailureMessage: null,
                cancellationToken);
        }

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "client-reported-duplicate",
            JsonSerializer.Serialize(new { reason = message, source = "history", followedDispatchId = existing?.Id }),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.duplicate",
            existing is not null
                ? $"{dispatch.DownloadClientName} already had {dispatch.ReleaseName}; it matches an earlier import."
                : $"{dispatch.DownloadClientName} already has {dispatch.ReleaseName}: {message}",
            null,
            null,
            "download_dispatch",
            dispatch.Id,
            cancellationToken);
    }

    /// <summary>
    /// The download itself is not lost, but finishing it needs a person
    /// (#733) — NZBGet's WARNING/SPACE, where the release downloaded
    /// completely and unpacking stopped because the disk filled.
    ///
    /// <para><b>Not a failure, so it takes none of the failure path's
    /// consequences.</b> Nothing is blocklisted and nothing is searched for
    /// again: the release did not fail, it is waiting on a person to free
    /// space and re-run post-processing in NZBGet. No import outcome is
    /// recorded either — there is nothing to point an import at yet.</para>
    ///
    /// <para><b>Deliberately not "finished".</b> Unlike a duplicate or a real
    /// failure, this dispatch's <c>ClientCompletedUtc</c> is left unset (see
    /// <see cref="ShownByClient"/>): the unpacked output is not there yet, so
    /// nothing downstream should treat it as ready to import, and NZBGet
    /// reporting SUCCESS later still needs to look like a fresh completion.
    /// That also means <see cref="ShownByClient"/> keeps re-surfacing this
    /// sighting on every poll until then, so the timeline/activity write here
    /// is guarded by whether it already ran — otherwise a person watching the
    /// same full disk would see the same entry every polling pass.</para>
    /// </summary>
    private async Task RecordClientNeedsAttentionAsync(
        DownloadDispatchItem dispatch,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return;
        }

        var timeline = await dispatchesRepository.GetDispatchTimelineAsync(dispatch.Id, cancellationToken);
        if (timeline.Any(entry => entry.EventType == "client-reported-needs-attention"))
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{dispatch.DownloadClientName} finished downloading this but needs a person to finish it."
            : reason;

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "client-reported-needs-attention",
            JsonSerializer.Serialize(new { reason = message, source = "history" }),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.needs-attention",
            $"{dispatch.DownloadClientName} could not finish {dispatch.ReleaseName}: {message}",
            null,
            null,
            "download_dispatch",
            dispatch.Id,
            cancellationToken);
    }

    /// <summary>
    /// The download itself finished, but the client's own post-processing
    /// script failed or warned (#910) — NZBGet's FAILURE/SCRIPT and
    /// WARNING/SCRIPT. A problem with the client's script, not the release:
    /// never blocklisted, never searched for again, and no import outcome is
    /// recorded here — the finished file is left for the ordinary import path
    /// to probe, which imports it when it is complete and playable, or
    /// follows the bad-release path for that reason if it turns out broken
    /// (#918).
    ///
    /// <para>Shown on System &gt; Health, so it stays visible after the
    /// activity feed scrolls past it, as well as in Activity — naming the
    /// script where the client reports which one it was. A soft warning is
    /// recorded too, at a lower severity, rather than silently read as a
    /// plain success.</para>
    /// </summary>
    private async Task RecordClientScriptProblemAsync(
        DownloadDispatchItem dispatch,
        bool isFailure,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{dispatch.DownloadClientName}'s post-processing script {(isFailure ? "failed" : "warned")} on this download."
            : reason;
        var queueItemId = dispatch.TorrentHashOrItemId ?? dispatch.Id;
        var platformSettings = await platformRepository.GetAsync(cancellationToken);
        var defaultStrikeWindow = TimeSpan.FromMinutes(Math.Clamp(platformSettings.DownloadHealthDefaultStrikeWindowMinutes, 1, 1440));
        var strikeInactivityWindow = TimeSpan.FromHours(Math.Clamp(platformSettings.DownloadHealthStrikeInactivityHours, 1, 720));

        await healthRepository.RecordDownloadHealthObservationsAsync(
            dispatch.DownloadClientId,
            [queueItemId],
            [new DownloadHealthObservation(
                dispatch.DownloadClientId,
                queueItemId,
                dispatch.ReleaseName,
                DownloadHealthStrikes.ClientScriptFailed,
                isFailure ? "critical" : "warning",
                message)],
            defaultStrikeWindow,
            strikeInactivityWindow,
            cancellationToken);

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            isFailure ? "client-reported-script-failed" : "client-reported-script-warning",
            JsonSerializer.Serialize(new { reason = message, source = "history" }),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            isFailure ? "download.client.script-failed" : "download.client.script-warning",
            $"Your client's script {(isFailure ? "failed" : "warned")} on {dispatch.ReleaseName}: {message}",
            null,
            null,
            "download_dispatch",
            dispatch.Id,
            cancellationToken);
    }

    /// <summary>
    /// "Follow the existing item" for a duplicate (#663): another dispatch for
    /// the same title that has already imported, so this dispatch's release is
    /// already on the shelf under a different id. Matched on entity rather than
    /// release name, because a duplicate's own release name is often not the
    /// name of the copy NZBGet kept.
    /// </summary>
    /// <summary>
    /// Another send of the very same release for the same title that is downloading or has
    /// imported (#1192); null when there is none. A send that failed, was refused or never
    /// reached its client does not count, so a release that really is bad everywhere is
    /// still blocked.
    /// </summary>
    internal async Task<DownloadDispatchItem?> FindLiveSendOfSameReleaseAsync(
        DownloadDispatchItem dispatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatch.EntityId) || string.IsNullOrWhiteSpace(dispatch.ReleaseName))
        {
            return null;
        }

        var (items, _) = await dispatchesRepository.QueryDispatchesAsync(
            new DispatchQueryFilter
            {
                EntityType = dispatch.EntityType,
                EntityId = dispatch.EntityId,
                IncludeArchived = true
            },
            new DispatchPaginationOptions { PageSize = 100 },
            cancellationToken);

        return items
            .Where(item => !string.Equals(item.Id, dispatch.Id, StringComparison.Ordinal)
                && string.Equals(item.ReleaseName, dispatch.ReleaseName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.GrabStatus, "succeeded", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(item.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrEmpty(item.ImportStatus)
                        && !string.Equals(item.Status, "archived", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(item => string.Equals(item.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
    }

    private async Task<DownloadDispatchItem?> FindExistingImportForSameEntityAsync(
        DownloadDispatchItem dispatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        var (items, _) = await dispatchesRepository.QueryDispatchesAsync(
            new DispatchQueryFilter
            {
                EntityType = dispatch.EntityType,
                EntityId = dispatch.EntityId,
                ImportStatus = "imported",
                IncludeArchived = true
            },
            new DispatchPaginationOptions { PageSize = 5 },
            cancellationToken);

        return items
            .Where(item => !string.Equals(item.Id, dispatch.Id, StringComparison.Ordinal))
            .OrderByDescending(item => item.ImportCompletedUtc ?? item.CreatedUtc)
            .FirstOrDefault();
    }

    /// <summary>Whether <see cref="QueueReplacementSearchAsync"/> can act on this dispatch's entity type. See <see cref="ReplacementSearchQueuer.Supports"/>.</summary>
    private static bool SupportsReplacementSearch(string entityType)
        => ReplacementSearchQueuer.Supports(entityType);

    private static IndexerItem? SiteOf(IReadOnlyList<IndexerItem> sites, DownloadDispatchItem dispatch, DownloadQueueItem item)
    {
        static bool Same(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

        return sites.FirstOrDefault(site => Same(site.Id, dispatch.IndexerId))
            ?? sites.FirstOrDefault(site => Same(site.Name, dispatch.IndexerName))
            ?? sites.FirstOrDefault(site => Same(site.Name, item.IndexerName));
    }

    /// <summary>
    /// What a site is, for keeping a torrent seeding (#1240): what was stored for it, or, when nothing was (a site added
    /// before Deluno kept it), what its tracker definition says. The person's own override is applied on top, by the resolver.
    /// </summary>
    private string EffectivePrivacy(IndexerItem site)
    {
        var stored = IndexerPrivacy.Normalize(site.Privacy);
        if (stored != IndexerPrivacy.Unknown || string.IsNullOrWhiteSpace(site.DefinitionId))
        {
            return stored;
        }

        return IndexerPrivacy.Normalize(trackerDefinitionNames?.Privacy(site.DefinitionId));
    }

    /// <summary>Asks the client to move one torrent to the keep-seeding category; a failure of any kind comes back as a plain refusal.</summary>
    private async Task<DownloadClientActionResult> MoveToKeepSeedingAsync(
        DownloadClientItem client,
        string queueItemId,
        string category,
        string? currentCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            return downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? await implementation.SetCategoryAsync(client, queueItemId, category, currentCategory, cancellationToken)
                : DownloadClientHelpers.ActionFailure(client, queueItemId, DownloadClientActions.SetCategory, $"'{client.Protocol}' is not a supported download client protocol.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or JsonException
                                          && !cancellationToken.IsCancellationRequested)
        {
            return DownloadClientHelpers.ActionFailure(
                client, queueItemId, DownloadClientActions.SetCategory,
                DownloadClientHelpers.PlainConnectionFailure(exception.Message, client.Name) ?? exception.Message,
                category: "unreachable");
        }
    }

    /// <summary>
    /// Records a torrent Deluno meant to keep seeding and could not (#1240), and says so in Activity the first time and
    /// whenever the reason changes, not on every retry. The row stays on System, Health until a retry works.
    /// </summary>
    private async Task NoteKeepSeedingProblemAsync(
        Dictionary<string, KeepSeedingProblem> known,
        HashSet<string> open,
        DownloadClientItem client,
        DownloadQueueItem item,
        string kind,
        string message,
        string? category,
        CancellationToken cancellationToken)
    {
        if (keepSeedingStore is null)
        {
            return;
        }

        var key = KeepSeedingProblem.KeyFor(client.Id, item.Id);
        open.Add(key);
        var now = timeProvider.GetUtcNow();
        known.TryGetValue(key, out var before);
        var problem = new KeepSeedingProblem(
            key, client.Id, client.Name, item.Id, item.ReleaseName, kind, message, category,
            before?.FirstSeenUtc ?? now, now, (before?.Attempts ?? 0) + 1);
        await keepSeedingStore.SaveProblemAsync(problem, cancellationToken);
        known[key] = problem;
        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishDownloadHealthChangedAsync(client.Id, cancellationToken));
        if (before is null || !string.Equals(before.Message, message, StringComparison.Ordinal) || !string.Equals(before.Kind, kind, StringComparison.Ordinal))
        {
            await activityFeedRepository.RecordActivityAsync(
                "download.health.keep-seeding-problem",
                message,
                JsonSerializer.Serialize(new { clientId = client.Id, queueItemId = item.Id, kind, category }),
                null,
                "download_client",
                client.Id,
                cancellationToken);
        }
    }

    /// <summary>"Moved X to Y on Z so it keeps seeding on T, and is looking for another copy", with the rule's own reason (#1240).</summary>
    internal static string KeepSeedingActivity(
        DownloadQueueItem item,
        DownloadClientItem client,
        string category,
        IndexerItem? site,
        IReadOnlyList<string> applied,
        DownloadHealthFinding struck,
        DownloadHealthRule? rule)
    {
        var tracker = !string.IsNullOrWhiteSpace(site?.Name)
            ? site!.Name
            : !string.IsNullOrWhiteSpace(item.IndexerName)
                ? item.IndexerName
                : item.TrackerHosts?.FirstOrDefault() ?? "its tracker";
        var searching = applied.Any(step => step.Contains("replacement", StringComparison.OrdinalIgnoreCase));
        var blocked = applied.Any(step => step.Contains("refused", StringComparison.OrdinalIgnoreCase));
        var message = new StringBuilder($"Moved {item.ReleaseName} to {category} on {client.Name} so it keeps seeding on {tracker}");
        message.Append(searching ? ", and is looking for another copy." : ".");
        if (blocked)
        {
            message.Append(" Deluno blocked this release.");
        }

        var reason = RuleReason(struck, rule) ?? struck.Summary;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            message.Append(' ').Append(reason.TrimEnd('.')).Append('.');
        }

        return message.ToString();
    }

    /// <summary>#905 decision 2: what each action preset means for the four legacy switches this executor already knew how to apply.</summary>
    private static (bool Block, bool Replacement, bool Remove, bool Purge) ActionsFor(string preset) => preset switch
    {
        DownloadHealthActionPresets.Replace => (true, true, true, true),
        DownloadHealthActionPresets.RetryElsewhere => (true, true, false, false),
        _ => (false, false, false, false) // JustTellMe: surfaced only.
    };

    internal sealed record ClientSighting(
        DownloadDispatchItem Dispatch,
        long? DownloadedBytes,
        bool Finished,
        bool Failed = false,
        string? FailureReason = null,
        /// <summary>
        /// The client's own duplicate check declined this release — it, or a
        /// better copy of it, is already held (#663). Distinct from
        /// <see cref="Failed"/>: nothing was lost, so nothing is blocklisted
        /// and nothing is searched for again.
        /// </summary>
        bool Duplicate = false,
        string? DuplicateReason = null,
        /// <summary>
        /// The download finished but a person has to act before it can go
        /// further (#733) — NZBGet's WARNING/SPACE (disk full while
        /// unpacking). Distinct from <see cref="Failed"/>: the release is not
        /// lost, so nothing is blocklisted and nothing is searched for again.
        /// </summary>
        bool NeedsAttention = false,
        string? AttentionReason = null,
        /// <summary>
        /// The download itself finished, but the client's own post-processing
        /// script failed or warned (#910). Distinct from <see cref="Failed"/>:
        /// a problem with the client's script, not the release, so nothing is
        /// blocklisted and nothing is searched for — the finished file still
        /// goes to the ordinary import path to be probed.
        /// </summary>
        bool ScriptProblem = false,
        bool IsScriptFailure = false,
        string? ScriptMessage = null);

    /// <summary>
    /// The downloads this client shows that their records do not know about yet:
    /// never seen, or seen and now finished.
    ///
    /// <para>Matched on the id the client gave when it took the download
    /// (SABnzbd's nzo_id, the torrent's infohash), never on the release name, which
    /// an earlier attempt at the same release shares.</para>
    /// </summary>
    internal static IReadOnlyList<ClientSighting> ShownByClient(
        DownloadClientTelemetrySnapshot live,
        IEnumerable<DownloadDispatchItem> clientDispatches)
    {
        var shown = new Dictionary<string, (long? Bytes, bool Finished, bool Failed, string? Reason, bool Duplicate, string? DuplicateReason, bool NeedsAttention, string? AttentionReason, bool ScriptProblem, bool IsScriptFailure, string? ScriptMessage)>(StringComparer.OrdinalIgnoreCase);

        void Add(string? id, long? bytes, bool finished, bool failed, string? reason, bool duplicate, string? duplicateReason, bool needsAttention, string? attentionReason, bool scriptProblem, bool isScriptFailure, string? scriptMessage)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var key = id.Trim();
            shown[key] = shown.TryGetValue(key, out var seen)
                ? (bytes ?? seen.Bytes, finished || seen.Finished, failed || seen.Failed, reason ?? seen.Reason, duplicate || seen.Duplicate, duplicateReason ?? seen.DuplicateReason,
                    needsAttention || seen.NeedsAttention, attentionReason ?? seen.AttentionReason,
                    scriptProblem || seen.ScriptProblem, isScriptFailure || seen.IsScriptFailure, scriptMessage ?? seen.ScriptMessage)
                : (bytes, finished, failed, reason, duplicate, duplicateReason, needsAttention, attentionReason, scriptProblem, isScriptFailure, scriptMessage);
        }

        foreach (var item in live.Queue)
        {
            Add(item.Id, item.DownloadedBytes, item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed, false, null, false, null, false, null, false, false, null);
        }

        foreach (var item in live.History)
        {
            // A failure counts as finished — and as a failure Deluno should act
            // on — only in the client's own history. One derived from the
            // queue is a torrent with an error message, which can still be
            // downloading (#647: NZBGet's FAILURE/DELETED/WARNING statuses,
            // SABnzbd's Failed, land here).
            var failedByClient = item.Outcome == "failed" && item.HistorySource == "native";
            // A duplicate the client itself declined (#663): NZBGet's
            // DELETED/DUPE, DELETED/COPY and DELETED/GOOD. Also only trusted
            // from the client's own history — never inferred from the queue.
            var duplicateByClient = item.Outcome == DownloadClientHistoryOutcomes.Duplicate && item.HistorySource == "native";
            // The download is not finished by needing a person (#733):
            // NZBGet's WARNING/SPACE (disk full during unpacking) means
            // unpacking has not produced anything importable yet, and NZBGet
            // keeps reporting the same history row until a person frees space
            // and re-runs post-processing — at which point it reports SUCCESS
            // and this dispatch takes the ordinary completed path. Folding it
            // into `finished` here stamped ClientCompletedUtc immediately,
            // which is wrong twice over: it looks like the still-packed
            // output is ready to import right now, and once NZBGet later
            // does report SUCCESS the sighting filter below (`seen.Finished
            // && ClientCompletedUtc is null`) never fires again because
            // ClientCompletedUtc is already set — the recovered download
            // would never be picked up.
            var attentionByClient = item.Outcome == DownloadClientHistoryOutcomes.NeedsAttention && item.HistorySource == "native";
            // The client's own post-processing script failed or warned (#910)
            // — NZBGet's FAILURE/SCRIPT and WARNING/SCRIPT. Unlike a disk-full
            // NeedsAttention sighting, the download itself really did finish
            // here, so this counts towards `finished` the same as a plain
            // success: the file is handed to the ordinary import path once,
            // and the script problem is only ever told once.
            var scriptFailedByClient = item.Outcome == DownloadClientHistoryOutcomes.ScriptFailed && item.HistorySource == "native";
            var scriptWarningByClient = item.Outcome == DownloadClientHistoryOutcomes.ScriptWarning && item.HistorySource == "native";
            var scriptProblemByClient = scriptFailedByClient || scriptWarningByClient;
            var finished = item.Outcome is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady || failedByClient || duplicateByClient || scriptProblemByClient;
            var bytes = item.SizeBytes > 0 ? item.SizeBytes : (long?)null;
            var reason = failedByClient ? item.ErrorMessage : null;
            var duplicateReason = duplicateByClient ? item.ErrorMessage : null;
            var attentionReason = attentionByClient ? item.ErrorMessage : null;
            var scriptMessage = scriptProblemByClient ? item.ErrorMessage : null;
            Add(item.Id, bytes, finished, failedByClient, reason, duplicateByClient, duplicateReason, attentionByClient, attentionReason, scriptProblemByClient, scriptFailedByClient, scriptMessage);
            Add(item.ExternalId, bytes, finished, failedByClient, reason, duplicateByClient, duplicateReason, attentionByClient, attentionReason, scriptProblemByClient, scriptFailedByClient, scriptMessage);
        }

        var sightings = new List<ClientSighting>();
        foreach (var dispatch in clientDispatches)
        {
            if (string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId)
                || !shown.TryGetValue(dispatch.TorrentHashOrItemId.Trim(), out var seen))
            {
                continue;
            }

            // A needs-attention sighting keeps qualifying on every poll while
            // ClientCompletedUtc stays unset (#733) — it never becomes
            // "finished" on its own, so this is the only way NZBGet later
            // reporting SUCCESS still gets picked up, and the only way a
            // person still gets told about it if the earlier telling somehow
            // never landed. RecordClientNeedsAttentionAsync is the one that
            // stops the timeline and activity feed from repeating.
            if (dispatch.DetectedUtc is null
                || (seen.Finished && dispatch.ClientCompletedUtc is null)
                || (seen.NeedsAttention && dispatch.ClientCompletedUtc is null))
            {
                sightings.Add(new ClientSighting(dispatch, seen.Bytes, seen.Finished, seen.Failed, seen.Reason, seen.Duplicate, seen.DuplicateReason, seen.NeedsAttention, seen.AttentionReason, seen.ScriptProblem, seen.IsScriptFailure, seen.ScriptMessage));
            }
        }

        return sightings;
    }

    /// <summary>
    /// Dispatches the client no longer lists at all - not in its queue, not
    /// in its history - that Deluno had already seen and has not imported yet
    /// (#927). Waiting for the ordinary grab/detection timeouts to notice
    /// this took hours; comparing against what the client actually shows,
    /// on every poll, notices it in minutes.
    ///
    /// <para><b>K3, the empty-list guard.</b> A client that has just
    /// restarted, or answered with a momentary empty page, reports an empty
    /// queue and empty history the same way a client that genuinely lost
    /// every download would. Reading that as "everything vanished" would
    /// raise a problem on every tracked download over one bad poll, so an
    /// entirely empty response is treated as Deluno not yet knowing, never as
    /// proof.</para>
    /// </summary>
    internal static IReadOnlyList<DownloadDispatchItem> VanishedFromClient(
        DownloadClientTelemetrySnapshot live,
        IEnumerable<DownloadDispatchItem> clientDispatches)
    {
        if (live.Queue.Count == 0 && live.History.Count == 0)
        {
            return [];
        }

        var shownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in live.Queue)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                shownIds.Add(item.Id.Trim());
            }
        }

        foreach (var item in live.History)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                shownIds.Add(item.Id.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.ExternalId))
            {
                shownIds.Add(item.ExternalId.Trim());
            }
        }

        var vanished = new List<DownloadDispatchItem>();
        foreach (var dispatch in clientDispatches)
        {
            // Only a download the client had actually shown before, and one
            // Deluno has not already finished with: never-appeared is the
            // grab-timeout's own story, and an imported or archived download
            // has nothing left to vanish from (#927 stage 3: nothing to do).
            if (dispatch.DetectedUtc is null
                || dispatch.ImportStatus is not null
                || string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
            {
                continue;
            }

            if (!shownIds.Contains(dispatch.TorrentHashOrItemId.Trim()))
            {
                vanished.Add(dispatch);
            }
        }

        return vanished;
    }

    /// <summary>
    /// Told once, the first time a still-tracked download is noticed missing
    /// from the client entirely (#927). No automatic search and nothing
    /// blocklisted: it may have been removed on purpose, so only a person
    /// decides what happens next.
    /// </summary>
    private async Task RecordClientVanishedAsync(DownloadDispatchItem dispatch, string clientName, CancellationToken cancellationToken)
    {
        const string problemKind = "vanished-downloading";
        if (dispatchProblemLog is null ||
            await dispatchProblemLog.IsRecordedAsync(dispatch.Id, problemKind, cancellationToken))
        {
            return;
        }

        await dispatchProblemLog.RecordAsync(dispatch.Id, problemKind, cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.vanished",
            $"{dispatch.ReleaseName} vanished from {clientName} while it was still downloading. Nothing was blocklisted or searched for automatically.",
            null,
            null,
            dispatch.EntityType,
            dispatch.EntityId,
            cancellationToken);

        // The Transfers recovery case: Search for another, Stop wanting it,
        // Dismiss (#927). The activity entry above is the signal; this is
        // what puts the three buttons on screen.
        if (recoveryHandler is not null)
        {
            var detailsJson = JsonSerializer.Serialize(new
            {
                DispatchId = dispatch.Id,
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                dispatch.ReleaseName,
                dispatch.MediaType,
                dispatch.EntityId
            });
            await recoveryHandler.HandleVanishedDownloadingAsync(
                dispatch.ReleaseName,
                dispatch.MediaType,
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                dispatch.ReleaseName,
                dispatch.EntityId,
                detailsJson,
                cancellationToken);
        }
    }

    internal static DownloadClientTelemetrySnapshot EnrichWithDispatchHistory(
        DownloadClientTelemetrySnapshot snapshot,
        IEnumerable<DownloadDispatchItem> dispatches,
        DateTimeOffset capturedUtc)
    {
        // Native usenet history and Deluno dispatch history use different row
        // identifiers. Match on both identities so a completed grab is shown
        // once while still retaining dispatch-only rows when the client has
        // forgotten them. This is especially important after restart: duplicate
        // rows make the grab -> client -> import trace look like two downloads.
        var liveIdentifiers = snapshot.History
            .SelectMany(item => new[] { item.Id, item.ExternalId })
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dispatchHistory = dispatches
            .Where(dispatch => !liveIdentifiers.Contains(dispatch.Id) &&
                               !liveIdentifiers.Contains(dispatch.TorrentHashOrItemId ?? string.Empty))
            .Select(dispatch => CreateDispatchHistoryItem(snapshot, dispatch, capturedUtc))
            .ToArray();

        if (dispatchHistory.Length == 0)
        {
            return snapshot;
        }

        var historyItems = snapshot.History
            .Concat(dispatchHistory)
            .OrderByDescending(item => item.CompletedUtc)
            .ToArray();

        return snapshot with
        {
            History = historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            HistoryTruncated = snapshot.HistoryTruncated || historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow
        };
    }

    /// <summary>
    /// "In library" is a claim about the title, so it needs the title to hold its file. A download
    /// whose import was recorded but whose film or episode has no file (removed and added again,
    /// or the link cleared by a repair) reads as ready to import, never as in the library.
    /// </summary>
    internal static async Task<DownloadClientTelemetrySnapshot> DemoteImportedWithoutItsFileAsync(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<DownloadDispatchItem> settledDispatches,
        IDispatchTitleUnmonitorService? titleFiles,
        CancellationToken cancellationToken,
        IReadOnlyList<ProcessorHandoffItem>? handoffs = null,
        IReadOnlyList<JobQueueItem>? importJobs = null)
    {
        if (titleFiles is null || !snapshot.Queue.Any(item => item.Status == DownloadQueueStatuses.Imported))
        {
            return snapshot;
        }

        var queue = new List<DownloadQueueItem>(snapshot.Queue.Count);
        foreach (var item in snapshot.Queue)
        {
            if (item.Status == DownloadQueueStatuses.Imported)
            {
                var dispatch = settledDispatches
                    .Where(candidate =>
                        string.Equals(candidate.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => candidate.CreatedUtc)
                    .FirstOrDefault();
                if (dispatch is not null
                    && !string.IsNullOrWhiteSpace(dispatch.EntityId)
                    && await titleFiles.HasFileAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken) == false)
                {
                    // "Ready to import" is a promise that Deluno imports this next. When the hand-off for this very
                    // download ran to its end and its import finished, Deluno has done that already and the
                    // planner never imports it twice: the file left the library afterwards (a wrong episode moved
                    // to the recycle bin, say). The row says so, as history: it is neither "In library" nor ready to
                    // import (#1182). A release sent again since is a new attempt and is handed over again, so for
                    // that one the row is ready to import.
                    // A download whose hand-off ended because it was gone, and was dismissed, is closed history (#1200):
                    // its files are not there to import, so it is never "ready to import". Sent again since, it is a new attempt.
                    if (SourceGoneAndDismissed(item, handoffs, settledDispatches))
                    {
                        queue.Add(item with { Status = DownloadQueueStatuses.FilesGone });
                        continue;
                    }

                    if (ImportEndedAndTheFileLeft(item, handoffs, settledDispatches, importJobs))
                    {
                        queue.Add(item with { Status = DownloadQueueStatuses.ImportedFileLeft });
                        continue;
                    }

                    queue.Add(item with { Status = DownloadQueueStatuses.ImportReady });
                    continue;
                }
            }

            queue.Add(item);
        }

        return snapshot with { Queue = queue };
    }

    private static bool SourceGoneAndDismissed(
        DownloadQueueItem item,
        IReadOnlyList<ProcessorHandoffItem>? handoffs,
        IReadOnlyList<DownloadDispatchItem> settledDispatches)
    {
        var handoff = handoffs?
            .Where(candidate =>
                string.Equals(candidate.ClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.QueueItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.UpdatedUtc)
            .FirstOrDefault();
        return handoff is not null
            && DownloadAttempts.SourceGoneClosed(handoff)
            && !DownloadAttempts.SentAgainSince(settledDispatches, item.ClientId, item.ReleaseName, handoff.UpdatedUtc);
    }

    /// <summary>
    /// A download the client lists as done whose files are not on disk says so within the check (#1200), instead of
    /// reading "Done 100%" for as long as the client keeps listing it. Only a path whose folder this machine can
    /// see is judged: a path on another machine, unmapped, is not "gone", it is simply out of sight.
    /// </summary>
    internal static DownloadClientTelemetrySnapshot MarkDownloadsWhoseFilesAreGone(
        DownloadClientTelemetrySnapshot snapshot,
        Func<string, bool> pathExists,
        IReadOnlyList<ProcessorHandoffItem>? handoffs = null,
        IReadOnlyList<DownloadDispatchItem>? settledDispatches = null)
    {
        var finishedInHistory = FinishedJobsListedOnlyInHistory(snapshot, handoffs, settledDispatches);
        if (finishedInHistory.Count == 0 && !snapshot.Queue.Any(item => DownloadFilesGone.ExpectsItsFiles(item.Status)))
        {
            return snapshot;
        }

        var queue = snapshot.Queue.Select(item =>
        {
            // Waiting for a processor is included (#1210): its files vanishing is flagged within one check, the
            // same as ready to import and done, instead of reading "Waiting for processor" until the processor says.
            if (!DownloadFilesGone.ExpectsItsFiles(item.Status) || !DownloadFilesGone.AreGone(item.SourcePath, pathExists))
            {
                return item;
            }

            return item with
            {
                Status = DownloadQueueStatuses.FilesGone,
                ErrorMessage = DownloadFilesGone.Message,
                IsSetupProblem = true
            };
        }).ToList();

        // SABnzbd and NZBGet list a finished job in their history only, never in the queue, so a job whose files are
        // gone from disk has no queue row to flag. It gets one, with the same status and actions (#1210, rc.30.22).
        foreach (var item in finishedInHistory)
        {
            if (queue.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                || !DownloadFilesGone.AreGone(item.SourcePath, pathExists))
            {
                continue;
            }

            queue.Add(item with
            {
                Status = DownloadQueueStatuses.FilesGone,
                ErrorMessage = DownloadFilesGone.Message,
                IsSetupProblem = true
            });
        }

        return snapshot with { Queue = queue };
    }

    /// <summary>
    /// The finished jobs a client lists in its history only (the client's own record, not Deluno's), as the queue rows the
    /// import planner reads them as, except those whose hand-off or import already ran to its end: their files are
    /// expected to be gone, and they are history, not a problem.
    /// </summary>
    private static List<DownloadQueueItem> FinishedJobsListedOnlyInHistory(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<ProcessorHandoffItem>? handoffs,
        IReadOnlyList<DownloadDispatchItem>? settledDispatches)
    {
        var rows = new List<DownloadQueueItem>();
        foreach (var history in snapshot.History)
        {
            if (!string.Equals(history.HistorySource, "native", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(history.Outcome, DownloadQueueStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(history.SourcePath))
            {
                continue;
            }

            var row = new DownloadQueueItem(
                history.ExternalId ?? history.Id,
                history.ClientId,
                history.ClientName,
                history.Protocol,
                history.MediaType,
                history.Title,
                history.ReleaseName,
                history.Category,
                DownloadQueueStatuses.Completed,
                Progress: 100,
                SpeedMbps: 0,
                EtaSeconds: 0,
                history.SizeBytes,
                DownloadedBytes: history.SizeBytes,
                Peers: 0,
                history.IndexerName,
                history.ErrorMessage,
                AddedUtc: history.CompletedUtc,
                history.SourcePath);

            var handoff = handoffs?
                .Where(candidate =>
                    string.Equals(candidate.ClientId, row.ClientId, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(candidate.QueueItemId, row.Id, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.ReleaseName, row.ReleaseName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(candidate => candidate.UpdatedUtc)
                .FirstOrDefault();
            var ranToItsEnd = handoff is not null
                && (handoff.Status is ProcessorHandoffStatuses.Completed or ProcessorHandoffStatuses.Released
                    || !string.IsNullOrWhiteSpace(handoff.ImportJobId));
            if (ranToItsEnd || WasAlreadyImported(row, settledDispatches ?? []))
            {
                continue;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static bool ImportEndedAndTheFileLeft(
        DownloadQueueItem item,
        IReadOnlyList<ProcessorHandoffItem>? handoffs,
        IReadOnlyList<DownloadDispatchItem> settledDispatches,
        IReadOnlyList<JobQueueItem>? importJobs)
    {
        var handoff = handoffs?
            .Where(candidate =>
                string.Equals(candidate.ClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.QueueItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.UpdatedUtc)
            .FirstOrDefault();
        // A hand-off settled Dismissed a moment before its import job finished is the same as a completed one (#1182).
        var jobStatus = handoff?.ImportJobId is { Length: > 0 } jobId
            ? importJobs?.FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase))?.Status
            : null;
        return handoff is not null
            && DownloadAttempts.ImportRan(handoff, jobStatus)
            && !DownloadAttempts.SentAgainSince(settledDispatches, item.ClientId, item.ReleaseName, handoff.UpdatedUtc);
    }

    internal static DownloadClientTelemetrySnapshot EnrichQueueImportState(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyList<DownloadDispatchItem> settledDispatches,
        IReadOnlyList<JobQueueItem> importJobs,
        IReadOnlyList<ProcessorHandoffItem> handoffs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> routeCategoriesByLibrary,
        IReadOnlySet<string>? entitiesWithFile = null,
        DateTimeOffset? nowUtc = null)
    {
        if (snapshot.Queue.Count == 0)
        {
            return snapshot;
        }

        var jobsBySource = importJobs
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => new { Job = job, SourcePath = TryReadImportSourcePath(job.PayloadJson) })
            .Where(item => !string.IsNullOrWhiteSpace(item.SourcePath))
            .GroupBy(item => NormalizeSourceKey(item.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Job.CreatedUtc).First().Job, StringComparer.OrdinalIgnoreCase);
        var jobsById = importJobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);

        var clientHandoffs = handoffs
            .Where(handoff => string.Equals(handoff.ClientId, snapshot.ClientId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(handoff => handoff.UpdatedUtc)
            .ToArray();
        var handoffsByQueueItem = clientHandoffs
            .GroupBy(handoff => handoff.QueueItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var handoffsBySource = clientHandoffs
            .Where(handoff => !string.IsNullOrWhiteSpace(handoff.SourcePath))
            .GroupBy(handoff => NormalizeSourceKey(handoff.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var queue = snapshot.Queue.Select(item =>
        {
            if (item.Status is not (DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed))
            {
                return item;
            }

            // The file check failed this download (#1148): Activity says so, so
            // Transfers says so too, as something to deal with, not as a
            // download still waiting for its processor.
            if (FileCheckFailureOf(item, dispatches) is { } fileCheckFailure)
            {
                return item with { Status = DownloadQueueStatuses.ImportFailed, ErrorMessage = fileCheckFailure };
            }

            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                jobsBySource.TryGetValue(NormalizeSourceKey(item.SourcePath), out var job))
            {
                var status = ShouldApplyImportJobState(item, job, dispatches)
                    ? job.Status switch
                    {
                        "queued" or "running" => DownloadQueueStatuses.ImportQueued,
                        "completed" => DownloadQueueStatuses.Imported,
                        "failed" or "dead-letter" => DownloadQueueStatuses.ImportFailed,
                        _ => item.Status
                    }
                    : item.Status;
                // The job ran to its end, but the import itself was refused (an unreadable file, say): the row says
                // what Activity says, not "Imported".
                if (status == DownloadQueueStatuses.Imported && ImportFailureOf(item, settledDispatches) is { } refusedImport)
                {
                    return item with { Status = DownloadQueueStatuses.ImportFailed, ErrorMessage = item.ErrorMessage ?? refusedImport };
                }

                return item with { Status = status };
            }

            var library = ResolveLibraryForQueueItem(item, libraries, dispatches, routeCategoriesByLibrary);
            var resolvedItem = library is null
                ? item
                : item with { LibraryId = library.Id, MediaType = library.MediaType };

            // A refined item is imported from the processor output path while it
            // still sits in the client queue under its download path, so matching
            // the two by path can never work. The hand-off record is what joins
            // them, and reading its lifecycle here is what finally lets an item
            // leave the Processing stage: without it a completed import still
            // reported processingCount 1 for ever (#280).
            var handoff = handoffsByQueueItem.GetValueOrDefault(item.Id)
                ?? (string.IsNullOrWhiteSpace(item.SourcePath) ? null : handoffsBySource.GetValueOrDefault(NormalizeSourceKey(item.SourcePath)));

            // A hand-off that predates the attempt in front of us describes a
            // previous one.
            //
            // Both keys it is found by — the infohash and the download path —
            // are the same every time the same release is fetched, so a
            // re-download inherited the last outcome. On the lab rig a brand new
            // download of Big Buck Bunny matched yesterday's completed hand-off
            // and was reported `imported` while the library folder was empty and
            // the film read Missing. That status is not in the set the import
            // planner accepts, so no hand-off was created, no import was
            // attempted, and nothing failed: the download simply stopped
            // existing as far as Deluno was concerned.
            if (handoff is not null && IsFromAnEarlierAttempt(handoff, item, dispatches))
            {
                handoff = null;
            }

            // A download whose file Deluno has already imported is imported,
            // whatever an old hand-off for it says (#1155): a failed or stale
            // hand-off must not keep it in Processing or Needs action.
            if (handoff is not null && WasAlreadyImported(item, settledDispatches))
            {
                return resolvedItem with { Status = DownloadQueueStatuses.Imported };
            }

            // A hand-off a person dismissed, or one the processor finished whose output is gone,
            // for a download whose title holds its file now (it was imported another way, or
            // relinked to its own file) is done, not waiting for anything (#1155).
            if (handoff is not null
                && TitleHoldsFile(item, settledDispatches, entitiesWithFile)
                && (handoff.Status == ProcessorHandoffStatuses.Dismissed
                    || (handoff.Status == ProcessorHandoffStatuses.Completed && string.IsNullOrWhiteSpace(handoff.ImportJobId))))
            {
                return resolvedItem with { Status = DownloadQueueStatuses.Imported };
            }

            if (handoff is not null)
            {
                var queueStatus = ProcessorHandoffQueueStatus.Resolve(handoff, jobsById, resolvedItem.Status);
                // The processor's output came back and the import job finished, but the import was refused
                // (an unreadable file after an integrity failure, say): Transfers agrees with Activity.
                if (queueStatus == DownloadQueueStatuses.Imported && ImportFailureOf(item, settledDispatches) is { } refusedImport)
                {
                    return resolvedItem with { Status = DownloadQueueStatuses.ImportFailed, ErrorMessage = resolvedItem.ErrorMessage ?? refusedImport };
                }

                return resolvedItem with
                {
                    Status = queueStatus,
                    // The hand-off is the only record holding the processor's own
                    // reason (#616). The client itself never saw a failure - it
                    // downloaded the file fine - so resolvedItem.ErrorMessage is
                    // empty here, and without this the row and its health finding
                    // fall back to a generic "the processor did not produce an
                    // import-ready output", leaving nothing to actually read.
                    ErrorMessage = queueStatus == DownloadQueueStatuses.ProcessingFailed
                        ? resolvedItem.ErrorMessage ?? handoff.FailureMessage
                        : resolvedItem.ErrorMessage,
                    // The reason came from the processor, not the client, so the
                    // failure says so (#1151). Left to the later normalisation it
                    // read "Deluge queue failed: The processor refused...".
                    Failure = queueStatus == DownloadQueueStatuses.ProcessingFailed
                        && resolvedItem.Failure is null
                        && string.IsNullOrWhiteSpace(resolvedItem.ErrorMessage)
                        && !string.IsNullOrWhiteSpace(handoff.FailureMessage)
                        ? IntegrationFailureFactory.FromLegacy(
                            "processor",
                            handoff.Id,
                            string.IsNullOrWhiteSpace(handoff.ProcessorName) ? "The processor" : handoff.ProcessorName!,
                            "hand-off",
                            "rejected",
                            handoff.FailureMessage!.Trim(),
                            code: handoff.Status,
                            externalId: handoff.Id)
                        : resolvedItem.Failure
                };
            }

            // The library's workflow says what happens to a new download, not
            // what happened to this one. On the rig Sintel was imported, its
            // library was switched to refine-before-import days later, and the
            // torrent was still seeding: with its import job aged out of the
            // window above, no hand-off (none was ever needed) and its record
            // archived, it read "Waiting for processor" for ever and held the
            // Processing count at one.
            if (WasAlreadyImported(item, settledDispatches))
            {
                return resolvedItem with { Status = DownloadQueueStatuses.Imported };
            }

            if (library is not null && ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                // No hand-off of its own. Only briefly is that the planner about to make one (#1155).
                // When another copy of the same release holds the hand-off, or the title already has
                // its file, the row says so; after a few minutes with neither it says what is wrong.
                if (TitleHoldsFile(item, settledDispatches, entitiesWithFile))
                {
                    return resolvedItem with { Status = DownloadQueueStatuses.Imported };
                }

                if (AnotherCopyHasTheHandoff(item, handoffs))
                {
                    return resolvedItem with { Status = DownloadQueueStatuses.HandedOverElsewhere };
                }

                var finishedUtc = FinishedUtcOf(item, dispatches);
                if (finishedUtc is { } finished && (nowUtc ?? DateTimeOffset.UtcNow) - finished >= NoHandoffNoticeAfter)
                {
                    return resolvedItem with { Status = DownloadQueueStatuses.NotHandedOver };
                }

                return resolvedItem with { Status = DownloadQueueStatuses.WaitingForProcessor };
            }

            return resolvedItem;
        }).ToArray();

        // One rule for "imported", on every path above: a download whose latest send had its import refused is
        // never in the library, whichever record (a finished hand-off, a title that holds a file) said so. The
        // archived sends are read too, since a refusal that was dismissed is still a refusal.
        queue = queue.Select(item =>
            item.Status == DownloadQueueStatuses.Imported && ImportFailureOf(item, settledDispatches) is { } refused
                ? item with { Status = DownloadQueueStatuses.ImportFailed, ErrorMessage = item.ErrorMessage ?? refused }
                : item).ToArray();

        // A download that is in the library has nothing left to ask of anyone, whatever its client said on the way
        // (a tracker that refused a connection, say): the row reads "In library", not "Needs action" (golden path rc.30.9).
        queue = queue.Select(item =>
            item.Status == DownloadQueueStatuses.Imported && (item.ErrorMessage is not null || item.Failure is not null)
                ? item with { ErrorMessage = null, Failure = null }
                : item).ToArray();

        // One failure, one row (#1167): a failure belongs to the copy that owns the hand-off. Another client's
        // copy of the same release, with no hand-off of its own, is not a second problem.
        queue = queue.Select(item =>
            item.Status is DownloadQueueStatuses.ImportFailed or DownloadQueueStatuses.ProcessingFailed
            && !handoffsByQueueItem.ContainsKey(item.Id)
            && (string.IsNullOrWhiteSpace(item.SourcePath) || !handoffsBySource.ContainsKey(NormalizeSourceKey(item.SourcePath)))
            && AnotherCopyHasTheHandoff(item, handoffs)
                ? item with { Status = DownloadQueueStatuses.HandedOverElsewhere, ErrorMessage = null, Failure = null }
                : item).ToArray();

        return snapshot with
        {
            Queue = queue,
            Summary = Summarize(queue)
        };
    }

    /// <summary>How long a finished download may have no hand-off before the row says something is wrong.</summary>
    internal static readonly TimeSpan NoHandoffNoticeAfter = TimeSpan.FromMinutes(10);

    private static DateTimeOffset? FinishedUtcOf(DownloadQueueItem item, IReadOnlyList<DownloadDispatchItem> dispatches)
        => dispatches
            .Where(dispatch => string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .Select(dispatch => dispatch.ClientCompletedUtc)
            .FirstOrDefault();

    private static bool AnotherCopyHasTheHandoff(DownloadQueueItem item, IReadOnlyList<ProcessorHandoffItem> handoffs)
        => handoffs.Any(other =>
            string.Equals(other.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(other.ClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.QueueItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            && (ProcessorHandoffStatuses.IsWithProcessor(other.Status)
                || other.Status is ProcessorHandoffStatuses.Completed or ProcessorHandoffStatuses.Dismissed));

    private static DownloadDispatchItem? LatestDispatchOf(DownloadQueueItem item, IReadOnlyList<DownloadDispatchItem> dispatches)
        => dispatches
            .Where(candidate => string.Equals(candidate.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.CreatedUtc)
            .FirstOrDefault();

    private static bool TitleHoldsFile(DownloadQueueItem item, IReadOnlyList<DownloadDispatchItem> settledDispatches, IReadOnlySet<string>? entitiesWithFile)
    {
        if (entitiesWithFile is not { Count: > 0 })
        {
            return false;
        }

        var dispatch = LatestDispatchOf(item, settledDispatches);
        return dispatch is not null && !string.IsNullOrWhiteSpace(dispatch.EntityId) && entitiesWithFile.Contains(dispatch.EntityId);
    }

    /// <summary>The titles, among those this client's finished downloads were grabbed for, that hold a file now.</summary>
    internal static async Task<IReadOnlySet<string>> EntitiesHoldingAFileAsync(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<DownloadDispatchItem> settledDispatches,
        IDispatchTitleUnmonitorService? titleFiles,
        CancellationToken cancellationToken)
    {
        var holding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (titleFiles is null)
        {
            return holding;
        }

        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Queue.Where(item => item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed))
        {
            var dispatch = LatestDispatchOf(item, settledDispatches);
            if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId) || !asked.Add(dispatch.EntityId))
            {
                continue;
            }

            if (await titleFiles.HasFileAsync(dispatch.EntityType, dispatch.EntityId, cancellationToken) == true)
            {
                holding.Add(dispatch.EntityId);
            }
        }

        return holding;
    }
    private static LibraryItem? ResolveLibraryForQueueItem(
        DownloadQueueItem item,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> routeCategoriesByLibrary)
    {
        var dispatch = dispatches
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault(dispatch =>
                string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dispatch.ReleaseName, item.Title, StringComparison.OrdinalIgnoreCase));
        if (dispatch is not null)
        {
            var dispatchedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (dispatchedLibrary is not null)
            {
                return dispatchedLibrary;
            }
        }

        var category = item.Category?.Trim();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryLibraries = libraries
                .Where(library => routeCategoriesByLibrary.TryGetValue(library.Id, out var categories) &&
                    categories.TryGetValue(item.ClientId, out var routeCategory) &&
                    string.Equals(routeCategory, category, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (categoryLibraries.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    var categorySource = NormalizeSourceKey(item.SourcePath);
                    var categoryPathMatch = categoryLibraries.FirstOrDefault(library =>
                        !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                        categorySource.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
                    if (categoryPathMatch is not null)
                    {
                        return categoryPathMatch;
                    }
                }

                return categoryLibraries[0];
            }
        }

        var normalizedMediaType = item.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
        var mediaLibraries = libraries
            .Where(library => string.Equals(library.MediaType, normalizedMediaType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(item.SourcePath))
        {
            var source = NormalizeSourceKey(item.SourcePath);
            var pathMatch = mediaLibraries.FirstOrDefault(library =>
                !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                source.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return mediaLibraries.FirstOrDefault();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadRouteCategoriesAsync(
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var routeCategoriesByLibrary = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            routeCategoriesByLibrary[library.Id] = (routing?.DownloadClients ?? [])
                .Where(link => !string.IsNullOrWhiteSpace(link.Category))
                .ToDictionary(link => link.DownloadClientId, link => link.Category!.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        return routeCategoriesByLibrary;
    }

    internal static string? TryReadImportSourcePath(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!TryGetProperty(root, "preview", out var preview) ||
                !TryGetProperty(preview, "sourcePath", out var sourcePath) ||
                sourcePath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sourcePath.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    /// <summary>
    /// A download client reports paths from its own filesystem namespace. Before
    /// Deluno queues a processor handoff or an import, translate that namespace
    /// to the one visible to this host. The longest matching remote root wins so
    /// a specific mapping may safely sit beside a broader one.
    ///
    /// <para>The match-and-combine algorithm itself lives in
    /// <see cref="Deluno.Contracts.PathMappingTranslator"/>, shared with the
    /// processor connection path mapping added for #962 rather than
    /// duplicated.</para>
    /// </summary>
    internal static string? TranslateRemotePath(
        string? reportedPath,
        IEnumerable<DownloadClientPathMappingItem> mappings)
        => Deluno.Contracts.PathMappingTranslator.Translate(
            reportedPath,
            mappings,
            item => item.IsEnabled,
            item => item.RemotePath,
            item => item.LocalPath,
            item => item.Priority);

    /// <summary>
    /// A mapping applies to any client whose host matches, not only the
    /// client it was originally added under (#809) - <paramref name="clientHost"/>
    /// is this snapshot's own client's host, already normalized the same way
    /// a mapping's <see cref="DownloadClientPathMappingItem.Host"/> is. A
    /// client with no host recorded falls back to exactly today's behaviour:
    /// only the mappings created under its own id.
    /// </summary>
    private static DownloadClientTelemetrySnapshot ApplyPathMappings(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<DownloadClientPathMappingItem> mappings,
        string? clientHost)
    {
        var clientMappings = mappings
            .Where(item =>
                string.Equals(item.DownloadClientId, snapshot.ClientId, StringComparison.OrdinalIgnoreCase) ||
                (clientHost is not null && item.Host is not null && string.Equals(item.Host, clientHost, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        // Whatever the client, and with or without a mapping, a reported path that climbs out ("..") is dropped
        // here, once, before the queue, the cleanup or an import can see it (#1306).
        return snapshot with
        {
            Queue = snapshot.Queue
                .Select(item => item with { SourcePath = TranslateRemotePath(Deluno.Contracts.SafePath.ReportedPath(item.SourcePath), clientMappings) })
                .ToArray(),
            History = snapshot.History
                .Select(item => item with { SourcePath = TranslateRemotePath(Deluno.Contracts.SafePath.ReportedPath(item.SourcePath), clientMappings) })
                .ToArray()
        };
    }

    private static IReadOnlyList<DownloadClientHistoryItem> CreateHistoryFromQueue(
        DownloadClientItem client,
        IEnumerable<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc)
    {
        return queue
            .Where(item => item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady || !string.IsNullOrWhiteSpace(item.ErrorMessage))
            .OrderByDescending(item => item.AddedUtc)
            .Select(item => new DownloadClientHistoryItem(
                Id: item.Id,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: item.MediaType,
                Title: item.Title,
                ReleaseName: item.ReleaseName,
                Category: item.Category,
                Outcome: !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? "failed"
                    : item.Status == DownloadQueueStatuses.ImportReady
                        ? DownloadQueueStatuses.ImportReady
                        : DownloadQueueStatuses.Completed,
                IndexerName: item.IndexerName,
                SizeBytes: item.SizeBytes,
                CompletedUtc: item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady ? capturedUtc : item.AddedUtc,
                ErrorMessage: item.ErrorMessage,
                SourcePath: item.SourcePath,
                HistorySource: "queue-derived",
                ExternalId: item.Id,
                Failure: item.Failure))
            .ToArray();
    }

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientTelemetrySnapshot snapshot,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            snapshot.ClientId,
            snapshot.ClientName,
            snapshot.Protocol,
            dispatch,
            capturedUtc);

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientItem client,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            client.Id,
            client.Name,
            client.Protocol,
            dispatch,
            capturedUtc);

    internal static DownloadClientHistoryItem CreateDispatchHistoryItem(
        string clientId,
        string clientName,
        string protocol,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
    {
        var dispatchFailure = dispatch.Failure;
        var failureMessage = dispatchFailure?.Message ?? DescribeDispatchFailure(dispatch);
        var failureOperation = IsFailureStatus(dispatch.ImportStatus)
            ? "import"
            : IsFailureStatus(dispatch.GrabStatus)
                ? "grab"
                : "dispatch";
        var failureCode = IsFailureStatus(dispatch.ImportStatus)
            ? dispatch.ImportFailureCode
            : dispatch.GrabFailureCode ?? dispatch.ImportFailureCode;
        return new DownloadClientHistoryItem(
            Id: dispatch.Id,
            ClientId: clientId,
            ClientName: clientName,
            Protocol: protocol,
            MediaType: dispatch.MediaType,
            Title: DownloadClientHelpers.CleanReleaseTitle(dispatch.ReleaseName),
            ReleaseName: dispatch.ReleaseName,
            Category: dispatch.MediaType,
            Outcome: NormalizeHistoryOutcome(dispatch),
            IndexerName: dispatch.IndexerName,
            SizeBytes: dispatch.DownloadedBytes ?? 0,
            CompletedUtc: dispatch.ImportCompletedUtc
                ?? dispatch.ImportDetectedUtc
                ?? dispatch.DetectedUtc
                ?? dispatch.GrabAttemptedUtc
                ?? (dispatch.CreatedUtc == default ? capturedUtc : dispatch.CreatedUtc),
            // Only a real failure message belongs here. NotesJson is the
            // dispatch's diagnostic payload (the search plan), and passing it
            // as an error message painted successful "sent" rows red and
            // leaked raw JSON into the activity list (#257).
            ErrorMessage: failureMessage,
            HistorySource: "dispatch-derived",
            ExternalId: dispatch.TorrentHashOrItemId ?? dispatch.Id,
            Failure: dispatchFailure ?? (failureMessage is { } message
                ? IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    clientId,
                    clientName,
                    failureOperation,
                    "rejected",
                    message,
                    code: failureCode ?? dispatch.Status,
                    externalId: dispatch.TorrentHashOrItemId ?? dispatch.Id)
                : null));
    }

    /// <summary>
    /// Whether this hand-off belongs to an earlier fetch of the same release.
    ///
    /// <para>The dispatch is what says "this is a new attempt", so a hand-off
    /// created before the current dispatch cannot be about it. Where there is no
    /// dispatch to compare against, the hand-off is kept — losing the Processing
    /// stage for an item Deluno cannot place is worse than showing a stale
    /// one.</para>
    /// </summary>
    internal static bool IsFromAnEarlierAttempt(
        ProcessorHandoffItem handoff,
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        if (latestDispatch is null || handoff.CreatedUtc >= latestDispatch.CreatedUtc)
        {
            return false;
        }

        // A hand-off is made after the client has the download, so one made after
        // this item was added is for this very download, however many later
        // grabs of the same release were recorded (#1155). A fetch again is a
        // new download: the client took it after the hand-off was made.
        return !(item.AddedUtc != default && item.AddedUtc <= handoff.CreatedUtc);
    }

    /// <summary>
    /// Whether the latest fetch of this release from this client was imported.
    ///
    /// <para>The latest, because fetching a release again is a new attempt: a
    /// newer record that has not been imported outranks an older one that was.
    /// Archived records count, because archiving is housekeeping and says
    /// nothing about the outcome.</para>
    /// </summary>
    /// <summary>
    /// Why the latest dispatch of this download failed Deluno's own file check,
    /// or null when it did not (#1148).
    /// </summary>
    internal static string? FileCheckFailureOf(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latestDispatch is not null
            && string.Equals(latestDispatch.ImportFailureCode, DownloadClientFailureCodes.IntegrityCheckFailed, StringComparison.OrdinalIgnoreCase)
            ? latestDispatch.ImportFailureMessage ?? "Deluno's check of the downloaded file failed."
            : null;
    }

    /// <summary>
    /// Why the latest dispatch of this download had its import refused, or null when it did not. Read when a
    /// finished import job would otherwise make the row read Imported while Activity says the import failed.
    /// A failure Deluno's own stall rules handled (the download was replaced) is Activity's story, not this one.
    /// </summary>
    internal static string? ImportFailureOf(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latestDispatch is not null
            && string.Equals(latestDispatch.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(latestDispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase)
            ? latestDispatch.ImportFailureMessage ?? "The import did not complete."
            : null;
    }

    internal static bool WasAlreadyImported(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatchesIncludingArchived)
    {
        var latestDispatch = dispatchesIncludingArchived
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latestDispatch is not null
            && string.Equals(latestDispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldApplyImportJobState(
        DownloadQueueItem item,
        JobQueueItem job,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();
        if (latestDispatch is null
            || string.Equals(latestDispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            TryReadImportDispatchId(job.PayloadJson),
            latestDispatch.Id,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadImportDispatchId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return TryGetProperty(document.RootElement, "dispatchId", out var dispatchId)
                && dispatchId.ValueKind == JsonValueKind.String
                ? dispatchId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The human-readable reason a dispatch failed, or null when it did not.
    /// </summary>
    private static string? DescribeDispatchFailure(DownloadDispatchItem dispatch)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ImportFailureMessage))
        {
            return dispatch.ImportFailureMessage;
        }

        if (IsFailureStatus(dispatch.ImportStatus) || IsFailureStatus(dispatch.GrabStatus) || IsFailureStatus(dispatch.Status))
        {
            if (IsFailureStatus(dispatch.ImportStatus))
            {
                return dispatch.ImportFailureMessage
                    ?? dispatch.ImportFailureCode
                    ?? "The download client reported an import failure.";
            }

            return dispatch.GrabMessage
                ?? dispatch.GrabFailureCode
                ?? dispatch.ImportFailureCode
                ?? "The download client reported a failure.";
        }

        return null;
    }

    private static bool IsFailureStatus(string? status)
        => status is not null &&
           (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("error", StringComparison.OrdinalIgnoreCase));

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
        => DownloadQueueSummary.Of(queue);

    private static string InferMediaType(DownloadClientItem client, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "movies";
        }

        if (!string.IsNullOrWhiteSpace(client.TvCategory) &&
            string.Equals(category, client.TvCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        if (!string.IsNullOrWhiteSpace(client.MoviesCategory) &&
            string.Equals(category, client.MoviesCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "movies";
        }

        var normalized = category.Trim().ToLowerInvariant();
        if (normalized.Contains("sonarr") ||
            normalized.Contains("series") ||
            normalized.Contains("show") ||
            normalized.Contains("tv"))
        {
            return "tv";
        }

        return "movies";
    }

    /// <summary>
    /// The verbs this gateway will pass to an adapter.
    ///
    /// <para>Every adapter implements <c>forget</c> and <c>delete-with-data</c>
    /// — Deluge, NZBGet, qBittorrent and SABnzbd all map them — but this
    /// function did not list them, so both were refused here and no adapter was
    /// ever reached. Measured on the lab rig on 2026-09-05: forcing a
    /// re-download reported <i>"qBittorrent would not forget the release:
    /// Unsupported action."</i></para>
    ///
    /// <para>That took out every path that asks a client to forget a release —
    /// the acquisition override, the refused-download clean-up pass, and
    /// "Clean up now" on the blocklist. None of their tests could see it,
    /// because they all stand in for this service.</para>
    /// </summary>
    private static string? NormalizeAction(string action)
        => action.Trim().ToLowerInvariant() switch
        {
            "pause" => DownloadClientActions.Pause,
            "resume" => DownloadClientActions.Resume,
            "remove" or "delete" => DownloadClientActions.Delete,
            "delete-with-data" => DownloadClientActions.DeleteWithData,
            "remove-refused-copy" => DownloadClientActions.RemoveRefusedCopy,
            "recheck" or "force-recheck" => DownloadClientActions.Recheck,
            // #810: queue priority/reorder and force-start.
            "move-to-top" or "movetotop" or "top" => DownloadClientActions.MoveToTop,
            "move-up" or "moveup" => DownloadClientActions.MoveUp,
            "move-down" or "movedown" => DownloadClientActions.MoveDown,
            "move-to-bottom" or "movetobottom" or "bottom" => DownloadClientActions.MoveToBottom,
            "force-start" or "forcestart" => DownloadClientActions.ForceStart,
            _ => null
        };

    private static string NormalizeHealth(string value)
        => value.Equals("healthy", StringComparison.OrdinalIgnoreCase) || value.Equals("ready", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : value.Equals("paused", StringComparison.OrdinalIgnoreCase) || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? "paused"
                : value.Equals("attention", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("degraded", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("untested", StringComparison.OrdinalIgnoreCase)
                    ? "degraded"
                    : value.Equals("unreachable", StringComparison.OrdinalIgnoreCase)
                        ? "down"
                        : "unknown";

    private static string NormalizeHistoryOutcome(DownloadDispatchItem dispatch)
    {
        if (dispatch.Failure is not null ||
            IsFailureStatus(dispatch.ImportStatus) ||
            IsFailureStatus(dispatch.GrabStatus) ||
            IsFailureStatus(dispatch.Status))
        {
            return "failed";
        }

        // Import is the latest durable lifecycle state. A dispatch can still
        // have Status=sent or GrabStatus=succeeded after the import callback
        // has completed, so history must not regress to the earlier stage.
        var importOutcome = NormalizeHistoryOutcomeValue(dispatch.ImportStatus);
        if (importOutcome is not "unknown")
        {
            return importOutcome == DownloadQueueStatuses.Completed
                ? DownloadQueueStatuses.Imported
                : importOutcome;
        }

        // GrabStatus only ever means "the client accepted the request" or "the
        // client refused it" (SqliteDownloadDispatchesRepository.RecordGrabAsync
        // writes only "succeeded" or "failed", and "failed" was already caught
        // above). It is never itself evidence that the transfer finished - that
        // needs detection or import - so "succeeded" must never read as history
        // "completed" here. A Transmission magnet that never got its metadata
        // was shown that way one second after being sent, with nothing left to
        // ever correct it (#650).
        if (string.Equals(dispatch.GrabStatus, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return "sent";
        }

        return NormalizeHistoryOutcomeValue(dispatch.Status);
    }

    private static string NormalizeHistoryOutcomeValue(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is "completed" or "succeeded" or "success") return DownloadQueueStatuses.Completed;
        if (normalized is "imported") return DownloadQueueStatuses.Imported;
        if (normalized.Contains("fail") || normalized.Contains("error")) return "failed";
        if (normalized.Contains("import")) return DownloadQueueStatuses.ImportReady;
        return normalized.Length == 0 ? "unknown" : normalized;
    }


}
