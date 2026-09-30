using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Media;

namespace Deluno.Worker.Jobs;

public abstract class LibrarySearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IMediaTagStore? mediaTagStore = null,
    IMediaStateRepository? mediaStateRepository = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
    IBlockedReleaseRepository? blockedReleases = null,
    IJobScheduler? jobScheduler = null,
    IPlatformSettingsRepository? platformSettingsRepository = null,
    ISearchQueuePauseGate? queuePauseGate = null) : IJobHandler
{
    /// <summary>
    /// Both catalogues run the same handler; only the lane differs.
    ///
    /// The split is about *isolation*, not behaviour — a TV search must not
    /// queue behind movie searches, especially when those are stuck against an
    /// unresponsive indexer. The work itself is identical, so duplicating this
    /// class would be duplicating four hundred lines to change one string.
    /// `MoviesLibrarySearchJobHandler` and `TvLibrarySearchJobHandler` are two
    /// lines each and exist only to give the registry two job types to route.
    /// </summary>
    public abstract string JobType { get; }

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseLibraryPayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LibraryName))
        {
            return "Finished checking a library.";
        }

        var now = timeProvider.GetUtcNow();
        var pausedMessage = await TryPauseForLongQueueAsync(job, payload, cancellationToken);
        if (pausedMessage is not null)
        {
            return pausedMessage;
        }

        if (IsTargeted(payload))
        {
            await ReleaseStaleDownloadingMarkAsync(payload, now, cancellationToken);
        }

        var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
        var configuredSources = routing?.Sources.Count ?? 0;
        var configuredClients = routing?.DownloadClients.Count ?? 0;
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        var searchStatus = ResolveSearchStatus(payload);
        var scopeFilters = ResolveScopeFilters(payload, payload.MediaType == "movies" ? MediaKind.Movie : MediaKind.Series);
        var customFormats = await SearchExecutionSupport.ResolveCustomFormatsAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);
        var profileRules = await QualityProfileResolver.ResolveSearchRulesAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);
        // #974: an automatic library search must honour "Do not upgrade
        // automatically" the same as a manual grab, or it keeps taking a
        // Proper/Repack of a file already installed.
        var properRepackAcquisitionMode = platformSettingsRepository is null
            ? null
            : await SearchPlanPreferences.ResolveProperRepackAcquisitionModeAsync(platformSettingsRepository, cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library?.QualityProfileId,
            cancellationToken,
            customFormats,
            properRepackAcquisitionMode);

        if (payload.MediaType == "movies")
        {
            return await SearchMoviesAsync(job, payload, searchStatus, scopeFilters, routing, customFormats, profileRules, preferencePlan, configuredSources, configuredClients, now, cancellationToken);
        }

        return await SearchSeriesAsync(job, payload, searchStatus, scopeFilters, routing, customFormats, profileRules, preferencePlan, configuredSources, configuredClients, now, cancellationToken);
    }

    /// <summary>
    /// #1002: the optional "pause automatic searching while many downloads are
    /// waiting" switch. Only the routine schedule is ever held: a search a
    /// person started, one aimed at a single title, and the follow-up to a
    /// refusal always run. The Activity line is written once when a pause
    /// starts, once when it ends, and once when the queue cannot be read (in
    /// which case searching carries on rather than pausing blind).
    /// </summary>
    private async Task<string?> TryPauseForLongQueueAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        CancellationToken cancellationToken)
    {
        if (queuePauseGate is null
            || IsTargeted(payload)
            || !string.Equals(payload.TriggeredBy, "schedule", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mediaType = payload.MediaType ?? "movies";
        var decision = await queuePauseGate.EvaluateAsync(mediaType, cancellationToken);
        var announcement = queuePauseGate.TakeAnnouncement(mediaType, decision);
        if (announcement is not null)
        {
            var message = announcement.Kind switch
            {
                SearchQueuePauseAnnouncementKinds.Paused => SearchQueuePauseGate.PausedMessage(mediaType, decision),
                SearchQueuePauseAnnouncementKinds.Resumed => SearchQueuePauseGate.ResumedMessage(mediaType),
                _ => SearchQueuePauseGate.UnreadableMessage(mediaType, decision)
            };
            await activityFeedRepository.RecordActivityAsync(
                "library.search.queuePause",
                message,
                null,
                job.Id,
                "library",
                payload.LibraryId,
                cancellationToken);
        }

        return decision.Paused
            ? $"Held {payload.LibraryName}'s automatic search: {decision.Waiting} downloads are waiting."
            : null;
    }

    private async Task<string> SearchMoviesAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        CatalogueFilters? scopeFilters,
        LibraryRoutingSnapshot? routing,
        IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem> customFormats,
        ProfileSearchRules profileRules,
        ReleasePreferencePlan? preferencePlan,
        int configuredSources,
        int configuredClients,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ignoreRetryWindow = IgnoresRetryWindow(payload);
        var startedUtc = now;
        var retryDelayed = ignoreRetryWindow
            ? 0
            : await movieCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken, searchStatus);
        var candidates = (await FetchMovieCandidatesAsync(payload, searchStatus, now, ignoreRetryWindow, scopeFilters, cancellationToken))
            .Where(candidate => MatchesTarget(payload, candidate.MovieId))
            .ToArray();
        IReadOnlyList<BlockedRelease> refusals = PersonAsked(payload) ? [] : await ListRefusalsAsync(cancellationToken);
        var matchedCount = 0;
        var blockedCount = 0;
        var checkedCount = 0;
        var heldCount = 0;
        var apiCallCount = 0;
        long queuedReleaseBytes = 0;

        foreach (var candidate in candidates)
        {
            if (!ignoreRetryWindow && await movieCatalogRepository.ConsumeSkipNextWantedSearchAsync(
                    candidate.MovieId,
                    payload.LibraryId,
                    cancellationToken))
            {
                var skippedNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                await movieCatalogRepository.RecordSearchAttemptAsync(
                    candidate.MovieId,
                    payload.LibraryId,
                    payload.TriggeredBy,
                    "skipped",
                    now,
                    skippedNextEligibleUtc,
                    "Skipped one scheduled search by user request.",
                    null,
                    null,
                    null,
                    cancellationToken);
                await jobQueueRepository.RecordSearchRetryWindowAsync(
                    "movie",
                    candidate.MovieId,
                    payload.LibraryId,
                    "movies",
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                    skippedNextEligibleUtc,
                    now,
                    "skipped",
                    cancellationToken);
                continue;
            }

            if (RefusedTooOften(refusals, "movies", candidate.MovieId) is { } movieRefusals)
            {
                heldCount++;
                var heldNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                await movieCatalogRepository.RecordSearchAttemptAsync(
                    candidate.MovieId,
                    payload.LibraryId,
                    payload.TriggeredBy,
                    "held",
                    now,
                    heldNextEligibleUtc,
                    ProcessorRefusals.WhySearchIsHeld(movieRefusals),
                    null,
                    null,
                    null,
                    cancellationToken);
                await jobQueueRepository.RecordSearchRetryWindowAsync(
                    "movie",
                    candidate.MovieId,
                    payload.LibraryId,
                    "movies",
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                    heldNextEligibleUtc,
                    now,
                    "held",
                    cancellationToken);
                continue;
            }

            var currentPreferenceEvaluation = await GetCurrentPreferenceEvaluationAsync(
                MediaKind.Movie,
                candidate.MovieId,
                payload.LibraryId,
                candidate.HasFile,
                candidate.FilePath,
                candidate.FileSizeBytes,
                cancellationToken);
            var movieTitleTags = mediaTagStore is null
                ? []
                : (await mediaTagStore.ListAsync(MediaKind.Movie, candidate.MovieId, cancellationToken)).Select(tag => tag.Name).ToArray();
            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    candidate.Title,
                    candidate.ReleaseYear,
                    "movies",
                    candidate.CurrentQuality,
                    candidate.TargetQuality,
                    routing?.Sources ?? [],
                    routing?.DownloadClients ?? [],
                    customFormats,
                    TagNames: movieTitleTags,
                     SearchKind: AcquisitionSearchKinds.Automatic,
                     AvailableUtc: candidate.AvailableUtc,
                     CurrentFilePresent: candidate.HasFile,
                     CurrentReleaseName: candidate.FilePath,
                     PreferencePlan: preferencePlan,
                     CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                     RuntimeMinutes: candidate.RuntimeMinutes,
                     ImdbId: candidate.ImdbId).WithProfile(profileRules),
                cancellationToken);
            if (decisionPlan.SourceCount > 0 && decisionPlan.DownloadClientCount > 0)
            {
                apiCallCount += decisionPlan.SourceCount;
            }
            var searchPlan = decisionPlan.SearchPlan;
            // Nobody was asked (#598): a search for one title runs again
            // shortly; in a pass over many, this one stays eligible for the
            // next pass rather than recording a search that never happened.
            if (SearchExecutionSupport.IndexersWereBusy(searchPlan))
            {
                if (IsTargeted(payload))
                {
                    SearchExecutionSupport.DeferIfIndexersWereBusy(searchPlan);
                }

                continue;
            }
            var bestCandidate = searchPlan.BestCandidate;
            var outcome = decisionPlan.Outcome;
            var searchResult = decisionPlan.SearchResult;
            var onItsWay = decisionPlan.ShouldDispatch && bestCandidate is not null
                ? await SearchExecutionSupport.AlreadyOnItsWayAsync(jobQueueRepository, bestCandidate.ReleaseName, [candidate.MovieId], cancellationToken)
                : null;
            if (onItsWay is not null)
            {
                outcome = "skipped";
                searchResult = onItsWay;
            }

            if (outcome == "matched")
            {
                matchedCount++;
            }
            else if (outcome == "held")
            {
                heldCount++;
            }
            else if (outcome == "blocked")
            {
                blockedCount++;
            }
            else
            {
                checkedCount++;
            }

            if (onItsWay is null && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
            {
                var downloadClient = decisionPlan.SelectedDownloadClient;
                var grabResult = await SearchExecutionSupport.GrabBestCandidateAsync(
                    downloadClientGrabService,
                    downloadClient.DownloadClientId,
                    bestCandidate!,
                    decisionPlan.DispatchRequest,
                    cancellationToken);

                if (mediaStateRepository is not null)
                {
                    await mediaStateRepository.MarkDownloadingIfSentAsync(MediaKind.Movie, candidate.MovieId, grabResult.Status, now, cancellationToken);
                }

                await SearchExecutionSupport.BlockDeadReleaseAsync(
                    blockedReleases,
                    activityFeedRepository,
                    grabResult,
                    "movies",
                    "movie",
                    candidate.MovieId,
                    candidate.Title,
                    bestCandidate!.IndexerName,
                    downloadClient.DownloadClientName,
                    cancellationToken,
                    jobScheduler,
                    payload.LibraryId,
                    payload.LibraryName,
                    bestCandidate.TorrentHashOrItemId);

                await SearchExecutionSupport.RecordDuplicateFollowAsync(
                    activityFeedRepository,
                    grabResult,
                    "movie",
                    candidate.MovieId,
                    cancellationToken);

                await SearchExecutionSupport.RetryConnectivityFailureAsync(
                    jobScheduler,
                    activityFeedRepository,
                    grabResult,
                    decisionPlan.DispatchRequest,
                    timeProvider,
                    payload.LibraryId,
                    "movies",
                    "movie",
                    candidate.MovieId,
                    candidate.Title,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    cancellationToken,
                    bestCandidate.TorrentHashOrItemId);

                await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
                    activityFeedRepository,
                    acquisitionPipeline,
                    grabResult,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    movieTitleTags,
                    "movie",
                    candidate.MovieId,
                    cancellationToken);

                await jobQueueRepository.RecordDownloadDispatchAsync(
                    payload.LibraryId,
                    "movies",
                    "movie",
                    candidate.MovieId,
                    bestCandidate!.ReleaseName,
                    bestCandidate.IndexerName,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    grabResult.Status,
                    SearchExecutionSupport.SerializeSearchPlan(searchPlan, grabResult),
                    grabResponseCode: grabResult.Succeeded ? 200 : 400,
                    grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                    cancellationToken: cancellationToken,
                    failure: grabResult.Failure,
                    replacementAuthorized: candidate.HasFile,
                    replacementExpectedPath: candidate.FilePath,
                    clientExternalId: grabResult.ExternalId);
                if (bestCandidate?.SizeBytes is > 0)
                {
                    queuedReleaseBytes += bestCandidate.SizeBytes.Value;
                }
            }

            await movieCatalogRepository.RecordSearchAttemptAsync(
                candidate.MovieId,
                payload.LibraryId,
                payload.TriggeredBy,
                outcome,
                now,
                now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                searchResult,
                bestCandidate?.ReleaseName,
                bestCandidate?.IndexerName,
                SearchExecutionSupport.SerializeSearchPlan(searchPlan),
                cancellationToken);

            var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
            await jobQueueRepository.RecordSearchRetryWindowAsync(
                "movie",
                candidate.MovieId,
                payload.LibraryId,
                "movies",
                SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                nextEligibleUtc,
                now,
                outcome,
                cancellationToken);
        }

        await jobQueueRepository.RecordSearchCycleRunAsync(
            new RecordSearchCycleRunRequest(
                payload.LibraryId,
                payload.LibraryName,
                "movies",
                payload.TriggeredBy,
                candidates.Length > 0 || retryDelayed > 0 ? "completed" : "empty",
                candidates.Length,
                matchedCount,
                retryDelayed,
                SearchExecutionSupport.SerializeCycleNotes(configuredSources, configuredClients, checkedCount, matchedCount, blockedCount, heldCount, retryDelayed, payload.MaxItems, apiCallCount, queuedReleaseBytes),
                startedUtc,
                timeProvider.GetUtcNow(),
                searchStatus ?? "combined"),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.search.executed",
            SearchExecutionSupport.FormatExecutionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie"),
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return SearchExecutionSupport.FormatCompletionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie");
    }

    /// <summary>
    /// How many eligible titles a targeted search reads before it filters to
    /// its target. The limit used to be the payload's <c>MaxItems</c> - one -
    /// applied before the filter, so a search aimed at one title took whichever
    /// title happened to come first and then threw it away (#497). Big enough
    /// to hold any real library.
    /// </summary>
    private const int TargetedCandidateLimit = 100_000;

    /// <summary>
    /// How many copies the processor has refused, when that is enough to stop
    /// fetching another (#583); null otherwise. Every search but a person's
    /// (see <see cref="PersonAsked"/>). The skip is recorded like any other search, so the title moves to the
    /// back of the queue instead of taking the run's budget every time.
    /// </summary>
    private static int? RefusedTooOften(IReadOnlyList<BlockedRelease> refusals, string mediaType, string entityId)
    {
        var refused = ProcessorRefusals.Count(refusals.Where(release =>
            string.Equals(release.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(release.EntityId, entityId, StringComparison.OrdinalIgnoreCase)));
        return ProcessorRefusals.StopSearching(refused) ? refused : null;
    }

    /// <summary>
    /// Whether a person asked for this search. Theirs to make whatever the
    /// processor has refused; every other search - the schedule, RSS sync, a
    /// follow-up to a refusal - is held (#583, #587).
    /// </summary>
    private static bool PersonAsked(JobPayloads.LibrarySearchPayload payload)
        => string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this search may pick a title that is unmonitored: a person's own
    /// request, or another tool asking Deluno to download a file again. That file
    /// was just removed on purpose, so the search must run even if the title was
    /// unmonitored earlier (#1065).
    /// </summary>
    private static bool AsksPastMonitored(JobPayloads.LibrarySearchPayload payload)
        => PersonAsked(payload)
            || string.Equals(payload.TriggeredBy, "external-redownload", StringComparison.OrdinalIgnoreCase);

    /// <summary>The blocklist, read once per run rather than once per title.</summary>
    private async Task<IReadOnlyList<BlockedRelease>> ListRefusalsAsync(CancellationToken cancellationToken)
        => blockedReleases is null ? [] : await blockedReleases.ListAsync(cancellationToken);

    private static bool IsTargeted(JobPayloads.LibrarySearchPayload payload)
        => !string.IsNullOrWhiteSpace(payload.TargetEntityId);

    /// <summary>
    /// A search aimed at one title usually follows the end of its download:
    /// refused after it stalled, rejected by the processor, or retried from a
    /// recovery case. The title is still marked downloading at that point, and
    /// a title marked downloading is not a candidate. The reconciler clears the
    /// mark within a minute, but this search can start first, find nothing to
    /// do, and leave the title waiting for its next routine search (#560). If
    /// nothing is in flight for the title, the mark is already stale.
    /// </summary>
    private async Task ReleaseStaleDownloadingMarkAsync(
        JobPayloads.LibrarySearchPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (mediaStateRepository is null)
        {
            return;
        }

        var inFlight = await jobQueueRepository.ListDownloadsInFlightAsync(DispatchLiveDownloadLookup.Limit, cancellationToken);
        if (inFlight.Any(download => string.Equals(download.EntityId, payload.TargetEntityId, StringComparison.Ordinal)))
        {
            return;
        }

        await mediaStateRepository.SetDownloadingAsync(
            payload.MediaType == "movies" ? MediaKind.Movie : MediaKind.Series,
            payload.TargetEntityId!,
            downloading: false,
            now,
            cancellationToken);
    }

    /// <summary>
    /// A search aimed at one title was asked for because something just
    /// happened to it - its release was refused, its download died - so the
    /// retry window that paces routine searching does not apply.
    /// </summary>
    private static bool IgnoresRetryWindow(JobPayloads.LibrarySearchPayload payload)
        => IsTargeted(payload) || string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The candidate-list cap for a specific kind ("missing" or "upgrade"). A
    /// combined cycle (both due, unscoped — <paramref name="searchStatus"/> is
    /// null) reads one list sized to hold both budgets at once, then
    /// <see cref="CapEachKind{T}"/> enforces each cap against its own half of
    /// that list — never letting one kind's backlog eat into the other's
    /// budget (#830). A targeted search still reads everything before
    /// filtering to its one target, same as before.
    /// </summary>
    private static int CandidateLimit(JobPayloads.LibrarySearchPayload payload, string? searchStatus)
        => IsTargeted(payload)
            ? TargetedCandidateLimit
            : searchStatus switch
            {
                "upgrade" => payload.EffectiveMaxUpgradeItems,
                "missing" => payload.EffectiveMaxMissingItems,
                _ => payload.EffectiveMaxMissingItems + payload.EffectiveMaxUpgradeItems
            };

    /// <summary>
    /// Truncates a combined missing+upgrade list so each kind is bounded by
    /// its own cap rather than the pair sharing one number (#830). A
    /// single-kind or targeted list is already exactly what it should be (the
    /// query itself was limited to one kind, or to the target's search), so
    /// this only changes anything for the combined, unscoped case.
    /// </summary>
    private static IReadOnlyList<T> CapEachKind<T>(
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        IReadOnlyList<T> candidates,
        Func<T, string> wantedStatus)
    {
        if (IsTargeted(payload) || searchStatus is not null)
        {
            return candidates;
        }

        var missingCap = payload.EffectiveMaxMissingItems;
        var upgradeCap = payload.EffectiveMaxUpgradeItems;
        var missingCount = 0;
        var upgradeCount = 0;
        var result = new List<T>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (string.Equals(wantedStatus(candidate), "upgrade", StringComparison.OrdinalIgnoreCase))
            {
                if (upgradeCount >= upgradeCap)
                {
                    continue;
                }

                upgradeCount++;
            }
            else
            {
                if (missingCount >= missingCap)
                {
                    continue;
                }

                missingCount++;
            }

            result.Add(candidate);
        }

        return result;
    }

    private async Task<IReadOnlyList<MovieWantedItem>> FetchMovieCandidatesAsync(
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CatalogueFilters? scopeFilters,
        CancellationToken cancellationToken)
    {
        var candidates = await movieCatalogRepository.ListEligibleWantedAsync(
            payload.LibraryId,
            CandidateLimit(payload, searchStatus),
            now,
            ignoreRetryWindow,
            cancellationToken,
            searchStatus,
            scopeFilters,
            requireMonitored: !AsksPastMonitored(payload),
            randomOrder: payload.RandomizeSearchOrder);
        return CapEachKind(payload, searchStatus, candidates, candidate => candidate.WantedStatus);
    }

    /// <summary>Series candidates for one cycle — same missing/upgrade cap enforcement as <see cref="FetchMovieCandidatesAsync"/> (#830).</summary>
    private async Task<IReadOnlyList<SeriesWantedItem>> FetchSeriesCandidatesAsync(
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CatalogueFilters? scopeFilters,
        CancellationToken cancellationToken)
    {
        var candidates = await seriesCatalogRepository.ListEligibleWantedAsync(
            payload.LibraryId,
            CandidateLimit(payload, searchStatus),
            now,
            ignoreRetryWindow,
            cancellationToken,
            searchStatus,
            scopeFilters,
            requireMonitored: !AsksPastMonitored(payload),
            randomOrder: payload.RandomizeSearchOrder);
        return CapEachKind(payload, searchStatus, candidates, candidate => candidate.WantedStatus);
    }

    /// <summary>
    /// Eligible episodes for one cycle — same missing/upgrade cap
    /// enforcement as <see cref="FetchMovieCandidatesAsync"/> and
    /// <see cref="FetchSeriesCandidatesAsync"/> (#830): a combined, unscoped
    /// cycle reads one list sized to both budgets, then
    /// <see cref="CapEachKind{T}"/> truncates it so each kind is bounded by
    /// its own cap rather than one kind's backlog eating the other's budget.
    /// </summary>
    private async Task<IReadOnlyList<EpisodeSearchEligibilityItem>> FetchEligibleEpisodesAsync(
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CatalogueFilters? scopeFilters,
        CancellationToken cancellationToken)
    {
        var candidates = await seriesCatalogRepository.ListEligibleWantedEpisodesAsync(
            payload.LibraryId,
            CandidateLimit(payload, searchStatus),
            now,
            ignoreRetryWindow,
            cancellationToken,
            searchStatus,
            scopeFilters,
            requireMonitored: !AsksPastMonitored(payload));
        return CapEachKind(payload, searchStatus, candidates, episode => episode.WantedStatus);
    }

    /// <summary>
    /// Whether a candidate is the target. An episode is named by its own id
    /// or its series: a download is recorded against the episode, a person
    /// asks for the show.
    /// </summary>
    private static bool MatchesTarget(JobPayloads.LibrarySearchPayload payload, params string?[] ids)
        => !IsTargeted(payload) ||
           ids.Any(id => string.Equals(id, payload.TargetEntityId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One search decides a season (#655). The show-level pass searches
    /// generally and can still land on a season pack — the same evidence the
    /// planner just used to hand this series' season its own
    /// <c>series.search.season</c> job. Sending the show-level candidate too
    /// would dispatch the same season twice within the one cycle, before
    /// either grab has a chance to show up in the in-flight list the other
    /// path checks. Null when the best candidate is not a pack for a season
    /// the planner just claimed.
    /// </summary>
    private static string? LeftToPlannedSeasonSearch(
        string seriesId,
        MediaSearchCandidate bestCandidate,
        IReadOnlyDictionary<string, HashSet<int>> plannedSeasonNumbersBySeries)
    {
        if (!plannedSeasonNumbersBySeries.TryGetValue(seriesId, out var plannedSeasons))
        {
            return null;
        }

        var classification = SeasonPackDetector.Classify(bestCandidate.ReleaseName);
        if (classification is not { IsSeason: true, Season: { } season } || !plannedSeasons.Contains(season))
        {
            return null;
        }

        return $"Not sent: season {season} is searched on its own this cycle.";
    }

    private async Task<string> SearchSeriesAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        CatalogueFilters? scopeFilters,
        LibraryRoutingSnapshot? routing,
        IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem> customFormats,
        ProfileSearchRules profileRules,
        ReleasePreferencePlan? preferencePlan,
        int configuredSources,
        int configuredClients,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var seriesIgnoreRetryWindow = IgnoresRetryWindow(payload);
        var seriesCandidates = (await FetchSeriesCandidatesAsync(payload, searchStatus, now, seriesIgnoreRetryWindow, scopeFilters, cancellationToken))
            .Where(candidate => MatchesTarget(payload, candidate.SeriesId))
            .ToArray();
        var seriesStartedUtc = now;
        var seriesRetryDelayed = seriesIgnoreRetryWindow
            ? 0
            : await seriesCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken, searchStatus);
        IReadOnlyList<BlockedRelease> seriesRefusals = PersonAsked(payload) ? [] : await ListRefusalsAsync(cancellationToken);
        var seriesMatchedCount = 0;
        var seriesBlockedCount = 0;
        var seriesCheckedCount = 0;
        var seriesHeldCount = 0;
        var seriesApiCallCount = 0;
        long seriesQueuedReleaseBytes = 0;

        // The episodes the series pass cannot reach (#303).
        //
        // `library.search` searches at the series level — a season pack, a
        // series-level release. Individual episodes have their own job type,
        // `episode.search`, their own handler and their own planner, and until
        // now nothing called the planner: three pieces that each worked and
        // never met. A show missing four scattered episodes was searched for as
        // a show, found nothing at that level, and the four were never asked
        // for.
        //
        // Planned here rather than in the heartbeat's automation lane, which is
        // where the missing call was expected. The lane would have needed its
        // own copy of every gate this cycle already passed — the time-of-day
        // window, the search interval, missing-versus-upgrade, the manual
        // override and `MaxItemsPerRun` — and a second copy of a scheduling
        // rule is how the last four defects in this codebase were built. Riding
        // on the cycle, the episode pass is due exactly when the series pass is
        // due, and asks for the same half of the work.
        //
        // Decided before the series-level pass below, not after: a season the
        // planner is about to hand to its own search is left alone by the
        // show-level candidate loop, so exactly one of the two ever dispatches
        // a candidate covering it (#655).
        var eligibleEpisodes = (await FetchEligibleEpisodesAsync(payload, searchStatus, now, seriesIgnoreRetryWindow, scopeFilters, cancellationToken))
            .Where(episode => MatchesTarget(payload, episode.SeriesId, episode.EpisodeId))
            .Select(episode => new EpisodeSearchPlanItem(
                EpisodeId: episode.EpisodeId,
                SeriesId: episode.SeriesId,
                SeasonNumber: episode.SeasonNumber,
                EpisodeNumber: episode.EpisodeNumber,
                // An episode with no title yet is still worth searching for; the
                // plan payload names it by its code, which is what an indexer
                // query uses anyway.
                Title: episode.Title ?? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"))
            .ToArray();

        // A season that is entirely missing and fully aired is searched as one
        // season rather than one search per episode, so a whole-season pack
        // can win the way the manual "Search season" button already lets it
        // (#646). Inventory is only fetched for the series with at least two
        // eligible episodes in the same season — nothing smaller can ever
        // qualify — and only once per series even when several of its seasons
        // qualify.
        var seasonCandidateSeriesIds = eligibleEpisodes
            .GroupBy(episode => (episode.SeriesId, episode.SeasonNumber))
            .Where(group => group.Count() >= SeasonSearchPlanner.MinimumSeasonSize)
            .Select(group => group.Key.SeriesId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var seasonInventoryBySeries = new Dictionary<string, SeriesInventoryDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateSeriesId in seasonCandidateSeriesIds)
        {
            var candidateInventory = await seriesCatalogRepository.GetInventoryDetailAsync(candidateSeriesId, cancellationToken);
            if (candidateInventory is not null)
            {
                seasonInventoryBySeries[candidateSeriesId] = candidateInventory;
            }
        }

        var seasonPlan = SeasonSearchPlanner.Decide(
            eligibleEpisodes,
            now,
            (seriesId, seasonNumber) => seasonInventoryBySeries.TryGetValue(seriesId, out var seriesInventory)
                ? seriesInventory.Episodes.Where(episode => episode.SeasonNumber == seasonNumber).ToArray()
                : []);

        var seasonSearches = seasonPlan.SeasonSearches
            .Select(season => season with
            {
                SeriesTitle = seasonInventoryBySeries.TryGetValue(season.SeriesId, out var seriesInventory)
                    ? seriesInventory.Title
                    : season.SeriesTitle
            })
            .ToArray();

        // One search decides a season (#655): every series+season the planner
        // just handed a season search looked up here, so the show-level pass
        // below can tell a season pack it is about to send apart from one that
        // is already spoken for this cycle.
        var plannedSeasonNumbersBySeries = seasonPlan.SeasonSearches
            .GroupBy(season => season.SeriesId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(season => season.SeasonNumber).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in seriesCandidates)
        {
            if (!seriesIgnoreRetryWindow && await seriesCatalogRepository.ConsumeSkipNextWantedSearchAsync(
                    candidate.SeriesId,
                    payload.LibraryId,
                    cancellationToken))
            {
                var skippedNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                await seriesCatalogRepository.RecordSearchAttemptAsync(
                    candidate.SeriesId,
                    null,
                    payload.LibraryId,
                    payload.TriggeredBy,
                    "skipped",
                    now,
                    skippedNextEligibleUtc,
                    "Skipped one scheduled search by user request.",
                    null,
                    null,
                    null,
                    cancellationToken);
                await jobQueueRepository.RecordSearchRetryWindowAsync(
                    "series",
                    candidate.SeriesId,
                    payload.LibraryId,
                    "tv",
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                    skippedNextEligibleUtc,
                    now,
                    "skipped",
                    cancellationToken);
                continue;
            }

            if (RefusedTooOften(seriesRefusals, "tv", candidate.SeriesId) is { } seriesRefusedCount)
            {
                seriesHeldCount++;
                var heldNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                await seriesCatalogRepository.RecordSearchAttemptAsync(
                    candidate.SeriesId,
                    null,
                    payload.LibraryId,
                    payload.TriggeredBy,
                    "held",
                    now,
                    heldNextEligibleUtc,
                    ProcessorRefusals.WhySearchIsHeld(seriesRefusedCount),
                    null,
                    null,
                    null,
                    cancellationToken);
                await jobQueueRepository.RecordSearchRetryWindowAsync(
                    "series",
                    candidate.SeriesId,
                    payload.LibraryId,
                    "tv",
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                    heldNextEligibleUtc,
                    now,
                    "held",
                    cancellationToken);
                continue;
            }

            var currentPreferenceEvaluation = await GetCurrentPreferenceEvaluationAsync(
                MediaKind.Series,
                candidate.SeriesId,
                payload.LibraryId,
                candidate.HasFile,
                candidate.FilePath,
                candidate.FileSizeBytes,
                cancellationToken);
            var seriesTitleTags = mediaTagStore is null
                ? []
                : (await mediaTagStore.ListAsync(MediaKind.Series, candidate.SeriesId, cancellationToken)).Select(tag => tag.Name).ToArray();
            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    candidate.Title,
                    candidate.StartYear,
                    "tv",
                    candidate.CurrentQuality,
                    candidate.TargetQuality,
                    routing?.Sources ?? [],
                    routing?.DownloadClients ?? [],
                    customFormats,
                    TagNames: seriesTitleTags,
                     SearchKind: AcquisitionSearchKinds.Automatic,
                     AvailableUtc: candidate.AvailableUtc,
                     CurrentFilePresent: candidate.HasFile,
                     CurrentReleaseName: candidate.FilePath,
                     PreferencePlan: preferencePlan,
                     CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                     RuntimeMinutes: candidate.RuntimeMinutes,
                     ImdbId: candidate.ImdbId,
                     SeasonsWithRemovedEpisodes: (await seriesCatalogRepository.GetInventoryDetailAsync(candidate.SeriesId, cancellationToken))?.SeasonsWithRemovedEpisodes()).WithProfile(profileRules),
                cancellationToken);
            if (decisionPlan.SourceCount > 0 && decisionPlan.DownloadClientCount > 0)
            {
                seriesApiCallCount += decisionPlan.SourceCount;
            }
            var searchPlan = decisionPlan.SearchPlan;
            // Nobody was asked (#598): a search for one title runs again
            // shortly; in a pass over many, this one stays eligible for the
            // next pass rather than recording a search that never happened.
            if (SearchExecutionSupport.IndexersWereBusy(searchPlan))
            {
                if (IsTargeted(payload))
                {
                    SearchExecutionSupport.DeferIfIndexersWereBusy(searchPlan);
                }

                continue;
            }
            var bestCandidate = searchPlan.BestCandidate;
            var outcome = decisionPlan.Outcome;
            var searchResult = decisionPlan.SearchResult;
            var onItsWay = decisionPlan.ShouldDispatch && bestCandidate is not null
                ? await SearchExecutionSupport.AlreadyOnItsWayAsync(jobQueueRepository, bestCandidate.ReleaseName, [candidate.SeriesId], cancellationToken)
                  ?? LeftToPlannedSeasonSearch(candidate.SeriesId, bestCandidate, plannedSeasonNumbersBySeries)
                : null;
            if (onItsWay is not null)
            {
                outcome = "skipped";
                searchResult = onItsWay;
            }

            if (outcome == "matched")
            {
                seriesMatchedCount++;
            }
            else if (outcome == "held")
            {
                seriesHeldCount++;
            }
            else if (outcome == "blocked")
            {
                seriesBlockedCount++;
            }
            else
            {
                seriesCheckedCount++;
            }

            if (onItsWay is null && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
            {
                var downloadClient = decisionPlan.SelectedDownloadClient;
                var grabResult = await SearchExecutionSupport.GrabBestCandidateAsync(
                    downloadClientGrabService,
                    downloadClient.DownloadClientId,
                    bestCandidate!,
                    decisionPlan.DispatchRequest,
                    cancellationToken);

                if (mediaStateRepository is not null)
                {
                    await mediaStateRepository.MarkDownloadingIfSentAsync(MediaKind.Series, candidate.SeriesId, grabResult.Status, now, cancellationToken);
                }

                await SearchExecutionSupport.BlockDeadReleaseAsync(
                    blockedReleases,
                    activityFeedRepository,
                    grabResult,
                    "tv",
                    "series",
                    candidate.SeriesId,
                    candidate.Title,
                    bestCandidate!.IndexerName,
                    downloadClient.DownloadClientName,
                    cancellationToken,
                    jobScheduler,
                    payload.LibraryId,
                    payload.LibraryName,
                    bestCandidate.TorrentHashOrItemId);

                await SearchExecutionSupport.RecordDuplicateFollowAsync(
                    activityFeedRepository,
                    grabResult,
                    "series",
                    candidate.SeriesId,
                    cancellationToken);

                await SearchExecutionSupport.RetryConnectivityFailureAsync(
                    jobScheduler,
                    activityFeedRepository,
                    grabResult,
                    decisionPlan.DispatchRequest,
                    timeProvider,
                    payload.LibraryId,
                    "tv",
                    "series",
                    candidate.SeriesId,
                    candidate.Title,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    cancellationToken,
                    bestCandidate.TorrentHashOrItemId);

                await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
                    activityFeedRepository,
                    acquisitionPipeline,
                    grabResult,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    seriesTitleTags,
                    "series",
                    candidate.SeriesId,
                    cancellationToken);

                await jobQueueRepository.RecordDownloadDispatchAsync(
                    payload.LibraryId,
                    "tv",
                    "series",
                    candidate.SeriesId,
                    bestCandidate!.ReleaseName,
                    bestCandidate.IndexerName,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    grabResult.Status,
                    SearchExecutionSupport.SerializeSearchPlan(searchPlan, grabResult),
                    grabResponseCode: grabResult.Succeeded ? 200 : 400,
                    grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                    cancellationToken: cancellationToken,
                    failure: grabResult.Failure,
                    replacementAuthorized: candidate.HasFile,
                    replacementExpectedPath: candidate.FilePath,
                    clientExternalId: grabResult.ExternalId);
                if (bestCandidate?.SizeBytes is > 0)
                {
                    seriesQueuedReleaseBytes += bestCandidate.SizeBytes.Value;
                }
            }

            await seriesCatalogRepository.RecordSearchAttemptAsync(
                candidate.SeriesId,
                null,
                payload.LibraryId,
                payload.TriggeredBy,
                outcome,
                now,
                now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                searchResult,
                bestCandidate?.ReleaseName,
                bestCandidate?.IndexerName,
                SearchExecutionSupport.SerializeSearchPlan(searchPlan),
                cancellationToken);

            var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
            await jobQueueRepository.RecordSearchRetryWindowAsync(
                "series",
                candidate.SeriesId,
                payload.LibraryId,
                "tv",
                SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
                nextEligibleUtc,
                now,
                outcome,
                cancellationToken);
        }

        // Both planners de-duplicate against jobs already queued for the same
        // episode or season, so a cycle that runs while the last one is still
        // working through its plan adds nothing rather than doubling it.
        await jobQueueRepository.PlanSeasonSearchesAsync(payload.LibraryId, seasonSearches, cancellationToken);
        await jobQueueRepository.PlanEpisodeSearchesAsync(payload.LibraryId, seasonPlan.EpisodeSearches, cancellationToken);

        await jobQueueRepository.RecordSearchCycleRunAsync(
            new RecordSearchCycleRunRequest(
                payload.LibraryId,
                payload.LibraryName,
                "tv",
                payload.TriggeredBy,
                seriesCandidates.Length > 0 || seriesRetryDelayed > 0 ? "completed" : "empty",
                seriesCandidates.Length,
                seriesMatchedCount,
                seriesRetryDelayed,
                SearchExecutionSupport.SerializeCycleNotes(configuredSources, configuredClients, seriesCheckedCount, seriesMatchedCount, seriesBlockedCount, seriesHeldCount, seriesRetryDelayed, payload.MaxItems, seriesApiCallCount, seriesQueuedReleaseBytes),
                seriesStartedUtc,
                timeProvider.GetUtcNow(),
                searchStatus ?? "combined"),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.search.executed",
            SearchExecutionSupport.FormatExecutionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show"),
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return SearchExecutionSupport.FormatCompletionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show");
    }

    private async Task<PreferenceEvaluationSnapshot?> GetCurrentPreferenceEvaluationAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        bool hasFile,
        string? filePath,
        long? fileSizeBytes,
        CancellationToken cancellationToken)
    {
        if (mediaStateRepository is null || !hasFile || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            libraryId,
            fileIdentity: null,
            cancellationToken,
            filePath,
            fileSizeBytes);
    }

    private static string? ResolveSearchStatus(JobPayloads.LibrarySearchPayload payload)
    {
        if (string.Equals(payload.SearchKind, "missing", StringComparison.OrdinalIgnoreCase)) return "missing";
        if (string.Equals(payload.SearchKind, "upgrade", StringComparison.OrdinalIgnoreCase)) return "upgrade";
        if (payload.CheckMissing && !payload.CheckUpgrades) return "missing";
        if (payload.CheckUpgrades && !payload.CheckMissing) return "upgrade";
        return null;
    }

    private static CatalogueFilters? ResolveScopeFilters(
        JobPayloads.LibrarySearchPayload payload,
        MediaKind kind)
    {
        // Null in both fields is the legacy/unscoped payload. An attached view
        // with no conditions has an empty condition list and intentionally
        // remains distinguishable from this case for the TV episode query.
        if (payload.ScopeId is null && payload.ScopeConditions is null)
        {
            return null;
        }

        var conditions = payload.ScopeConditions ?? [];
        foreach (var condition in conditions)
        {
            if (condition.Values is null ||
                CatalogueFilterFields.Find(kind, condition.FieldId) is not { } field ||
                !field.Operators.Contains(condition.Operator) ||
                (CatalogueFilterOperators.TakesValues(condition.Operator)
                    ? condition.Values.Count == 0 || condition.Values.Any(string.IsNullOrWhiteSpace)
                    : condition.Values.Count != 0))
            {
                throw new InvalidOperationException(
                    $"Saved search scope {payload.ScopeId ?? "(unknown)"} contains an invalid catalogue filter.");
            }
        }

        return conditions.Count == 0 ? CatalogueFilters.None : new CatalogueFilters(conditions);
    }
}
