using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Processing;
using Deluno.Platform;
using Deluno.Connections.Data;
using Deluno.Recovery.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Recovery.Services;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Deluno.Quality;
using Deluno.Quality.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// The recurring maintenance and automation passes that used to live directly
/// on <see cref="DelunoHeartbeatWorker"/>. Each pass claims its own slot in the
/// jobs database via <see cref="IJobQueueRepository.TryClaimScheduledPassAsync"/>
/// rather than gating on an in-memory timestamp, so the schedule survives a
/// restart and two hosts sharing one database cannot both run the same pass in
/// the same window.
/// </summary>
public sealed class WorkPlanner(
    ILogger<WorkPlanner> logger,
    IJobQueueRepository jobQueueRepository,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// How many metadata refresh jobs of one type to keep queued. Bounded so a
    /// large backfill never writes the whole catalogue into the job table, and
    /// deep enough that the metadata lane is not left idle between top-ups.
    /// </summary>
    private const int MetadataQueueTargetDepth = 200;

    /// <summary>
    /// How often the backfill tops the queue up. Short, because it is now a
    /// top-up rather than the old fixed 30-per-pass allocation — a settled
    /// library finds nothing stale and queues nothing.
    /// </summary>
    private static readonly TimeSpan MetadataTopUpInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gives browsers, search-result caches and an in-flight metadata refresh
    /// time to stop using an old artwork URL before its file is reclaimed.
    /// </summary>
    private static readonly TimeSpan ArtworkCacheGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// How many files one processor may be holding before Deluno stops handing
    /// it more (#575).
    ///
    /// <para>A processor keeps each source at full size in the download client
    /// until it is finished with it, so an unbounded queue is an unbounded
    /// disk. What is not sent this pass is sent the next one, which is how a
    /// slow processor slows Deluno down instead of filling the disk.</para>
    /// </summary>
    internal const int MostFilesWithOneProcessor = 20;

    public Task RunDispatchCleanupAsync(
        IDispatchCleanupService cleanupService,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.DispatchCleanup,
            () => cleanupService.RunCleanupPassAsync(cancellationToken),
            "Dispatch cleanup pass failed.",
            cancellationToken);

    /// <summary>
    /// Puts titles back on the work list when the download they were waiting on
    /// is no longer happening.
    ///
    /// <para>Five minutes, which is how long a failed grab may sit on the shelf
    /// saying "Downloading" before it corrects itself. The seven-day expiry in
    /// <c>WantedStatuses.StuckDownloadAfter</c> sits behind this and catches the
    /// case where even this never runs.</para>
    ///
    /// <para>A named pass on the maintenance planner rather than a job type of
    /// its own: it enqueues nothing, it holds no lease worth speaking of, and
    /// DESIGN-002 rule 3 is emphatic that recurring work rides what already
    /// exists.</para>
    /// </summary>
    public Task RunDownloadStateReconcileAsync(
        IDownloadStateReconciler reconciler,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.DownloadState,
            () => reconciler.ReconcileAsync(cancellationToken),
            "Download state reconciliation failed.",
            cancellationToken);

    /// <summary>
    /// Says again what a processor did not hear about the files it handed back
    /// (#580). Deluno said each one once and gave up, while Weir retries its own
    /// reports to Deluno on a heartbeat: reliable in one direction only.
    /// </summary>
    public Task RunProcessorOutcomePassAsync(
        ProcessorOutcomeReporter reporter,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.ProcessorOutcomes,
            () => reporter.SendDueAsync(cancellationToken),
            "Processor outcome pass failed.",
            cancellationToken);

    /// <summary>
    /// Reads each indexer's newest releases and searches for any wanted title
    /// they are for (#587). Rides the automation lane with the search planner,
    /// because it is searching.
    /// </summary>
    public Task RunRssSyncPassAsync(
        RssSyncService rssSync,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.RssSync,
            () => rssSync.SyncAsync(cancellationToken),
            "RSS sync pass failed.",
            cancellationToken);

    public Task PlanMetadataRefreshAutomationAsync(
        IJobScheduler jobScheduler,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.MetadataRefresh,
            async () =>
            {
                var now = timeProvider.GetUtcNow();
                // Shared with the manual refresh endpoints, so the count they
                // report as still to go is the count this planner will actually
                // work through.
                var staleBefore = now - MetadataStalenessWindow.StaleAfter;
                var retryAttemptsBefore = now - MetadataStalenessWindow.AttemptCooldown;

                await TopUpMetadataQueueAsync(
                    jobScheduler,
                    jobType: "movies.metadata.refresh",
                    relatedEntityType: "movie",
                    fetchCandidates: take => movieCatalogRepository.ListStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, take, cancellationToken),
                    cancellationToken);

                await TopUpMetadataQueueAsync(
                    jobScheduler,
                    jobType: "series.metadata.refresh",
                    relatedEntityType: "series",
                    fetchCandidates: take => seriesCatalogRepository.ListStaleMetadataCandidatesAsync(staleBefore, retryAttemptsBefore, take, cancellationToken),
                    cancellationToken);
            },
            "Metadata refresh planning failed.",
            cancellationToken);

    /// <summary>
    /// Reclaims localized artwork after checking both catalogues for live URL
    /// references. This is a maintenance pass, not a second scheduler: it
    /// rides the existing maintenance lane and records its result in Activity.
    /// </summary>
    public Task RunArtworkCacheCleanupAsync(
        TmdbMetadataProvider metadataProvider,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.ArtworkCacheCleanup,
            async () =>
            {
            var referencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            referencedKeys.UnionWith(await movieCatalogRepository.ListReferencedArtworkCacheKeysAsync(cancellationToken));
            referencedKeys.UnionWith(await seriesCatalogRepository.ListReferencedArtworkCacheKeysAsync(cancellationToken));

            var result = await metadataProvider.CleanupArtworkCacheAsync(
                referencedKeys,
                timeProvider.GetUtcNow() - ArtworkCacheGrace,
                cancellationToken);

            logger.LogInformation(
                "Artwork cache cleanup scanned {ScannedCount} old entries, deleted {DeletedCount}, reclaimed {ReclaimedBytes} bytes, skipped {SkippedReferencedCount} referenced entries, and had {FailedCount} failures.",
                result.ScannedCount,
                result.DeletedCount,
                result.ReclaimedBytes,
                result.SkippedReferencedCount,
                result.FailedCount);

            await activityFeedRepository.RecordActivityAsync(
                "metadata.artwork.cleanup",
                result.DeletedCount > 0
                    ? $"Artwork cache cleanup reclaimed {result.ReclaimedBytes:N0} bytes from {result.DeletedCount} unreferenced entr{(result.DeletedCount == 1 ? "y" : "ies")}."
                    : "Artwork cache cleanup found no unreferenced artwork ready to remove.",
                JsonSerializer.Serialize(new
                {
                    result.ScannedCount,
                    result.DeletedCount,
                    result.ReclaimedBytes,
                    result.SkippedReferencedCount,
                    result.FailedCount,
                    GraceHours = ArtworkCacheGrace.TotalHours
                }, PayloadJsonOptions),
                null,
                "artwork-cache",
                null,
                cancellationToken);
            },
            "Artwork cache cleanup pass failed.",
            cancellationToken);

    /// <summary>
    /// Checks the files Deluno believes it holds are still there, and corrects
    /// itself where they are not.
    ///
    /// <para>The scan and the repair both already existed and nothing ever ran
    /// them, so a file deleted outside Deluno left the library reporting
    /// *Quality met* for ever — never searched for, and answering "you already
    /// have this" when asked why it would not download. DESIGN-007 decision
    /// 11.</para>
    ///
    /// <para>The check itself lives in <see cref="ILibraryFileCheckService"/>,
    /// which is also what the <b>Check library files now</b> button calls. All
    /// this adds is the schedule: the two paths cannot diverge, because there
    /// is only one of them.</para>
    /// </summary>
    /// <param name="everyHours">
    /// How often the user asked for it. The right answer depends on the disk:
    /// a local pool can afford hourly, and a NAS that spins up to answer should
    /// not be woken every hour to be asked.
    /// </param>
    public Task RunLibraryFileCheckAsync(
        ILibraryFileCheckService fileCheck,
        int everyHours,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.LibraryFileCheck,
            () => fileCheck.RunAsync(cancellationToken),
            "Library file check failed.",
            cancellationToken,
            SystemTasks.IntervalForHours(SystemTasks.LibraryFileCheck, everyHours));

    /// <summary>
    /// Clears the leftovers of a refused release, when the sharing rule no
    /// longer needs them.
    ///
    /// <para>The clearing itself lives in
    /// <see cref="IRefusedDownloadCleanupService"/>, which is also what the
    /// <b>Clean up now</b> button on a blocklist row calls. All this adds is
    /// the schedule.</para>
    /// </summary>
    public Task RunBlockedReleaseCleanupAsync(
        IRefusedDownloadCleanupService cleanup,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.BlockedReleaseCleanup,
            () => cleanup.CleanUpEverythingAsync(cancellationToken),
            "Blocked release cleanup failed.",
            cancellationToken);

    /// <summary>
    /// #888: gives up on an unmatched import nobody acted on within the
    /// chosen limit. A no-op unless "give up after N days" is the setting in
    /// force - the service itself checks that first, so this only adds the
    /// schedule.
    /// </summary>
    public Task RunUnmatchedImportGiveUpAsync(
        IUnmatchedImportGiveUpService giveUpService,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.UnmatchedImportGiveUp,
            () => giveUpService.RunAsync(cancellationToken),
            "Unmatched-import give-up pass failed.",
            cancellationToken);

    public Task RunRecycleBinCleanupAsync(
        IRecycleBinService recycleBinService,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.RecycleBinCleanup,
            async () =>
            {
                var removed = await recycleBinService.CleanupAsync(cancellationToken);
                if (removed > 0)
                {
                    logger.LogInformation("Recycle-bin cleanup permanently removed {RemovedCount} expired or over-capacity item(s).", removed);
                }
            },
            "Recycle-bin cleanup pass failed.",
            cancellationToken);

    /// <summary>
    /// #932: the scheduled half of the leftover-downloads list on Queue. The
    /// service itself reads the enabled switch and age threshold, so a run
    /// while it is switched off is simply a pass that removes nothing.
    /// </summary>
    public Task RunLeftoverDownloadCleanupAsync(
        ILeftoverDownloadCleanupService leftoverCleanup,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.DownloadLeftoverCleanup,
            async () =>
            {
                var result = await leftoverCleanup.RunAsync(cancellationToken);
                if (result.Removed > 0)
                {
                    logger.LogInformation("Leftover-download cleanup removed {RemovedCount} item(s); {HeldCount} held back by a hold condition.", result.Removed, result.Held);
                }
            },
            "Leftover-download cleanup pass failed.",
            cancellationToken);

    /// <summary>
    /// Keeps a metadata queue topped up to <see cref="MetadataQueueTargetDepth"/>
    /// rather than queueing a fixed number per pass.
    ///
    /// The old shape queued 30 per type every 6 hours, which is fine for a
    /// settled library and hopeless for a backlog: 20,000 freshly imported
    /// movies would have taken 667 passes -- about 167 days -- to get their
    /// metadata. Topping up instead means the backfill runs continuously and
    /// finishes in the time it takes the metadata lane to drain, while a
    /// settled library queues nothing at all because nothing is stale.
    ///
    /// Depth is bounded deliberately: queueing all 20,000 at once would write
    /// 20,000 rows and hand the lane a backlog it cannot reason about. The
    /// drain rate is set by the metadata lane's own concurrency, which is what
    /// bounds outbound provider traffic -- see #163 for pacing that properly.
    /// </summary>
    private async Task TopUpMetadataQueueAsync(
        IJobScheduler jobScheduler,
        string jobType,
        string relatedEntityType,
        Func<int, Task<IReadOnlyList<MetadataRefreshCandidate>>> fetchCandidates,
        CancellationToken cancellationToken)
    {
        var active = await jobQueueRepository.CountActiveJobsAsync(jobType, cancellationToken);
        var room = MetadataQueueTargetDepth - active;
        if (room <= 0)
        {
            return;
        }

        var candidates = await fetchCandidates(room);
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            // EnqueueAsync dedupes against an existing active job for the same
            // entity, so re-selecting something already queued is a no-op
            // rather than a duplicate.
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: jobType,
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        candidate.Id,
                        candidate.Title,
                        candidate.Year,
                        scheduled = true
                    }),
                    RelatedEntityType: relatedEntityType,
                    RelatedEntityId: candidate.Id),
                cancellationToken);
        }

        logger.LogInformation(
            "Metadata backfill queued {Queued} {JobType} job(s); {Active} were already active (target depth {Target}).",
            candidates.Count,
            jobType,
            active,
            MetadataQueueTargetDepth);
    }

    /// <summary>
    /// Queues a slice of the media probe, for each kind, when one is due.
    ///
    /// <para>Its own claim on the shared heartbeat — no timer, no worker, no
    /// dependency on any other pass. The handler re-queues itself while there
    /// is more to read, so this only has to start it.</para>
    /// </summary>
    public Task PlanMediaProbeAsync(
        IJobScheduler jobScheduler,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.MediaProbe,
            async () =>
            {
                foreach (var entity in new[] { "movie", "series" })
                {
                    await jobScheduler.EnqueueAsync(
                        new EnqueueJobRequest(
                            JobType: "library.media.probe",
                            Source: "system",
                            PayloadJson: null,
                            RelatedEntityType: entity,
                            RelatedEntityId: null,
                            DedupeKey: $"media-probe:{entity}"),
                        cancellationToken);
                }
            },
            "Media probe planning failed.",
            cancellationToken);

    public Task PlanIntakeAutomationAsync(
        IIntakeSyncService intakeSyncService,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.IntakeAutomation,
            () => intakeSyncService.PlanDueSyncJobsAsync(cancellationToken),
            "Intake automation planning failed.",
            cancellationToken);

    /// <summary>
    /// Folds monitored movie collections into the existing automation cycle.
    /// The collection repository claims its own due rows, while the resulting
    /// jobs run on the existing movie-search lane; there is deliberately no
    /// second timer or worker lane for franchises.
    /// </summary>
    public async Task PlanMovieCollectionSyncAsync(
        IJobScheduler jobScheduler,
        IMovieCollectionsRepository collectionsRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = await collectionsRepository.ClaimDueAsync(
            now,
            TimeSpan.FromHours(24),
            cancellationToken);

        foreach (var collection in due)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: MovieCollectionJobTypes.Sync,
                    Source: "collections",
                    PayloadJson: JsonSerializer.Serialize(new { collectionId = collection.Id }, PayloadJsonOptions),
                    RelatedEntityType: "movie-collection",
                    RelatedEntityId: collection.Id,
                    IdempotencyKey: $"movie-collection.sync:{collection.Id}:{now:yyyyMMddHH}",
                    DedupeKey: $"movie-collection.sync:{collection.Id}"),
                cancellationToken);
        }

        if (due.Count > 0)
        {
            logger.LogInformation(
                "Queued {CollectionCount} monitored movie collection refresh job(s).",
                due.Count);
        }
    }

    /// <summary>
    /// How long an active import run may go without progress before it is
    /// assumed abandoned and re-queued. Long enough that a slice in flight is
    /// never re-queued underneath itself (a slice is capped at 20 seconds, and
    /// the job lease at two minutes), short enough that a user who restarted
    /// the app sees the import pick up again rather than sit there.
    /// </summary>
    private static readonly TimeSpan ImportRunIdleBeforeResume = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Re-queues import runs that stopped without finishing.
    ///
    /// This is what makes an import survive a restart. The run row holds the
    /// position, so re-queueing costs one slice of replayed work at most --
    /// every import write is an upsert, so replaying it changes nothing. Paused
    /// runs are left alone; they are idle because somebody asked them to be.
    ///
    /// Bounded by construction: there is at most one active run per library, and
    /// the query is capped regardless.
    /// </summary>
    public Task PlanLibraryImportResumeAsync(
        IJobScheduler jobScheduler,
        IExistingLibraryImportService importService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunScheduledPassAsync(
            SystemTasks.LibraryImportResume,
            async () =>
            {
            var idleBefore = timeProvider.GetUtcNow() - ImportRunIdleBeforeResume;
            var stalled = await importService.ListResumableRunsAsync(idleBefore, 25, cancellationToken);

            foreach (var run in stalled)
            {
                logger.LogInformation(
                    "Resuming library import {RunId} for {LibraryName} from item {ProcessedCount}.",
                    run.RunId,
                    run.LibraryName,
                    run.ProcessedCount);

                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "library.import.existing",
                        Source: "library-import-resume",
                        PayloadJson: JsonSerializer.Serialize(new { run.RunId, run.LibraryId }),
                        RelatedEntityType: "library",
                        RelatedEntityId: run.LibraryId,
                        DedupeKey: LibraryImportSliceOutcome.ContinuationDedupeKey(run.RunId, run.ProcessedCount)),
                    cancellationToken);
            }
            },
            "Library import resume pass failed.",
            cancellationToken);

    /// <summary>
    /// Lets go of downloads that have finished sharing, and records what is
    /// still being held (#288).
    ///
    /// Runs after the import pass, over items the client still holds. Each one
    /// is measured against the rule its search source carries — the global rule
    /// with that source's override laid over it — and reclaimed only when the
    /// obligation is discharged. Anything still sharing is left exactly where it
    /// is; anything Deluno does not recognise is never touched, because an item
    /// with no dispatch behind it was not put there by Deluno.
    ///
    /// The pass also writes down what it decided, because "why is my drive
    /// full" is a question a user should be able to answer from the dashboard
    /// rather than by opening a torrent client. Storing the evaluator's own
    /// sentence — rather than recomputing one for display — is what stops the
    /// explanation and the action from ever disagreeing.
    /// </summary>
    /// <summary>
    /// The library's own name for what a download was matched to ("Big Buck Bunny (2008)"; a show for an episode), or null
    /// when it is matched to nothing, so the sentence falls back to the release name (#1098).
    /// </summary>
    private static async Task<string?> LibraryTitleOfAsync(
        DispatchCatalogueLink dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dispatch.EntityId))
            {
                var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
                return movie is null || string.IsNullOrWhiteSpace(movie.Title) ? null : SeedingCleanupActivity.LibraryTitle(movie.Title, movie.ReleaseYear);
            }

            var seriesId = await CatalogueImportNaming.SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            if (seriesId is null)
            {
                return null;
            }

            var series = await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken);
            return series is null || string.IsNullOrWhiteSpace(series.Title) ? null : SeedingCleanupActivity.LibraryTitle(series.Title, series.StartYear);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The sentence is only wording: a title that cannot be read must never stop a clean-up from being written down.
            return null;
        }
    }

    public async Task PlanSharingReclaimAsync(
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IPlatformSettingsRepository platformSettingsRepository,
        IConnectionsRepository connectionsRepository,
        ILibrariesRepository librariesRepository,
        IActivityFeedRepository activityFeedRepository,
        IDownloadSharingRepository sharingRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        SharingReclaimService reclaimService,
        IHardlinkInspector hardlinkInspector,
        IDownloadHealthRepository healthRepository,
        Deluno.Integrations.DownloadClients.IDownloadHealthConnectivityCheck connectivityCheck,
        CancellationToken cancellationToken,
        ISharingRulesRepository? sharingRulesRepository = null,
        ISharingActivityTracker? sharingActivityTracker = null,
        IProcessorRepository? processorRepository = null)
    {
        await RunScheduledPassAsync(
            SystemTasks.SharingReclaim,
            async () =>
            {
        var settings = await platformSettingsRepository.GetAsync(cancellationToken);

        // #824: the same person-configured reachability guard the
        // strike/blocklist executor already honours - a failed check skips
        // this pass entirely, so nothing is reclaimed while it lasts.
        if (settings.DownloadHealthConnectivityCheckEnabled &&
            !await connectivityCheck.AnyReachableAsync(settings.DownloadHealthConnectivityCheckUrls, cancellationToken))
        {
            await activityFeedRepository.RecordActivityAsync(
                "download.health.connectivity-check-skipped",
                "Connectivity check found no reachable URL; the seeding reclaim pass was skipped and nothing was reclaimed.",
                null,
                null,
                "download_client",
                "connectivity-check",
                cancellationToken);
            return;
        }

        var globalPolicy = new SharingPolicy(
            settings.SharingMode,
            settings.SharingForHours,
            settings.SharingUntilRatio,
            settings.SharingStuckAction,
            settings.SharingStuckAfterDays,
            settings.SharingCompletionAction,
            settings.SharingPrivacyScope);
        // #814: one sandbox switch, shared with the strike/blocklist executors.
        var dryRun = settings.DownloadHealthDryRunEnabled;
        // #817: the same ignore list the strike/blocklist executor already
        // honours - a download matching it is exempt from every health rule,
        // seeding reclaim included.
        var ignoreList = await healthRepository.ListDownloadHealthIgnoreEntriesAsync(cancellationToken);

        // Nothing to do at all when the user has told Deluno to keep its hands
        // off, and no reason to read telemetry to find that out. The stored
        // picture ages out on its own, so the dashboard stops claiming to be
        // holding anything within a pass or two of the mode changing.
        if (string.Equals(SharingPolicy.NormalizeMode(globalPolicy.Mode), SharingPolicy.ModeLeaveAlone, StringComparison.Ordinal))
        {
            return;
        }

        DownloadTelemetryOverview overview;
        try
        {
            overview = await downloadClientTelemetryService.GetFreshOverviewAsync(DownloadTelemetryFreshness.Acting, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sharing reclaim pass could not read download client telemetry.");
            return;
        }

        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var indexersByName = indexers
            .GroupBy(indexer => indexer.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // A dispatch is tied to its indexer by id (#1077), so renaming the indexer
        // does not lose the sharing rule that belongs to it. The name is only the
        // way back for a dispatch whose source matched no indexer.
        var indexersById = indexers
            .GroupBy(indexer => indexer.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var librariesById = libraries.ToDictionary(library => library.Id, StringComparer.OrdinalIgnoreCase);

        // The rule matrix (#820), read once per pass rather than once per
        // item — it rarely changes, and a pass can hold hundreds of items.
        IReadOnlyList<SharingRuleItem> sharingRules = sharingRulesRepository is null
            ? []
            : await sharingRulesRepository.ListAsync(cancellationToken);

        // What the clients are still holding, for the dashboard to explain.
        var holds = new List<DownloadSharingHold>();
        string? driveNote = null;

        // #819/#926: real per-title file paths, read once per library rather
        // than once per hold. The heuristic below (same drive, hardlinks on)
        // still decides the drive-layout note; this is what catches the case
        // it cannot see - a library copy that was upgraded or deleted out
        // from under a download that is still, physically, a separate copy.
        var trackedFiles = new SharingTrackedFileCache(movieCatalogRepository, seriesCatalogRepository);

        foreach (var item in overview.Clients.SelectMany(client => client.Queue))
        {
            if (!string.Equals(item.Status, DownloadQueueStatuses.Imported, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only items Deluno itself dispatched. Something a user added by
            // hand is theirs, and reclaiming it would be Deluno deleting a file
            // it was never asked to manage.
            var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(item.ClientId, item.Id, item.ReleaseName, cancellationToken);
            if (dispatch is null)
            {
                continue;
            }

            var source = !string.IsNullOrWhiteSpace(dispatch.IndexerId) && indexersById.TryGetValue(dispatch.IndexerId, out var linkedIndexer)
                ? linkedIndexer
                : indexersByName.GetValueOrDefault(dispatch.IndexerName ?? string.Empty);

            // #817: exempt from seeding reclaim the same way it is exempt from
            // every strike/blocklist rule.
            if (ignoreList.Any(entry => entry.Matches(item.Id, item.Category, item.IndexerName, item.Tags, item.TrackerHosts,
                (source?.Tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))))
            {
                continue;
            }

            var outcome = await reclaimService.ReconcileAsync(
                new SharingReclaimCandidate(
                    item.Id,
                    item.ClientId,
                    item.ClientName,
                    item.Title,
                    item.Protocol,
                    item.Ratio,
                    item.SeedingMinutes,
                    item.IsPrivate,
                    item.ReleaseName,
                    item.Category,
                    Seeders: item.Peers,
                    IsActiveNow: item.SpeedMbps > 0 || item.UploadSpeedMbps > 0),
                globalPolicy,
                source,
                dryRun,
                cancellationToken,
                sharingRules,
                sharingActivityTracker);

            if (outcome.WouldReclaim)
            {
                await activityFeedRepository.RecordActivityOnChangeAsync(
                    "download.sharing.reclaim-previewed",
                    $"[Dry run] Would remove {item.Title} from {item.ClientName}: it finished sharing. {outcome.Reason}",
                    null,
                    null,
                    dispatch.EntityType,
                    dispatch.EntityId,
                    cancellationToken);
                continue;
            }

            if (outcome.Reclaimed)
            {
                await activityFeedRepository.RecordActivityOnChangeAsync(
                    SeedingCleanupActivity.Category,
                    SeedingCleanupActivity.Message(await LibraryTitleOfAsync(dispatch, movieCatalogRepository, seriesCatalogRepository, cancellationToken) ?? item.Title, item.ClientName),
                    System.Text.Json.JsonSerializer.Serialize(new { reason = outcome.Reason }),
                    null,
                    dispatch.EntityType,
                    dispatch.EntityId,
                    cancellationToken);
                continue;
            }

            if (outcome.Stopped)
            {
                // Discharged the same as a reclaim, but the entry and its
                // files stay in the client — nothing about the library, and
                // nothing left for this pass to keep tracking (#821).
                await activityFeedRepository.RecordActivityOnChangeAsync(
                    "download.sharing.stopped",
                    $"{item.Title} finished sharing. Seeding was stopped on {item.ClientName}, and the download was left in place. {outcome.Reason}",
                    null,
                    null,
                    dispatch.EntityType,
                    dispatch.EntityId,
                    cancellationToken);
                continue;
            }

            if (outcome.Warning is not null)
            {
                logger.LogWarning("Sharing reclaim for {Title} did not complete: {Warning}", item.Title, outcome.Warning);
            }

            // Everything that survived the pass is still on the disk. Where the
            // client put it decides whether that costs anything: the library's
            // copy and this one may be the same file data, in which case saying
            // it uses gigabytes would be a lie.
            //
            // The library comes from the dispatch rather than the queue item:
            // telemetry stops resolving a library once an import job has claimed
            // an item, which is precisely the state everything here is in.
            librariesById.TryGetValue(
                string.IsNullOrWhiteSpace(dispatch.LibraryId) ? item.LibraryId ?? string.Empty : dispatch.LibraryId,
                out var library);
            var downloadPath = item.SourcePath ?? library?.DownloadsPath;
            var libraryPath = library?.RootPath;
            var heuristicShares = SharingFootprint.SharesOneCopy(downloadPath, libraryPath, settings.UseHardlinks);

            // A download the processor cleaned was never linked to the library: the library's file is the
            // processor's output copy, and this one is the untouched original beside it. That is the normal
            // result of refine-before-import, not a link that was lost, so it is never "unlinked" (it costs its
            // own space, which is what SharesLibraryCopy = false already says).
            if (heuristicShares && processorRepository is not null)
            {
                var handoff = await processorRepository.FindProcessorHandoffForDownloadAsync(item.ClientId, item.Id, cancellationToken);
                if (handoff is not null && string.Equals(handoff.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase))
                {
                    heuristicShares = false;
                }
            }

            // The heuristic only ever asked "could this be one copy" from
            // configuration. Where it says yes, check the actual files: gone
            // or replaced (false) downgrades it, uncertain (null) - most often
            // the exact tracked file could not be resolved - leaves it as the
            // heuristic said, same as before this existed.
            bool sharesLibraryCopy = heuristicShares;
            string? unlinkedDetail = null;
            if (heuristicShares)
            {
                var exactLibraryPath = await trackedFiles.ResolveAsync(dispatch, library?.Id, cancellationToken);
                var actuallyLinked = exactLibraryPath is null ? (bool?)null : hardlinkInspector.StillLinked(downloadPath, exactLibraryPath);
                if (actuallyLinked == false)
                {
                    sharesLibraryCopy = false;
                    unlinkedDetail = "No longer shared with your library - the library copy was upgraded or removed.";
                }
            }

            holds.Add(new DownloadSharingHold(
                item.ClientId,
                item.ClientName,
                item.Id,
                // The catalogue's title, not the release string. Somebody
                // reading a dashboard to find out what is holding their disk
                // should see "Big Buck Bunny", not
                // "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO".
                await ResolveCatalogueTitleAsync(
                    dispatch,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    item.Title,
                    cancellationToken),
                unlinkedDetail ?? outcome.Detail ?? outcome.Reason,
                Math.Max(0, item.SizeBytes),
                NeedsYou: outcome.Action == SharingAction.Ask || outcome.Warning is not null,
                SharesLibraryCopy: sharesLibraryCopy,
                LostHardlink: unlinkedDetail is not null));

            driveNote ??= SharingFootprint.Describe(downloadPath, libraryPath, settings.UseHardlinks);
        }

        await sharingRepository.ReplaceHoldsAsync(holds, driveNote, cancellationToken);
            },
            "Sharing reclaim pass failed.",
            cancellationToken);
    }

    /// <summary>
    /// The title a person recognises, falling back to whatever the download
    /// client called it when the catalogue no longer has the item — a title
    /// deleted while its download was still sharing, most obviously.
    /// </summary>
    private async Task<string> ResolveCatalogueTitleAsync(
        DispatchCatalogueLink dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return fallback;
        }

        try
        {
            if (string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
                return string.IsNullOrWhiteSpace(movie?.Title) ? fallback : movie.Title;
            }

            if (CatalogueImportNaming.IsShowGrab(dispatch))
            {
                var seriesId = await CatalogueImportNaming.SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
                var series = seriesId is null ? null : await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken);
                return string.IsNullOrWhiteSpace(series?.Title) ? fallback : series.Title;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve a catalogue title for dispatch {DispatchId}.", dispatch.DispatchId);
        }

        return fallback;
    }

    /// <param name="availability">
    /// Which libraries Deluno can act on. A library whose root is not mounted
    /// is paused rather than imported into: attempting the move would fail for
    /// every title in it and record each failure against the release rather
    /// than against the drive. DESIGN-007 decision 12.
    /// </param>
    public async Task PlanImportAutomationAsync(
        IJobScheduler jobScheduler,
        IProcessorRepository processorRepository,
        ILibrariesRepository librariesRepository,
        ILibraryAvailabilityService availability,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IProcessorConnectionService processorConnectionService,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        // Checkpoint A (#917). Optional and appended last so every existing
        // caller and test keeps working unchanged: the checkpoint simply does
        // not run when these are not supplied, exactly like the setting being
        // off.
        IPlatformSettingsRepository? downloadIntegrityPlatformSettings = null,
        IMediaProbeService? downloadIntegrityProbeService = null,
        IBlockedReleaseRepository? downloadIntegrityBlockedReleases = null,
        IQualityRepository? downloadIntegrityQualityRepository = null,
        // #1148: lets a failed file check be recorded on the download itself,
        // so Transfers agrees with Activity. Optional like the rest.
        IDownloadDispatchesRepository? downloadIntegrityDispatches = null,
        // #1155: lets a hand-off that has waited past its limit import the finished copy sitting in the ready folder.
        IProcessorOutputImportQueue? processorOutputImports = null,
        // #1242: lets a problem closed because its file was imported take its System row with it at once.
        IHealthCheckRepository? healthChecks = null,
        IRealtimeEventPublisher? realtime = null)
    {
        await RunScheduledPassAsync(
            SystemTasks.ImportAutomation,
            async () =>
            {
        var now = timeProvider.GetUtcNow();

        var allLibraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var usable = await availability.ReadAsync(allLibraries, cancellationToken);
        IReadOnlyList<LibraryItem> libraries = allLibraries.Where(library => usable.IsUsable(library.Id)).ToArray();
        if (libraries.Count == 0)
        {
            return;
        }

        var routeCategoriesByLibrary = await LoadRouteCategoriesAsync(
            libraries,
            librariesRepository,
            cancellationToken);

        var maintenancePlanningBatchSize = configuration.GetValue("Deluno:Worker:MaintenancePlanningBatchSize", 600);
        var existingJobs = await jobQueueRepository.ListAsync(maintenancePlanningBatchSize, cancellationToken);
        var knownImportSources = existingJobs
            // A live or completed import reserves its source. A dead-letter
            // import does not: after the user repairs the source or sends a
            // fresh release, permanently reserving that path makes recovery
            // impossible. Enqueue's payload/dedupe key still prevents two live
            // jobs for the same request.
            .Where(job =>
                job.JobType == "filesystem.import.execute" &&
                !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase))
            .Select(job => TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // How much each processor is already holding, so this pass can stop
        // before it buries one (#575).
        var withProcessor = await processorRepository.ListProcessorHandoffsWithStatusAsync(
            ProcessorHandoffStatuses.WithProcessor,
            cancellationToken);

        // A hand-off for a download Deluno has already imported is finished
        // work, not work in progress (#1155): closed here so it neither sits
        // on the Dashboard as "working" nor is sent again.
        var openHandoffs = new List<ProcessorHandoffItem>(withProcessor.Count);
        foreach (var handoff in withProcessor)
        {
            if (await ImportedDownloads.WasImportedAsync(jobQueueRepository, handoff.ClientId, handoff.ReleaseName, cancellationToken))
            {
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Completed, null, null, null, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "processing.handoff.closed-imported",
                    $"{handoff.ReleaseName} was already imported, so Deluno closed its hand-off to the processor.",
                    JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.ReleaseName, handoff.ClientId, PreviousStatus = handoff.Status }, PayloadJsonOptions),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                continue;
            }

            openHandoffs.Add(handoff);
        }

        var inFlightByProcessor = openHandoffs
            .GroupBy(handoff => handoff.ProcessorConnectionId ?? handoff.ProcessorName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        // Hand-offs not sent yet, by the download they are for, so a download
        // its client has moved since is sent from where it is now (#1150).
        var unsentByDownload = openHandoffs
            .Where(handoff => string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase))
            .GroupBy(handoff => $"{handoff.ClientId}\u001f{handoff.QueueItemId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var recentWaiting = await activityFeedRepository.ListActivityAsync(150, null, null, cancellationToken);
        await ReconcileMatchedProcessorOutputsAsync(
            jobScheduler,
            processorRepository,
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            allLibraries,
            existingJobs,
            cancellationToken);
        await RecordUnmatchedProcessorOutputsAsync(
            processorRepository,
            jobQueueRepository,
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            knownImportSources,
            recentWaiting,
            cancellationToken);
        // Problems whose title has its file now close themselves, with a line each (#1167).
        await new HandledProblemsSettler(
                movieCatalogRepository,
                seriesCatalogRepository,
                jobQueueRepository,
                downloadIntegrityDispatches,
                activityFeedRepository,
                processorRepository,
                processorConnectionService,
                healthChecks: healthChecks,
                realtime: realtime)
            .SettleAsync(cancellationToken);
        await new ProcessorHandoffWatcher(
                processorRepository,
                processorConnectionService,
                activityFeedRepository,
                movieCatalogRepository,
                seriesCatalogRepository,
                processorOutputImports,
                downloadIntegrityDispatches)
            .WatchAsync(libraries, now, cancellationToken, allLibraries);

        var recentWaitingKeys = recentWaiting
            .Where(item => item.Category == "processing.waiting" && item.CreatedUtc > now.AddHours(-6))
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var telemetry = await downloadClientTelemetryService.GetFreshOverviewAsync(DownloadTelemetryFreshness.Acting, cancellationToken);
        await downloadClientTelemetryService.RunConfiguredHealthRemediationAsync(telemetry, cancellationToken);
        await CloseContainedSendsAsync(telemetry, downloadIntegrityDispatches, cancellationToken);
        foreach (var item in GetImportCandidates(telemetry))
        {
            // `waitingForProcessor` belongs here too, and leaving it out deadlocked the
            // Processing stage. GetOverviewAsync rewrites a finished download in a
            // refine-before-import library to that status so the dashboard can show the
            // stage — and this loop reads the same enriched snapshot. So the status that
            // made the stage appear was the status that stopped the hand-off being
            // created, and the item sat in Processing for ever.
            //
            // It is the right set to accept: that status is only ever produced for a
            // completed download in a library that refines before importing, which is
            // exactly what needs handing to a processor.
            if (item.Status is not ("importReady" or "completed" or "waitingForProcessor" or "notHandedOver"))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                continue;
            }

            // A download client can report completion just before the final
            // copy or rename has released the file. Keep the item visible to
            // telemetry, but do not create a hand-off or import job until the
            // path is stable and readable. This is a last safety net behind
            // the client's own settled status (#912): logged only when it is
            // actually what is holding the import back.
            if (File.Exists(item.SourcePath) && !ProcessorOutputReadiness.IsReady(item.SourcePath))
            {
                await TellHeldByFileCheckOnceAsync(activityFeedRepository, item, cancellationToken);
                continue;
            }

            var sourceKey = NormalizeSourceKey(item.SourcePath);
            var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(
                item.ClientId,
                item.Id,
                item.ReleaseName,
                cancellationToken);

            if (HasImportReservation(existingJobs, sourceKey, dispatch))
            {
                continue;
            }

            // A download whose title or library was removed after the grab is left
            // for a person: importing it would bring back, under the release name,
            // a title someone removed on purpose.
            // Unless the title has come back (#1155): removed, restored and added again, it is
            // the same download for the same name and year, and belongs to the title that is there.
            var titleCameBack = false;
            if (await CatalogueImportNaming.RemovedSinceGrabAsync(dispatch, allLibraries, movieCatalogRepository, seriesCatalogRepository, cancellationToken) is { } removed)
            {
                var readded = await CatalogueImportNaming.FindReAddedTitleAsync(dispatch, item.ReleaseName, movieCatalogRepository, seriesCatalogRepository, cancellationToken);
                if (readded is null)
                {
                    await TellNotImportedOnceAsync(activityFeedRepository, dispatch!.DispatchId, $"{item.Title} finished in {item.ClientName}, but {removed}, so Deluno did not import it. Remove it from {item.ClientName}, or import it by hand if you still want it.", item, cancellationToken);
                    continue;
                }

                dispatch = readded;
                titleCameBack = true;
            }

            var library = ResolveLibraryForQueueItem(item, libraries, routeCategoriesByLibrary);
            if (library is null)
            {
                continue;
            }

            // A download whose file Deluno has already imported is never handed
            // over again (#1155), whatever a stale hand-off says.
            if (ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                await ImportedDownloads.WasImportedAsync(jobQueueRepository, item.ClientId, item.ReleaseName, cancellationToken))
            {
                continue;
            }

            // The client may have moved the download since a hand-off for it
            // was made, and a retry re-reads where it is now (#1150): the path
            // is always the one the download is in at hand-off time.
            if (ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                unsentByDownload.TryGetValue($"{item.ClientId}\u001f{item.Id}", out var unsent) &&
                !string.Equals(NormalizeSourceKey(unsent.SourcePath), sourceKey, StringComparison.OrdinalIgnoreCase))
            {
                await processorRepository.RepointWaitingProcessorHandoffAsync(unsent.Id, item.SourcePath, cancellationToken);
            }

            // A download that has been dealt with is never handed over again, whatever path it is
            // at now (#1155). The hand-off is found by the client's own id for the download, not
            // by where its files are: a client moves a finished download, and a path match then
            // missed a hand-off a person had dismissed and sent the download to the processor a
            // second time. Nor is it handed over while another copy of the same release holds a
            // hand-off, or when its title already holds the file from another download.
            if (ImportWorkflows.HandsToProcessor(library.ImportWorkflow) && !titleCameBack &&
                await HasBeenDealtWithAsync(item, processorRepository, processorOutputImports, cancellationToken, jobQueueRepository))
            {
                continue;
            }

            // Checkpoint A (#917): before this download reaches a processor or
            // the library, Deluno opens the file and checks it against what its
            // own name and the catalogue's known runtime claim. Off by default
            // for every install (#936) -- nothing here runs until the setting
            // is turned on.
            if (await RunDownloadIntegrityCheckpointAsync(
                    item,
                    dispatch,
                    library,
                    downloadIntegrityPlatformSettings,
                    downloadIntegrityProbeService,
                    downloadIntegrityBlockedReleases,
                    downloadIntegrityQualityRepository,
                    downloadIntegrityDispatches,
                    downloadClientTelemetryService,
                    jobScheduler,
                    activityFeedRepository,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    cancellationToken))
            {
                continue;
            }

            // A hand-off released to import the original leaves the processor out
            // of it: the download goes through the ordinary import below, with
            // the same catalogue naming as any other (#511).
            var released = false;
            ProcessorHandoffItem? existing = null;
            if (ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                existing = await processorRepository.FindProcessorHandoffAsync(library.Id, null, item.SourcePath, cancellationToken);
                released = existing?.Status == ProcessorHandoffStatuses.Released;

                if (released)
                {
                    // #953: whoever released this -- the auto give-up path
                    // above, or a person clicking "Import the original" on a
                    // recovery case -- the processor still had a working
                    // folder for it. That folder's scraps and now-empty
                    // subfolders belong to this abandoned hand-off, not to
                    // the fresh import about to run below, and are cleaned
                    // up here once, keeping any complete file #921's Health
                    // row still offers.
                    ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
                        ExpectedOutputDirectoryNames(existing!.SourcePath, library.DownloadsPath),
                        library.ProcessorOutputPath,
                        ProcessorOutputReadiness.IsReady,
                        allLibraries);
                }
            }

            // A processor is given what it can hold, not everything at once.
            //
            // This loop used to create and submit a hand-off for every finished
            // download in the pass: five hundred completed downloads were five
            // hundred submissions, and every one of their sources stayed at
            // full size in the download client until the processor got to it.
            // A slow processor filled the disk (#575). What is not sent this
            // pass is sent the next one.
            if (!released &&
                ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                existing is null &&
                inFlightByProcessor.GetValueOrDefault(library.ProcessorConnectionId ?? library.ProcessorName ?? string.Empty) >= MostFilesWithOneProcessor)
            {
                continue;
            }

            // Files that are not there are not handed to a processor (#1210). The download reads "Files are
            // gone" from the same check Transfers uses (#1200), with the same row and actions, instead of
            // waiting for the processor to say so many minutes later.
            if (!released && ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                DownloadFilesGone.AreGoneOnDisk(item.SourcePath))
            {
                continue;
            }

            if (!released && ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                var handoff = await processorRepository.EnsureProcessorHandoffAsync(
                    new CreateProcessorHandoffRequest(
                        library.Id,
                        library.MediaType,
                        item.ClientId,
                        item.Id,
                        item.ReleaseName,
                        item.SourcePath,
                        library.ProcessorName,
                        // A hand-off closed because its title was removed belongs to that acquisition; the
                        // title being back is a new one, though the client's download is the old one (#1155).
                        (titleCameBack && existing is { Status: ProcessorHandoffStatuses.Cancelled or ProcessorHandoffStatuses.Dismissed })
                            || (existing is { Status: ProcessorHandoffStatuses.Completed or ProcessorHandoffStatuses.Dismissed }
                                && await ImportedDownloads.FinishedWithItsImportAsync(jobQueueRepository, existing, cancellationToken)
                                && await ImportedDownloads.SentAgainSinceAsync(jobQueueRepository, item.ClientId, item.ReleaseName, existing.UpdatedUtc, cancellationToken))
                            ? now
                            : item.AddedUtc,
                        library.ProcessorConnectionId),
                    cancellationToken);
                // The processor was busy with this file for another connection (#1250): it is sent again after its
                // back-off, not on every pass.
                if (handoff.Status == ProcessorHandoffStatuses.Waiting
                    && ProcessorNotTaken.IsBusyMessage(handoff.FailureMessage)
                    && ProcessorNotTaken.RetryNotDue(
                        handoff,
                        ProcessorNotTaken.TriesSoFar(await activityFeedRepository.ListActivityAsync(200, "processor-handoff", handoff.Id, cancellationToken)),
                        now))
                {
                    continue;
                }

                var currentHandoff = handoff;
                ProcessorSubmissionResult? submission = null;
                var connection = await processorRepository.FindProcessorConnectionAsync(library.ProcessorConnectionId, library.ProcessorName, cancellationToken);
                if (connection is { IsEnabled: true } && handoff.Status == "waiting")
                {
                    // Try again sends from Transfers at once (#1253); whoever holds a hand-off's lease sends it, and the
                    // other leaves it. Once the lease is held the hand-off is read again: one that has moved on is not sent.
                    using var lease = ProcessorHandoffSends.TryBegin(handoff.Id);
                    if (lease is null)
                    {
                        continue;
                    }

                    var fresh = await processorRepository.GetProcessorHandoffAsync(handoff.Id, cancellationToken);
                    if (fresh is null || fresh.Status != "waiting")
                    {
                        continue;
                    }

                    handoff = fresh;
                    (currentHandoff, submission) = await ProcessorHandoffSubmission.SubmitAsync(
                        handoff, library, connection, item.Title, dispatch?.DispatchId,
                        processorRepository, processorConnectionService, activityFeedRepository, cancellationToken);
                }
                var waitKey = handoff.Id;
                if (currentHandoff.Status != "failed" && recentWaitingKeys.Add(waitKey))
                {
                    var waitingMessage = submission?.IsAccepted == true
                        ? $"Deluno submitted {item.Title} to {connection!.Name} and is waiting for a cleaned output."
                        : $"{item.Title} is complete in {item.ClientName}; Deluno is waiting for {library.ProcessorName ?? "the configured processor"} to produce a cleaned output.";
                    await activityFeedRepository.RecordActivityOnChangeAsync(
                        "processing.waiting",
                        waitingMessage,
                        JsonSerializer.Serialize(new
                        {
                            DispatchId = dispatch?.DispatchId,
                            item.ClientId,
                            item.ClientName,
                            item.ReleaseName,
                            item.SourcePath,
                            HandoffId = handoff.Id,
                            library.Id,
                            library.Name,
                            library.ProcessorName,
                            ProcessorConnectionId = library.ProcessorConnectionId,
                            library.ProcessorOutputPath,
                            library.ProcessorTimeoutMinutes
                        }, PayloadJsonOptions),
                        null,
                        "download",
                        waitKey,
                        cancellationToken);
                }

                continue;
            }

            // Name from the catalogue when Deluno knows which title it grabbed.
            // The client reports a release name ("Blade.Runner.2049.2017.1080p…"),
            // and parsing it produced both a mangled folder name and the wrong
            // year — 2049 is part of the title, not the release year (#268).
            var catalogue = await CatalogueImportNaming.ResolveAsync(
                dispatch,
                movieCatalogRepository,
                seriesCatalogRepository,
                cancellationToken);

            var request = new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: item.SourcePath,
                    FileName: InferImportFileName(item),
                    MediaType: item.MediaType,
                    // With no grab to name it by, the title and year the name gives,
                    // not the whole name: "The Kid 1921 CC 1080p…" is The Kid (#617).
                    Title: catalogue.Title ?? ReleaseTitleMatch.ParseTitleAndYear(item.ReleaseName).Title,
                    Year: catalogue.Year ?? ReleaseTitleMatch.ParseTitleAndYear(item.ReleaseName).Year ?? CatalogueImportNaming.InferYear(item.ReleaseName),
                    Genres: catalogue.Genres,
                    Tags: string.IsNullOrWhiteSpace(item.Category) ? [] : [item.Category],
                    Studio: catalogue.Studio,
                    OriginalLanguage: null,
                    ImdbId: catalogue.ImdbId,
                    TvDbId: catalogue.TvDbId,
                    Network: catalogue.Network,
                    SeriesId: catalogue.SeriesId,
                    SeriesType: catalogue.SeriesType,
                    NumberingScheme: catalogue.NumberingScheme,
                    // Into the library that grabbed it, not the first of its type (#526).
                    LibraryId: library.Id,
                    OriginalTitle: catalogue.OriginalTitle),
                TransferMode: "auto",
                Overwrite: dispatch?.ReplacementAuthorized == true,
                AllowCopyFallback: true,
                ForceReplacement: dispatch?.ForceReplacementAuthorized == true,
                DispatchId: dispatch?.DispatchId,
                ExpectedExistingPath: dispatch?.ReplacementExpectedPath,
                ReplacementTargets: dispatch?.ReplacementTargets);

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: JsonSerializer.Serialize(request, PayloadJsonOptions),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    // The catalogue item this import belongs to, when the grab
                    // is known — so the job is traceable to the title.
                    RelatedEntityId: catalogue.SeriesId ?? dispatch?.EntityId),
                cancellationToken);

            knownImportSources.Add(sourceKey);

            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.auto-queued",
                $"{item.Title} finished in {item.ClientName}; Deluno queued it for import into {library.Name}.",
                JsonSerializer.Serialize(new
                {
                    item.ClientId,
                    item.ClientName,
                    item.ReleaseName,
                    item.SourcePath,
                    LibraryId = library.Id,
                    LibraryName = library.Name,
                    JobId = job.Id
                }, PayloadJsonOptions),
                job.Id,
                "library",
                library.Id,
                cancellationToken);
        }
            },
            "Import automation planning failed.",
            cancellationToken);
    }

    internal const string NotImportedCategory = "filesystem.import.not-imported";
    internal const string HeldByFileCheckCategory = "filesystem.import.held-by-file-check";

    /// <summary>
    /// Says once, against the source path, that the 20-second stability check
    /// — the safety net behind a client's own settled status (#912) — is what
    /// is holding this import back. The planner sees the same unready file on
    /// every pass; the activity feed is where it is remembered that this was
    /// already logged, so it is not repeated every poll.
    /// </summary>
    private static async Task TellHeldByFileCheckOnceAsync(
        IActivityFeedRepository activityFeedRepository,
        DownloadQueueItem item,
        CancellationToken cancellationToken)
    {
        var sourceKey = NormalizeSourceKey(item.SourcePath!);
        var told = await activityFeedRepository.ListActivityAsync(5, "download-source", sourceKey, cancellationToken);
        if (told.Any(activity => activity.Category == HeldByFileCheckCategory))
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            HeldByFileCheckCategory,
            $"{item.Title} finished in {item.ClientName}, but Deluno is waiting for the file to stop changing size before importing it.",
            JsonSerializer.Serialize(new { item.ClientId, item.ClientName, item.ReleaseName, item.SourcePath }, PayloadJsonOptions),
            null,
            "download-source",
            sourceKey,
            cancellationToken);
    }

    /// <summary>
    /// Says once, against the grab, that a finished download was not imported.
    /// The planner looks at it again every pass; the activity feed is where it is
    /// remembered that the person was already told.
    /// </summary>
    private static async Task TellNotImportedOnceAsync(
        IActivityFeedRepository activityFeedRepository,
        string dispatchId,
        string message,
        DownloadQueueItem item,
        CancellationToken cancellationToken)
    {
        var told = await activityFeedRepository.ListActivityAsync(20, "download-dispatch", dispatchId, cancellationToken);
        if (told.Any(activity => activity.Category == NotImportedCategory))
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            NotImportedCategory,
            message,
            JsonSerializer.Serialize(new { DispatchId = dispatchId, item.ClientId, item.ClientName, item.ReleaseName, item.SourcePath }, PayloadJsonOptions),
            null,
            "download-dispatch",
            dispatchId,
            cancellationToken);
    }

    /// <summary>Files that passed the integrity check: by download, path, and each file's size and last write.</summary>
    internal static class PassedFiles
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Passed = new(StringComparer.Ordinal);

        public static bool Contains(string key) => Passed.ContainsKey(key);

        public static void Remember(string key)
        {
            if (Passed.Count > 5000)
            {
                Passed.Clear();
            }

            Passed[key] = 0;
        }

        public static string KeyOf(DownloadQueueItem item, string path)
        {
            var text = new System.Text.StringBuilder().Append(item.ClientId).Append('|').Append(item.Id).Append('|').Append(path);
            try
            {
                IEnumerable<string> files = Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                    : [path];
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    text.Append('|').Append(file).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                text.Append("|unreadable:").Append(Guid.NewGuid());
            }

            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    internal const string DownloadIntegrityFailedCategory = "download.integrity.failed";
    internal const string DownloadIntegrityPassedCategory = "download.integrity.passed";

    /// <summary>
    /// Checkpoint A (#917): opens a finished download and checks it against
    /// what its own name and the catalogue's known runtime claim, before a
    /// processor or the library ever sees it. Off by default for every install
    /// (#936): the three integrity dependencies are only supplied once a
    /// person turns the setting on, and this returns false unchanged when they
    /// are not.
    ///
    /// <para>Returns true when the download failed and has already been
    /// removed from its client, deleted, blocklisted (hash/id + name, every
    /// indexer, #874) and searched again -- the caller must not go on to hand
    /// it to a processor or import it. Returns false for a pass, for a file
    /// still being written (tried again next pass), and whenever the check
    /// itself is not switched on.</para>
    /// </summary>
    private static async Task<bool> RunDownloadIntegrityCheckpointAsync(
        DownloadQueueItem item,
        DispatchCatalogueLink? dispatch,
        LibraryItem library,
        IPlatformSettingsRepository? platformSettingsRepository,
        IMediaProbeService? mediaProbeService,
        IBlockedReleaseRepository? blockedReleaseRepository,
        IQualityRepository? qualityRepository,
        IDownloadDispatchesRepository? dispatchesRepository,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IJobScheduler jobScheduler,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        // A folder-shaped download (a film in its own folder -- the common
        // case -- or a season pack) is resolved to its own file(s) inside
        // DownloadIntegrityCheck.RunAsync, not here, so both a plain file and
        // a folder are let through this guard (#917).
        if (platformSettingsRepository is null ||
            mediaProbeService is null ||
            item.SourcePath is not { Length: > 0 } sourcePath ||
            !(File.Exists(sourcePath) || Directory.Exists(sourcePath)))
        {
            return false;
        }

        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        if (!settings.DownloadIntegrityCheckEnabled)
        {
            return false;
        }

        // A file that passed is not opened or written about again while it is unchanged (#1155):
        // the same finished download is in front of every pass until it leaves the client.
        var passedKey = PassedFiles.KeyOf(item, sourcePath);
        if (PassedFiles.Contains(passedKey))
        {
            return false;
        }

        var languagePreference = qualityRepository is null
            ? null
            : await LanguagePreferenceForAsync(library, qualityRepository, cancellationToken);
        var knownRuntimeMinutes = await KnownRuntimeMinutesAsync(dispatch, movieCatalogRepository, seriesCatalogRepository, cancellationToken);
        var knownTitle = await KnownTitleAsync(dispatch, movieCatalogRepository, seriesCatalogRepository, cancellationToken);

        var result = await DownloadIntegrityCheck.RunAsync(
            sourcePath,
            item.ReleaseName,
            knownRuntimeMinutes,
            settings.DownloadIntegrityMinimumRuntimePercent,
            mediaProbeService,
            cancellationToken,
            languagePreference,
            knownTitle);

        if (result.Outcome == DownloadIntegrityOutcome.StillWriting)
        {
            return false;
        }

        var entityType = library.MediaType == "tv" ? "series" : "movie";
        var entityId = string.IsNullOrWhiteSpace(dispatch?.EntityId) ? null : dispatch.EntityId;
        // #1062: a release the person chose on Search has no title: it is never
        // blocklisted for them and nothing is searched for.
        var chosenOnSearch = dispatch is not null && !DispatchTargets.HasTitle(dispatch.EntityType);
        if (chosenOnSearch)
        {
            entityId = null;
        }

        if (result.Outcome == DownloadIntegrityOutcome.Pass)
        {
            PassedFiles.Remember(passedKey);

            // A pass is recorded on the title's own history so the result can
            // be seen later, same as a failure is.
            if (entityId is not null)
            {
                await activityFeedRepository.RecordActivityAsync(
                    DownloadIntegrityPassedCategory,
                    $"{item.Title} passed Deluno's check of the download before it went on.",
                    JsonSerializer.Serialize(new { item.ClientId, item.ClientName, item.ReleaseName }, PayloadJsonOptions),
                    null,
                    entityType,
                    entityId,
                    cancellationToken);
            }

            return false;
        }

        // Failed: a bad release. Removed from the client, deleted, blocklisted
        // (hash/id + name, every indexer, #874) and another searched for. The
        // processor never sees it.
        var reason = result.Reason ?? "Deluno's own check of this file found it was not what it claimed to be.";

        // Said once (#1148). The same finished download is looked at again on
        // every pass for as long as it is still in the client, and writing the
        // same failure each minute buried everything else in Activity.
        var subjectType = entityId is null ? "download" : entityType;
        var subjectId = entityId ?? item.Id;
        var earlier = (await activityFeedRepository.ListActivityAsync(30, subjectType, subjectId, cancellationToken))
            .Where(activity => activity.Category == DownloadIntegrityFailedCategory
                && activity.DetailsJson is { Length: > 0 } details
                && details.Contains(item.Id, StringComparison.Ordinal))
            .OrderByDescending(activity => activity.CreatedUtc)
            .FirstOrDefault();

        if (earlier is not null)
        {
            if (!EarlierReportSaidRemoved(earlier))
            {
                // The removal did not work last time. Try again, quietly, and
                // say so only when it finally does.
                var (removedNow, _) = await TryRemoveFromClientAsync(item, downloadClientTelemetryService, cancellationToken);
                if (removedNow)
                {
                    await activityFeedRepository.RecordActivityAsync(
                        DownloadIntegrityFailedCategory,
                        $"{item.Title} is now removed from {item.ClientName}.",
                        JsonSerializer.Serialize(new { item.ClientId, item.ClientName, item.ReleaseName, item.SourcePath, itemId = item.Id, reason, removedFromClient = true }, PayloadJsonOptions),
                        null,
                        subjectType,
                        subjectId,
                        cancellationToken);
                }
            }

            return true;
        }

        var blocklisted = false;
        if (blockedReleaseRepository is not null && !chosenOnSearch)
        {
            try
            {
                await blockedReleaseRepository.BlockAsync(
                    new BlockedRelease(
                        Guid.NewGuid().ToString("n"),
                        BlockedReleaseKeys.For(item.ReleaseName, dispatch?.IndexerName is { Length: > 0 } indexer ? indexer : item.IndexerName),
                        item.ReleaseName,
                        dispatch?.IndexerName is { Length: > 0 } releaseIndexer ? releaseIndexer : item.IndexerName,
                        library.MediaType,
                        entityId,
                        item.Title,
                        ImportFailurePolicy.DownloadIntegrityFailed,
                        reason,
                        item.Id,
                        item.ClientId,
                        item.ClientName,
                        DateTimeOffset.UtcNow),
                    cancellationToken);
                blocklisted = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Left unblocked, and said so below.
            }
        }

        var (removedFromClient, removalProblem) = await TryRemoveFromClientAsync(item, downloadClientTelemetryService, cancellationToken);

        var searchQueued = false;
        if (entityId is not null)
        {
            try
            {
                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        LibrarySearchJobTypes.For(library.MediaType),
                        "download-integrity-failed",
                        JsonSerializer.Serialize(new
                        {
                            libraryId = library.Id,
                            libraryName = library.Name,
                            mediaType = library.MediaType,
                            checkMissing = true,
                            checkUpgrades = true,
                            maxItems = 1,
                            triggeredBy = "download-integrity-failed",
                            targetEntityId = entityId
                        }, PayloadJsonOptions),
                        entityType,
                        entityId,
                        IdempotencyKey: $"download-integrity-failed:{item.Id}",
                        DedupeKey: $"download-integrity-failed:{item.Id}"),
                    cancellationToken);
                searchQueued = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The title's own scheduled search picks it back up
                // regardless, and the message below does not claim otherwise.
            }
        }

        // What the message says is what happened, step by step: a step that
        // did not work is named, with the reason, never claimed (#1148).
        var done = new List<string>();
        var notDone = new List<string>();
        if (removedFromClient)
        {
            done.Add(string.IsNullOrWhiteSpace(removalProblem)
                ? "removed it"
                : $"removed it, but its files are still on disk ({removalProblem.TrimEnd('.')})");
        }
        else
        {
            notDone.Add($"could not remove it from {item.ClientName}{(string.IsNullOrWhiteSpace(removalProblem) ? string.Empty : $" ({removalProblem.TrimEnd('.')})")}, so remove it there");
        }

        if (!chosenOnSearch)
        {
            if (blocklisted)
            {
                done.Add("blocklisted the release");
            }
            else if (entityId is not null)
            {
                notDone.Add("could not blocklist the release");
            }
        }

        if (entityId is not null)
        {
            if (searchQueued)
            {
                done.Add("is searching for another copy");
            }
            else
            {
                notDone.Add("could not start a search for another copy");
            }
        }

        var told = new System.Text.StringBuilder($"{item.Title} finished in {item.ClientName}, but {reason}");
        if (done.Count > 0)
        {
            told.Append(" Deluno ").Append(JoinWithAnd(done)).Append('.');
        }

        if (notDone.Count > 0)
        {
            told.Append(" Deluno ").Append(JoinWithAnd(notDone)).Append('.');
        }

        if (chosenOnSearch)
        {
            told.Append(' ').Append(DispatchTargets.ChosenOnSearchNote);
        }

        await activityFeedRepository.RecordActivityAsync(
            DownloadIntegrityFailedCategory,
            told.ToString(),
            JsonSerializer.Serialize(new
            {
                item.ClientId,
                item.ClientName,
                item.ReleaseName,
                item.SourcePath,
                itemId = item.Id,
                reason,
                technicalDetail = result.Detail,
                removedFromClient
            }, PayloadJsonOptions),
            null,
            subjectType,
            subjectId,
            cancellationToken);

        // Transfers shows the same failure as Activity, as something to deal
        // with, rather than "Waiting for processor" (#1148).
        if (dispatchesRepository is not null && dispatch is not null && !string.IsNullOrWhiteSpace(dispatch.DispatchId))
        {
            try
            {
                await dispatchesRepository.RecordImportOutcomeAsync(
                    dispatch.DispatchId,
                    "failed",
                    null,
                    DownloadClientFailureCodes.IntegrityCheckFailed,
                    reason,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Activity already has it; the next pass does not repeat itself.
            }
        }

        return true;
    }

    /// <summary>The plain reason a contained send carries, on Activity and in the drawer.</summary>
    public const string ContainedSendReason = "Not needed: another copy was handed over";

    /// <summary>
    /// A send Deluno contains (its client entry reads "another copy was handed over", #1215) is closed for good the
    /// moment that is decided, with its reason on the record. Read from the live entry alone it fell back to "In
    /// progress" once the copy left the client. Sends already in that state are closed the next time this runs.
    /// </summary>
    public static async Task<int> CloseContainedSendsAsync(
        DownloadTelemetryOverview telemetry,
        IDownloadDispatchesRepository? dispatches,
        CancellationToken cancellationToken)
    {
        if (dispatches is null)
        {
            return 0;
        }

        var closed = 0;
        foreach (var item in telemetry.Clients.SelectMany(client => client.Queue)
                     .Where(item => item.Status == DownloadQueueStatuses.HandedOverElsewhere))
        {
            try
            {
                closed += await dispatches.CloseContainedDispatchesAsync(item.ClientId, item.Id, ContainedSendReason, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Closed again on the next pass.
            }
        }

        return closed;
    }

    private static async Task<(bool Removed, string? Problem)> TryRemoveFromClientAsync(
        DownloadQueueItem item,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        CancellationToken cancellationToken)
    {
        try
        {
            var actionResult = await downloadClientTelemetryService.ExecuteActionAsync(
                item.ClientId,
                new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, item.Id),
                cancellationToken);
            // A removal that left the files on disk says so in the second part, so "removed it" never claims more (#1212).
            return (actionResult.Succeeded, actionResult.Succeeded ? actionResult.FilesNote : actionResult.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, exception.Message);
        }
    }

    private static bool EarlierReportSaidRemoved(ActivityEventItem earlier)
    {
        try
        {
            using var document = JsonDocument.Parse(earlier.DetailsJson!);
            return document.RootElement.TryGetProperty("removedFromClient", out var removed)
                && removed.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string JoinWithAnd(IReadOnlyList<string> parts)
        => parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
        };

    /// <summary>The library's own language preference, from its assigned quality profile, or null when it has none.</summary>
    private static async Task<ProfileLanguagePreference?> LanguagePreferenceForAsync(
        LibraryItem library,
        IQualityRepository qualityRepository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(library.QualityProfileId))
        {
            return null;
        }

        var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Id, library.QualityProfileId, StringComparison.OrdinalIgnoreCase))
            ?.LanguagePreference;
    }

    /// <summary>
    /// The runtime Deluno already knows for this grab's title -- a film's own
    /// runtime, or a show's typical episode runtime, whichever the dispatch
    /// names. Null when there is no dispatch, no catalogue match, or no
    /// recorded runtime, in which case Checkpoint A's runtime rules simply
    /// have nothing to compare against.
    /// </summary>
    private static async Task<int?> KnownRuntimeMinutesAsync(
        DispatchCatalogueLink? dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        if (CatalogueImportNaming.IsShowGrab(dispatch))
        {
            var seriesId = await CatalogueImportNaming.SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            var series = seriesId is null ? null : await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken);
            return series?.RuntimeMinutes;
        }

        var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
        return movie?.RuntimeMinutes;
    }

    /// <summary>
    /// The title Deluno already knows for this grab, read the same way as
    /// <see cref="KnownRuntimeMinutesAsync"/> -- what Checkpoint A's "not the
    /// film/episode the title says" rule compares a file's own embedded title
    /// tag against (#917). Null under the same conditions.
    /// </summary>
    private static async Task<string?> KnownTitleAsync(
        DispatchCatalogueLink? dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        if (CatalogueImportNaming.IsShowGrab(dispatch))
        {
            var seriesId = await CatalogueImportNaming.SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            var series = seriesId is null ? null : await seriesCatalogRepository.GetByIdAsync(seriesId, cancellationToken);
            return series?.Title;
        }

        var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
        return movie?.Title;
    }

    private static async Task RecordUnmatchedProcessorOutputsAsync(
        IProcessorRepository processorRepository,
        IJobQueueRepository jobQueueRepository,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        IReadOnlyList<ActivityEventItem> recentActivity,
        CancellationToken cancellationToken)
    {
        var reportedOutputKeys = recentActivity
            .Where(item => item.Category == "processing.output.unmatched")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refineLibraries = libraries
            .Where(library =>
                ImportWorkflows.HandsToProcessor(library.ImportWorkflow) &&
                !string.IsNullOrWhiteSpace(library.ProcessorOutputPath) &&
                Directory.Exists(library.ProcessorOutputPath))
            .ToArray();

        foreach (var library in refineLibraries)
        {
            IReadOnlyList<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(library.ProcessorOutputPath!, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsImportableVideoFile)
                    .Where(ProcessorOutputReadiness.IsReady)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(10)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.scan-failed",
                    $"Deluno could not scan the processor output folder for {library.Name}. Check the path and service permissions.",
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.ProcessorOutputPath,
                        Error = ex.Message
                    }, PayloadJsonOptions),
                    null,
                    "library",
                    library.Id,
                    cancellationToken);
                continue;
            }

            IReadOnlyList<ProcessorHandoffItem>? handoffs = null;
            foreach (var file in files)
            {
                var sourceKey = NormalizeSourceKey(file);
                if (knownImportSources.Contains(sourceKey))
                {
                    continue;
                }

                // A file a send that was removed left, for a release a person sent again (#1242): the resend's hand-off
                // is on its way and imports this very file, so it is not a stranger. Looked at again on the next pass;
                // if the resend never imports it, the file is listed then.
                handoffs ??= await processorRepository.ListProcessorHandoffsAsync(library.Id, 500, cancellationToken);
                if (await IsOutputOfAReleaseSentAgainAsync(handoffs, jobQueueRepository, file, library.DownloadsPath, cancellationToken))
                {
                    continue;
                }

                var outputKey = $"{library.Id}:{Path.GetFileName(file).ToLowerInvariant()}";
                if (!reportedOutputKeys.Add(outputKey))
                {
                    continue;
                }

                var title = Path.GetFileNameWithoutExtension(file);
                var summary = $"Deluno found a cleaned output in {library.Name}, but cannot safely match it to a download hand-off.";
                var recommendation = "Use the processor callback with Deluno's hand-off ID, or review this file and import it manually. Deluno did not import it automatically.";
                if (library.MediaType == "tv")
                {
                    await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateSeriesImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                else
                {
                    await movieCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateMovieImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.unmatched",
                    summary,
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.MediaType,
                        FileName = Path.GetFileName(file),
                        Recommendation = recommendation
                    }, PayloadJsonOptions),
                    null,
                    "processor-output",
                    outputKey,
                    cancellationToken);
            }
        }
    }

    private static async Task<bool> IsOutputOfAReleaseSentAgainAsync(
        IReadOnlyList<ProcessorHandoffItem> handoffs,
        IJobQueueRepository jobQueueRepository,
        string file,
        string? watchedFolder,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file);
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var handoff in handoffs.Where(item => ExpectedOutputDirectoryNames(item.SourcePath, watchedFolder)
                     .Any(expected => string.Equals(expected, name, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(expected, stem, StringComparison.OrdinalIgnoreCase))
                     || ProcessorOutputLayout.ClaimsTopLevelName(item.SourcePath, watchedFolder, name)))
        {
            if (await ImportedDownloads.SentAgainSinceAsync(jobQueueRepository, handoff.ClientId, handoff.ReleaseName, handoff.CreatedUtc, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Completes the processor-agnostic path. A processor does not need to call
    /// Deluno or use a vendor adapter: it writes its result below the configured
    /// processed-output root while retaining the final source-folder name. That
    /// one stable path component lets Deluno match the output to a durable
    /// hand-off without guessing from a release title.
    /// </summary>
    /// <summary>
    /// Whether a processor that answers for itself is still working on this file.
    ///
    /// <para>A finished output appears in the folder before the processor has
    /// let go of it: Weir moves its file into place and then puts the original's
    /// timestamps back on it. Importing on the file's own evidence alone caught
    /// it mid-tidy, so every processed import failed its first attempt and read
    /// as failed until the retry (#562). A processor that says "queued",
    /// "scheduled" or "working" has not finished, and waiting costs one pass.</para>
    ///
    /// <para>Only what the processor says about this cycle counts. One that
    /// cannot be asked never says anything, and a hand-off Deluno stopped
    /// waiting for is a late delivery to take as it is (#511).</para>
    /// </summary>
    internal static bool ProcessorHasNotFinished(ProcessorHandoffItem handoff, DateTimeOffset now)
    {
        // Only a hand-off a processor was actually given. One that is merely
        // waiting has nobody to ask — the library watches a folder — and its
        // output is the only word there will ever be.
        if (!ProcessorHandoffStatuses.IsOpen(handoff.Status))
        {
            return false;
        }

        return handoff.Processor?.State switch
        {
            ProcessorHandoffStates.Queued or ProcessorHandoffStates.Scheduled or ProcessorHandoffStates.Working => true,

            // Handed over so recently that nobody has asked yet. The watcher
            // asks every pass, so waiting for its answer costs a minute; past
            // the grace the processor is one that does not answer, and the file
            // is all Deluno has to go on.
            null => now - handoff.UpdatedUtc < UnansweredGrace,

            // It finished, passed the file through, or reported an ending.
            _ => false
        };
    }

    /// <summary>How long a hand-off waits for a first answer before its output is taken on the file's own evidence.</summary>
    private static readonly TimeSpan UnansweredGrace = TimeSpan.FromMinutes(3);

    private async Task ReconcileMatchedProcessorOutputsAsync(
        IJobScheduler jobScheduler,
        IProcessorRepository processorRepository,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<LibraryItem> allLibraries,
        IReadOnlyList<JobQueueItem> existingJobs,
        CancellationToken cancellationToken)
    {
        // Every hand-off an output could still belong to, however much history
        // sits in front of them (#571).
        var handoffs = await processorRepository.ListProcessorHandoffsWithStatusAsync(
            ProcessorHandoffStatuses.AcceptingLateOutput,
            cancellationToken);

        // Outputs this pass has already queued. The reservation rule reads the
        // jobs that existed when the pass started, so without this two hand-offs
        // correlating to one file would both queue it.
        var queuedThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Including hand-offs Deluno stopped waiting for: a processor that
        // delivers late has still delivered, and the file is still wanted (#511).
        foreach (var handoff in handoffs.Where(item => ProcessorHandoffStatuses.AcceptsLateOutput(item.Status)))
        {
            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase) &&
                ImportWorkflows.HandsToProcessor(item.ImportWorkflow) &&
                !string.IsNullOrWhiteSpace(item.ProcessorOutputPath));
            if (library is null || ProcessorHasNotFinished(handoff, timeProvider.GetUtcNow()))
            {
                continue;
            }

            // The same reservation rule the loop above uses, rather than a flat
            // set of every path any import has ever touched.
            //
            // A processor writes its output to a folder named after the release,
            // so the refined path is identical every time that release is
            // fetched. Treating a *completed* import as a permanent reservation
            // meant a release could be imported exactly once, ever: on the lab
            // rig the hand-off for a fresh download sat at "waiting" for good,
            // because yesterday's successful import still held the path.
            //
            // HasImportReservation already says why this is wrong, at its own
            // definition, and scopes the reservation to the dispatch. The rule
            // existed; this call site did not use it.
            //
            // Which dispatch this hand-off belongs to decides whether the output
            // is already spoken for; the import queue reads it again to name it.
            var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(
                handoff.ClientId,
                handoff.QueueItemId,
                handoff.ReleaseName,
                cancellationToken);

            var candidates = FindCorrelatedProcessorOutputs(library.ProcessorOutputPath!, handoff.SourcePath, library.DownloadsPath)
                .Where(path => !queuedThisPass.Contains(NormalizeSourceKey(path)))
                .Where(path => !HasImportReservation(existingJobs, NormalizeSourceKey(path), dispatch))
                .Where(ProcessorOutputReadiness.IsReady)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var outputPath = candidates[0];
            var importJob = await new ProcessorOutputImportQueue(jobQueueRepository, jobScheduler, movieCatalogRepository, seriesCatalogRepository)
                .QueueAsync(library, handoff, outputPath, "processor-output-watch", cancellationToken);
            if (importJob.AlreadyImported)
            {
                // A second copy of a download Deluno already imported (#1155).
                var deleted = Deluno.Platform.Processing.ProcessorOutputPathPolicy.DeleteSecondCopy(outputPath, library.ProcessorOutputPath, handoff.SourcePath, allLibraries);
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, "completed", outputPath, null, null, cancellationToken);
                queuedThisPass.Add(NormalizeSourceKey(outputPath));
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.already-imported",
                    deleted
                        ? $"The processor left a second copy of {handoff.ReleaseName}, which Deluno had already imported. Deluno deleted the second copy."
                        : $"The processor left a second copy of {handoff.ReleaseName}, which Deluno had already imported. There was nothing to import.",
                    JsonSerializer.Serialize(new { importJob.DispatchId, handoff.ReleaseName, HandoffId = handoff.Id, OutputPath = outputPath, Deleted = deleted }, PayloadJsonOptions),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                continue;
            }

            if (importJob.NotImportedBecause is { } removed)
            {
                var message = $"{handoff.ReleaseName} was processed, but {removed}, so Deluno did not import it. The processed file is still at {outputPath}.";
                await processorRepository.UpdateProcessorHandoffAsync(handoff.Id, "completed", outputPath, null, message, cancellationToken);
                queuedThisPass.Add(NormalizeSourceKey(outputPath));
                await activityFeedRepository.RecordActivityAsync(
                    NotImportedCategory,
                    message,
                    JsonSerializer.Serialize(new { importJob.DispatchId, handoff.ReleaseName, HandoffId = handoff.Id, OutputPath = outputPath }, PayloadJsonOptions),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                continue;
            }

            await processorRepository.UpdateProcessorHandoffAsync(
                handoff.Id,
                "completed",
                outputPath,
                importJob.JobId,
                null,
                cancellationToken);
            queuedThisPass.Add(NormalizeSourceKey(outputPath));
            await activityFeedRepository.RecordActivityAsync(
                "processing.output.matched.import-queued",
                $"Deluno matched processed output for {handoff.ReleaseName} and queued it for import.",
                JsonSerializer.Serialize(new
                {
                    importJob.DispatchId,
                    handoff.ReleaseName,
                    HandoffId = handoff.Id,
                    library.Id,
                    library.Name,
                    SourcePath = handoff.SourcePath,
                    OutputPath = outputPath,
                    JobId = importJob.JobId,
                    Match = "stable processed-output subfolder"
                }, PayloadJsonOptions),
                importJob.JobId,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    internal static IReadOnlyList<string> FindCorrelatedProcessorOutputs(string outputRoot, string sourcePath, string? watchedFolder = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        try
        {
            var root = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(root))
            {
                return [];
            }

            var matches = new List<string>();

            // Where the processor puts it: the source's path below the watched folder, under the output folder (#1262).
            foreach (var path in ProcessorOutputLayout.ExistingOutputPaths(root, sourcePath, watchedFolder))
            {
                matches.AddRange(VideosAt(path));
            }

            if (matches.Count > 0)
            {
                return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            }

            // The older generic layout, for a processor that publishes no folder list or a library with no downloads
            // folder: a folder named after the source, with or without its extension.
            foreach (var directoryName in ExpectedOutputDirectoryNames(sourcePath))
            {
                var expectedDirectory = Path.GetFullPath(Path.Combine(root, directoryName));
                if (!expectedDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expectedDirectory))
                {
                    continue;
                }

                matches.AddRange(VideosAt(expectedDirectory));
            }

            return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The ready video files at a path inside the output folder: the file itself, or those below a folder, newest first.</summary>
    private static IEnumerable<string> VideosAt(string path)
        => File.Exists(path)
            ? IsImportableVideoFile(path) && ProcessorOutputReadiness.IsReady(path) ? [path] : []
            : Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(IsImportableVideoFile)
                .Where(ProcessorOutputReadiness.IsReady)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(2);

    internal static bool IsImportableVideoFile(string path)
        => SupportedVideoExtensions.Contains(path);

    /// <summary>
    /// The one or two top-level directory names a processor's output for this
    /// release could stand under: the final source-folder name, with and
    /// without its extension (a single-file source names a directory after
    /// itself minus the extension) -- what the processor output safety-net
    /// health check also reads, so an active hand-off's own working folder is
    /// never reported as unexpected (#921).
    /// </summary>
    internal static IReadOnlyList<string> ExpectedOutputDirectoryNames(string sourcePath, string? watchedFolder = null)
    {
        // Path.GetFileName only recognises this OS's own separator, but a
        // source path can be reported by a download client running on a
        // different OS than Deluno itself (the same reason NormalizeSourceKey
        // below treats '\' and '/' the same regardless of platform). On Linux,
        // Path.GetFileName would return a Windows-style path unchanged instead
        // of its leaf name, so nothing here ever matched (reproduced on Linux
        // CI for #953: both temporary-file-name tests failed on Linux while
        // passing on Windows for exactly this reason).
        var trimmed = sourcePath.Replace('\\', '/').TrimEnd('/');
        var sourceLeaf = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(sourceLeaf) || sourceLeaf is "." or "..")
        {
            return [];
        }

        // The first folder or the file below the watched folder is what the processor's layout puts in the output folder
        // (#1262): the release folder of a file inside one.
        return new[]
        {
            sourceLeaf,
            Path.GetFileNameWithoutExtension(sourceLeaf)
        }
            .Concat(ProcessorOutputLayout.TopLevelNames(sourcePath, watchedFolder))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LibraryItem? ResolveLibraryForQueueItem(
        DownloadQueueItem item,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> routeCategoriesByLibrary)
    {
        if (!string.IsNullOrWhiteSpace(item.LibraryId))
        {
            var assignedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (assignedLibrary is not null)
            {
                return assignedLibrary;
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

    internal static IReadOnlyList<DownloadQueueItem> GetImportCandidates(DownloadTelemetryOverview telemetry)
    {
        var candidates = new List<DownloadQueueItem>();
        foreach (var client in telemetry.Clients)
        {
            candidates.AddRange(client.Queue);

            // Fast clients such as SABnzbd can download and post-process a small
            // item completely between worker polls. In that case there is never
            // a completed queue row for this planner to observe, but native
            // history still contains the authoritative source path. Dispatch-
            // derived history is deliberately excluded: it proves only that
            // Deluno sent the release, not that the client completed it.
            candidates.AddRange(client.History
                .Where(item =>
                    string.Equals(item.HistorySource, "native", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Outcome, "completed", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.SourcePath))
                .Select(item => new DownloadQueueItem(
                    item.ExternalId ?? item.Id,
                    item.ClientId,
                    item.ClientName,
                    item.Protocol,
                    item.MediaType,
                    item.Title,
                    item.ReleaseName,
                    item.Category,
                    DownloadQueueStatuses.Completed,
                    Progress: 1,
                    SpeedMbps: 0,
                    EtaSeconds: 0,
                    item.SizeBytes,
                    DownloadedBytes: item.SizeBytes,
                    Peers: 0,
                    item.IndexerName,
                    item.ErrorMessage,
                    AddedUtc: item.CompletedUtc,
                    item.SourcePath)));
        }

        // A client can briefly expose the same completion in both queue and
        // native history. Prefer the live queue row and process the physical
        // source once; job/dispatch reservations provide durable dedupe on
        // later planning passes and across restarts.
        return candidates
            .GroupBy(
                item => $"{item.ClientId}:{NormalizeSourceKey(item.SourcePath ?? item.Id)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadRouteCategoriesAsync(
        IReadOnlyList<LibraryItem> libraries,
        ILibrariesRepository librariesRepository,
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

    /// <param name="lastWrittenUtc">
    /// When the source was last written; null when it cannot be read. Defaults
    /// to the file system.
    /// </param>
    internal static bool HasImportReservation(
        IEnumerable<JobQueueItem> jobs,
        string normalizedSourcePath,
        DispatchCatalogueLink? dispatch,
        Func<string, DateTimeOffset?>? lastWrittenUtc = null)
    {
        foreach (var job in jobs)
        {
            if (!string.Equals(job.JobType, "filesystem.import.execute", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase) && SourceChangedSince(job, lastWrittenUtc ?? LastWrittenUtc))
                || !string.Equals(
                    NormalizeSourceKey(TryReadImportSourcePath(job.PayloadJson) ?? string.Empty),
                    normalizedSourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A source path alone is not an import identity. Download clients
            // commonly reuse the same completed folder when a release is
            // grabbed again. A completed job for an older dispatch must not
            // permanently reserve that folder and suppress the new import.
            // Unscoped/manual imports retain the legacy source reservation.
            if (dispatch is null)
            {
                return true;
            }

            if (string.Equals(TryReadImportDispatchId(job.PayloadJson), dispatch.DispatchId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An import that gave up still holds its file, until the file changes.
    ///
    /// <para>A dead-lettered import used to reserve nothing, so the next pass
    /// queued the same broken file again, it failed the same way, and the Queue
    /// showed the retry revived for ever (the corrupt refined copy of The Terror
    /// on the rig). Retrying it unchanged is a person's choice, from Queue; a
    /// file that has been written since - repaired, refined again, replaced - is
    /// a new attempt worth making by itself.</para>
    /// </summary>
    private static bool SourceChangedSince(JobQueueItem job, Func<string, DateTimeOffset?> lastWrittenUtc)
    {
        var source = TryReadImportSourcePath(job.PayloadJson);
        var failedAt = job.LastAttemptUtc ?? job.CompletedUtc ?? job.CreatedUtc;
        return source is not null && lastWrittenUtc(source) is { } written && written > failedAt;
    }

    private static DateTimeOffset? LastWrittenUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path)
                : Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>
    /// Whether this download has already been handed over, dealt with or covered by another copy (#1155).
    /// Found by the client and the client's id for the download, so a moved path cannot hide it.
    /// </summary>
    internal static async Task<bool> HasBeenDealtWithAsync(
        DownloadQueueItem item,
        IProcessorRepository processorRepository,
        IProcessorOutputImportQueue? outputImports,
        CancellationToken cancellationToken,
        IJobQueueRepository? dispatches = null)
    {
        // A download the client took after the hand-off ended is a new acquisition.
        // A hand-off is made after the client has the download, so a download added after the hand-off was made is a new
        // one, whatever happened to the hand-off since. A hand-off closed because its download was removed is stamped
        // when the removal lands, which can be later than the add time of the same torrent added again (the client
        // stamps whole seconds), so a dismissed hand-off is judged on when it was made, not when it was closed.
        static bool IsNewer(DownloadQueueItem download, ProcessorHandoffItem handoff)
            => download.AddedUtc > DateTimeOffset.UnixEpoch
               && (download.AddedUtc > handoff.UpdatedUtc
                   || (handoff.Status == ProcessorHandoffStatuses.Dismissed && download.AddedUtc > handoff.CreatedUtc));

        var prior = await processorRepository.FindProcessorHandoffForDownloadAsync(item.ClientId, item.Id, cancellationToken);
        if (prior is not null && !IsNewer(item, prior))
        {
            if (prior.Status is ProcessorHandoffStatuses.Dismissed or ProcessorHandoffStatuses.Completed)
            {
                // A finished hand-off for a release that was sent again on purpose since (the title lost its
                // file and search picked this very torrent, still finished in the client) is over: the new
                // send is a new acquisition and gets a hand-off of its own (#1182).
                return !(dispatches is not null
                    && await ImportedDownloads.FinishedWithItsImportAsync(dispatches, prior, cancellationToken)
                    && await ImportedDownloads.SentAgainSinceAsync(dispatches, item.ClientId, item.ReleaseName, prior.UpdatedUtc, cancellationToken));
            }

            // Still with the processor, or waiting to be sent, at another path: it is that hand-off's
            // download, and the path was repointed above when it could be.
            if (ProcessorHandoffStatuses.IsWithProcessor(prior.Status)
                && !string.Equals(NormalizeSourceKey(prior.SourcePath), NormalizeSourceKey(item.SourcePath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (outputImports is not null && await outputImports.TitleHasFileFromAnotherDownloadAsync(prior, cancellationToken))
            {
                return true;
            }
        }

        // Another copy of the same release, in another client, that has the hand-off already.
        if (!string.IsNullOrWhiteSpace(item.ReleaseName))
        {
            foreach (var other in await processorRepository.ListProcessorHandoffsForReleaseAsync(item.ReleaseName, cancellationToken))
            {
                if (string.Equals(other.ClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(other.QueueItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsNewer(item, other))
                {
                    continue;
                }

                if (ProcessorHandoffStatuses.IsWithProcessor(other.Status)
                    || other.Status is ProcessorHandoffStatuses.Completed or ProcessorHandoffStatuses.Dismissed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    /// <summary>
    /// Claims and records one recurring pass. Keeping this wrapper beside the
    /// planner prevents a pass from being reported as healthy merely because
    /// its lease was claimed; the System screen gets a terminal result and
    /// duration instead.
    /// </summary>
    /// <param name="chosenInterval">
    /// A cadence the user has chosen, where the pass is configurable. Left null
    /// for the fixed engineering cadences, which are the majority: how often it
    /// is worth asking a download client what it is doing is not a preference.
    /// It still comes from <see cref="SystemTasks"/> — a caller may choose
    /// between declared cadences, never invent one.
    /// </param>
    private async Task RunScheduledPassAsync(
        string scheduleKey,
        Func<Task> operation,
        string failureMessage,
        CancellationToken cancellationToken,
        TimeSpan? chosenInterval = null)
    {
        var interval = chosenInterval ?? SystemTasks.IntervalFor(scheduleKey);

        if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                scheduleKey,
                interval,
                cancellationToken))
        {
            return;
        }

        var startedUtc = timeProvider.GetUtcNow();
        try
        {
            await operation();
            await RecordScheduledPassOutcomeAsync(scheduleKey, startedUtc, "completed", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordScheduledPassOutcomeAsync(scheduleKey, startedUtc, "cancelled", CancellationToken.None);
        }
        catch (Exception exception)
        {
            await RecordScheduledPassOutcomeAsync(scheduleKey, startedUtc, "failed", CancellationToken.None, PlainFailure.Describe(exception, SystemTasks.All.FirstOrDefault(task => task.Key == scheduleKey)?.Name.ToLowerInvariant()));
            logger.LogWarning(exception, failureMessage);
        }
    }

    private async Task RecordScheduledPassOutcomeAsync(
        string scheduleKey,
        DateTimeOffset startedUtc,
        string result,
        CancellationToken cancellationToken,
        string? failureReason = null)
    {
        var completedUtc = timeProvider.GetUtcNow();
        var durationMs = Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds);
        try
        {
            await jobQueueRepository.RecordScheduledPassOutcomeAsync(
                scheduleKey,
                completedUtc,
                result,
                durationMs,
                startedUtc.Add(SystemTasks.IntervalFor(scheduleKey)),
                cancellationToken,
                failureReason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record scheduled pass outcome for {ScheduleKey}.", scheduleKey);
        }
    }

    internal static string InferImportFileName(DownloadQueueItem item)
    {
        // Both separators named outright, because Path.GetInvalidFileNameChars()
        // answers for the host: on Linux it returns NUL and '/' only, so a
        // release name carrying a backslash kept it and Deluno inferred a file
        // name Windows would have cleaned. Release names come from indexers,
        // which is not a place to take the host's word for what is safe. The
        // sibling sanitizer in ImportPipelineService already spells them out.
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(item.ReleaseName
            .Select(character => invalid.Contains(character) || character is '/' or '\\' ? '.' : character)
            .ToArray())
            .Replace(' ', '.')
            .Trim('.');

        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        if (cleaned.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned;
        }

        var sourceExtension = Path.GetExtension(item.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceExtension) &&
            IsImportableVideoFile($"placeholder{sourceExtension}"))
        {
            return $"{(string.IsNullOrWhiteSpace(cleaned) ? item.Id : cleaned)}{sourceExtension.ToLowerInvariant()}";
        }

        return $"{(string.IsNullOrWhiteSpace(cleaned) ? item.Id : cleaned)}.mkv";
    }


}
