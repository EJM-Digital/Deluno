using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Connections.Data;
using Deluno.Recovery.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Recovery.Services;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
    IConfiguration configuration)
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

    public async Task RunDispatchCleanupAsync(
        IDispatchCleanupService cleanupService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("dispatch.cleanup", TimeSpan.FromHours(6), cancellationToken))
        {
            return;
        }

        try
        {
            await cleanupService.RunCleanupPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch cleanup pass failed.");
        }
    }

    public async Task RunDispatchRetryPassAsync(
        IDownloadRetryService downloadRetryService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("dispatch.retry", TimeSpan.FromMinutes(2), cancellationToken))
        {
            return;
        }

        try
        {
            await downloadRetryService.RunRetryPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch retry pass failed.");
        }
    }

    public async Task PlanMetadataRefreshAutomationAsync(
        IJobScheduler jobScheduler,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("metadata.refresh", MetadataTopUpInterval, cancellationToken))
        {
            return;
        }

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
    }

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

    public async Task PlanIntakeAutomationAsync(
        IIntakeSyncService intakeSyncService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("intake.automation", TimeSpan.FromMinutes(5), cancellationToken))
        {
            return;
        }

        await intakeSyncService.PlanDueSyncJobsAsync(cancellationToken);
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
    public async Task PlanLibraryImportResumeAsync(
        IJobScheduler jobScheduler,
        IExistingLibraryImportService importService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                "library.import.resume",
                TimeSpan.FromMinutes(1),
                cancellationToken))
        {
            return;
        }

        try
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Library import resume pass failed.");
        }
    }

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
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("sharing.reclaim", TimeSpan.FromSeconds(30), cancellationToken))
        {
            return;
        }

        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        var globalPolicy = new SharingPolicy(
            settings.SharingMode,
            settings.SharingForHours,
            settings.SharingUntilRatio,
            settings.SharingStuckAction,
            settings.SharingStuckAfterDays);

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
            overview = await downloadClientTelemetryService.GetOverviewAsync(cancellationToken);
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

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var librariesById = libraries.ToDictionary(library => library.Id, StringComparer.OrdinalIgnoreCase);

        // What the clients are still holding, for the dashboard to explain.
        var holds = new List<DownloadSharingHold>();
        string? driveNote = null;

        foreach (var item in overview.Clients.SelectMany(client => client.Queue))
        {
            if (!string.Equals(item.Status, DownloadQueueStatuses.Imported, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only items Deluno itself dispatched. Something a user added by
            // hand is theirs, and reclaiming it would be Deluno deleting a file
            // it was never asked to manage.
            var dispatch = await jobQueueRepository.FindRecentDispatchLinkAsync(item.ClientId, item.ReleaseName, cancellationToken);
            if (dispatch is null)
            {
                continue;
            }

            indexersByName.TryGetValue(dispatch.IndexerName ?? string.Empty, out var source);

            var outcome = await reclaimService.ReconcileAsync(
                new SharingReclaimCandidate(
                    item.Id,
                    item.ClientId,
                    item.ClientName,
                    item.Title,
                    item.Protocol,
                    item.Ratio,
                    item.SeedingMinutes),
                globalPolicy,
                source,
                cancellationToken);

            if (outcome.Reclaimed)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "download.sharing.reclaimed",
                    $"{item.Title} finished sharing and was removed from {item.ClientName}. {outcome.Reason}",
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
                outcome.Detail ?? outcome.Reason,
                Math.Max(0, item.SizeBytes),
                NeedsYou: outcome.Action == SharingAction.Ask || outcome.Warning is not null,
                SharesLibraryCopy: SharingFootprint.SharesOneCopy(downloadPath, libraryPath, settings.UseHardlinks)));

            driveNote ??= SharingFootprint.Describe(downloadPath, libraryPath, settings.UseHardlinks);
        }

        await sharingRepository.ReplaceHoldsAsync(holds, driveNote, cancellationToken);
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

            if (string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase))
            {
                var series = await seriesCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
                return string.IsNullOrWhiteSpace(series?.Title) ? fallback : series.Title;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve a catalogue title for dispatch {DispatchId}.", dispatch.DispatchId);
        }

        return fallback;
    }

    public async Task PlanImportAutomationAsync(
        IJobScheduler jobScheduler,
        IProcessorRepository processorRepository,
        ILibrariesRepository librariesRepository,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IProcessorConnectionService processorConnectionService,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("import.automation", TimeSpan.FromSeconds(15), cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
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
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recentWaiting = await activityFeedRepository.ListActivityAsync(150, null, null, cancellationToken);
        await ReconcileMatchedProcessorOutputsAsync(
            jobScheduler,
            processorRepository,
            activityFeedRepository,
            libraries,
            knownImportSources,
            cancellationToken);
        await RecordUnmatchedProcessorOutputsAsync(
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            knownImportSources,
            recentWaiting,
            cancellationToken);
        await RecordProcessorTimeoutsAsync(
            processorRepository,
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            recentWaiting,
            now,
            cancellationToken);

        var recentWaitingKeys = recentWaiting
            .Where(item => item.Category == "processing.waiting" && item.CreatedUtc > now.AddHours(-6))
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var telemetry = await downloadClientTelemetryService.GetOverviewAsync(cancellationToken);
        await downloadClientTelemetryService.RunConfiguredHealthRemediationAsync(telemetry, cancellationToken);
        foreach (var item in telemetry.Clients.SelectMany(client => client.Queue))
        {
            if (item.Status is not ("importReady" or "completed"))
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
            // path is stable and readable.
            if (File.Exists(item.SourcePath) && !ProcessorOutputReadiness.IsReady(item.SourcePath))
            {
                continue;
            }

            var sourceKey = NormalizeSourceKey(item.SourcePath);
            if (knownImportSources.Contains(sourceKey))
            {
                continue;
            }

            var library = ResolveLibraryForQueueItem(item, libraries, routeCategoriesByLibrary);
            if (library is null)
            {
                continue;
            }

            if (string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                var handoff = await processorRepository.EnsureProcessorHandoffAsync(
                    new CreateProcessorHandoffRequest(
                        library.Id,
                        library.MediaType,
                        item.ClientId,
                        item.Id,
                        item.ReleaseName,
                        item.SourcePath,
                        library.ProcessorName),
                    cancellationToken);
                var currentHandoff = handoff;
                ProcessorSubmissionResult? submission = null;
                var connection = await processorRepository.FindProcessorConnectionByNameAsync(library.ProcessorName, cancellationToken);
                if (connection is { IsEnabled: true } && handoff.Status == "waiting")
                {
                    submission = await processorConnectionService.SubmitAsync(connection, handoff, cancellationToken);
                    currentHandoff = await processorRepository.UpdateProcessorHandoffAsync(
                        handoff.Id,
                        submission.Status,
                        null,
                        null,
                        submission.IsAccepted ? null : submission.Message,
                        cancellationToken) ?? handoff;
                    await processorRepository.RecordProcessorConnectionHealthAsync(
                        connection.Id,
                        submission.IsAccepted ? "healthy" : "degraded",
                        submission.Message,
                        cancellationToken);
                    await activityFeedRepository.RecordActivityAsync(
                        submission.IsAccepted ? "processing.submitted" : "processing.submission-failed",
                        submission.IsAccepted
                            ? $"Deluno submitted {item.Title} to {connection.Name}."
                            : $"Deluno could not submit {item.Title} to {connection.Name}. {submission.Message}",
                        JsonSerializer.Serialize(new
                        {
                            HandoffId = handoff.Id,
                            ConnectionId = connection.Id,
                            connection.Name,
                            connection.Provider,
                            submission.Status,
                            submission.StatusCode
                        }, PayloadJsonOptions),
                        null,
                        "processor-handoff",
                        handoff.Id,
                        cancellationToken);
                }
                var waitKey = handoff.Id;
                if (currentHandoff.Status != "failed" && recentWaitingKeys.Add(waitKey))
                {
                    var waitingMessage = submission?.IsAccepted == true
                        ? $"Deluno submitted {item.Title} to {connection!.Name} and is waiting for a cleaned output."
                        : $"{item.Title} is complete in {item.ClientName}; Deluno is waiting for {library.ProcessorName ?? "the configured processor"} to produce a cleaned output.";
                    await activityFeedRepository.RecordActivityAsync(
                        "processing.waiting",
                        waitingMessage,
                        JsonSerializer.Serialize(new
                        {
                            item.ClientId,
                            item.ClientName,
                            item.ReleaseName,
                            item.SourcePath,
                            HandoffId = handoff.Id,
                            library.Id,
                            library.Name,
                            library.ProcessorName,
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

            var dispatch = await jobQueueRepository.FindRecentDispatchLinkAsync(
                item.ClientId,
                item.ReleaseName,
                cancellationToken);

            // Name from the catalogue when Deluno knows which title it grabbed.
            // The client reports a release name ("Blade.Runner.2049.2017.1080p…"),
            // and parsing it produced both a mangled folder name and the wrong
            // year — 2049 is part of the title, not the release year (#268).
            var (catalogueTitle, catalogueYear) = await ResolveCatalogueNamingAsync(
                dispatch,
                movieCatalogRepository,
                seriesCatalogRepository,
                cancellationToken);

            var request = new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: item.SourcePath,
                    FileName: InferImportFileName(item),
                    MediaType: item.MediaType,
                    Title: catalogueTitle ?? item.Title,
                    Year: catalogueYear ?? InferYear(item.ReleaseName),
                    Genres: [],
                    Tags: string.IsNullOrWhiteSpace(item.Category) ? [] : [item.Category],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "auto",
                Overwrite: false,
                AllowCopyFallback: true,
                ForceReplacement: false,
                DispatchId: dispatch?.DispatchId);

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: JsonSerializer.Serialize(request, PayloadJsonOptions),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    // The catalogue item this import belongs to, when the grab
                    // is known — so the job is traceable to the title.
                    RelatedEntityId: dispatch?.EntityId),
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
    }

    private static async Task RecordUnmatchedProcessorOutputsAsync(
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
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
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

            foreach (var file in files)
            {
                var sourceKey = NormalizeSourceKey(file);
                if (knownImportSources.Contains(sourceKey))
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

    /// <summary>
    /// Completes the processor-agnostic path. A processor does not need to call
    /// Deluno or use a vendor adapter: it writes its result below the configured
    /// processed-output root while retaining the final source-folder name. That
    /// one stable path component lets Deluno match the output to a durable
    /// hand-off without guessing from a release title.
    /// </summary>
    private static async Task ReconcileMatchedProcessorOutputsAsync(
        IJobScheduler jobScheduler,
        IProcessorRepository processorRepository,
        IActivityFeedRepository activityFeedRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        CancellationToken cancellationToken)
    {
        var waitingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "waiting", "submitted", "accepted", "started"
        };
        var handoffs = await processorRepository.ListProcessorHandoffsAsync(null, 250, cancellationToken);

        foreach (var handoff in handoffs.Where(item => waitingStatuses.Contains(item.Status)))
        {
            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ProcessorOutputPath));
            if (library is null)
            {
                continue;
            }

            var candidates = FindCorrelatedProcessorOutputs(library.ProcessorOutputPath!, handoff.SourcePath)
                .Where(path => !knownImportSources.Contains(NormalizeSourceKey(path)))
                .Where(ProcessorOutputReadiness.IsReady)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var outputPath = candidates[0];
            var importPayload = new
            {
                preview = new
                {
                    sourcePath = outputPath,
                    fileName = Path.GetFileName(outputPath),
                    mediaType = library.MediaType,
                    title = Path.GetFileNameWithoutExtension(outputPath),
                    year = (int?)null,
                    genres = Array.Empty<string>(),
                    tags = new[] { "processed" },
                    studio = (string?)null,
                    originalLanguage = (string?)null
                },
                transferMode = "auto",
                overwrite = false,
                allowCopyFallback = true,
                forceReplacement = false
            };

            var importJob = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "processor-output-watch",
                    PayloadJson: JsonSerializer.Serialize(importPayload),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    RelatedEntityId: null,
                    IdempotencyKey: $"processor-output:{library.Id}:{Path.GetFullPath(outputPath).ToLowerInvariant()}"),
                cancellationToken);

            await processorRepository.UpdateProcessorHandoffAsync(
                handoff.Id,
                "completed",
                outputPath,
                importJob.Id,
                null,
                cancellationToken);
            knownImportSources.Add(NormalizeSourceKey(outputPath));
            await activityFeedRepository.RecordActivityAsync(
                "processing.output.matched.import-queued",
                $"Deluno matched processed output for {handoff.ReleaseName} and queued it for import.",
                JsonSerializer.Serialize(new
                {
                    HandoffId = handoff.Id,
                    library.Id,
                    library.Name,
                    SourcePath = handoff.SourcePath,
                    OutputPath = outputPath,
                    JobId = importJob.Id,
                    Match = "stable processed-output subfolder"
                }, PayloadJsonOptions),
                importJob.Id,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> FindCorrelatedProcessorOutputs(string outputRoot, string sourcePath)
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

            var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
            if (string.IsNullOrWhiteSpace(sourceLeaf) || sourceLeaf is "." or "..")
            {
                return [];
            }

            var directoryNames = new[]
            {
                sourceLeaf,
                Path.GetFileNameWithoutExtension(sourceLeaf)
            }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = new List<string>();
            foreach (var directoryName in directoryNames)
            {
                var expectedDirectory = Path.GetFullPath(Path.Combine(root, directoryName));
                if (!expectedDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expectedDirectory))
                {
                    continue;
                }

                matches.AddRange(Directory.EnumerateFiles(expectedDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(IsImportableVideoFile)
                    .Where(ProcessorOutputReadiness.IsReady)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2));
            }

            return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static async Task RecordProcessorTimeoutsAsync(
        IProcessorRepository processorRepository,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<ActivityEventItem> recentActivity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var timeoutKeys = recentActivity
            .Where(item => item.Category == "processing.timeout")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var waiting in recentActivity.Where(item => item.Category == "processing.waiting"))
        {
            if (string.IsNullOrWhiteSpace(waiting.RelatedEntityId) ||
                timeoutKeys.Contains(waiting.RelatedEntityId))
            {
                continue;
            }

            var details = TryReadProcessingWaitDetails(waiting.DetailsJson);
            var library = !string.IsNullOrWhiteSpace(details.LibraryId)
                ? libraries.FirstOrDefault(item => string.Equals(item.Id, details.LibraryId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (library is null)
            {
                continue;
            }

            var timeout = TimeSpan.FromMinutes(Math.Max(1, library.ProcessorTimeoutMinutes));
            if (now - waiting.CreatedUtc < timeout)
            {
                continue;
            }

            var title = details.ReleaseName ?? details.SourcePath ?? "Processor output";
            var summary = $"{title} waited longer than {library.ProcessorTimeoutMinutes} minutes for a cleaned processor output.";
            var recommended = library.ProcessorFailureMode switch
            {
                "import-original" => "Review the original download, then manually queue import if it is acceptable.",
                "manual-review" => "Open Queue recovery and choose retry, manual import, or dismiss.",
                _ => "Check the processor logs and output folder, then retry once the cleaned file exists."
            };

            if (library.MediaType == "tv")
            {
                await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateSeriesImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }
            else
            {
                await movieCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateMovieImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }

            await processorRepository.UpdateProcessorHandoffAsync(
                waiting.RelatedEntityId,
                "timed-out",
                null,
                null,
                summary,
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "processing.timeout",
                summary,
                waiting.DetailsJson,
                null,
                "download",
                waiting.RelatedEntityId,
                cancellationToken);
        }
    }

    private static bool IsImportableVideoFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".mov" or ".m4v";

    private static ProcessingWaitDetails TryReadProcessingWaitDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return new ProcessingWaitDetails(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            return new ProcessingWaitDetails(
                TryGetProperty(root, "libraryId", out var libraryId) && libraryId.ValueKind == JsonValueKind.String ? libraryId.GetString() : null,
                TryGetProperty(root, "releaseName", out var releaseName) && releaseName.ValueKind == JsonValueKind.String ? releaseName.GetString() : null,
                TryGetProperty(root, "sourcePath", out var sourcePath) && sourcePath.ValueKind == JsonValueKind.String ? sourcePath.GetString() : null);
        }
        catch (JsonException)
        {
            return new ProcessingWaitDetails(null, null, null);
        }
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

    private static string? TryReadImportSourcePath(string? payloadJson)
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

    private static string InferImportFileName(DownloadQueueItem item)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(item.ReleaseName
            .Select(character => invalid.Contains(character) ? '.' : character)
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

        return $"{(string.IsNullOrWhiteSpace(cleaned) ? item.Id : cleaned)}.mkv";
    }

    /// <summary>
    /// The catalogue title and year behind a dispatched release, or (null,
    /// null) when the grab cannot be tied to a catalogue item — in which case
    /// the caller falls back to parsing the release name.
    /// </summary>
    private static async Task<(string? Title, int? Year)> ResolveCatalogueNamingAsync(
        DispatchCatalogueLink? dispatch,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        CancellationToken cancellationToken)
    {
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return (null, null);
        }

        if (string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase))
        {
            var series = await seriesCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
            return series is null ? (null, null) : (series.Title, series.StartYear);
        }

        var movie = await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken);
        return movie is null ? (null, null) : (movie.Title, movie.ReleaseYear);
    }

    private static int? InferYear(string value) => ReleaseNameParser.InferYear(value);

    private sealed record ProcessingWaitDetails(
        string? LibraryId,
        string? ReleaseName,
        string? SourcePath);
}
