using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Search;

public sealed class FeedMediaSearchPlanner(
    IPlatformSettingsRepository platformRepository,
    IConnectionsRepository connectionsRepository,
    IHttpClientFactory httpClientFactory,
    IIntegrationResiliencePolicy resiliencePolicy,
    IQualityModelService qualityModelService,
    IReleaseRankingModelService rankingModelService,
    IOutboundRequestThrottle outboundRequestThrottle,
    ILogger<FeedMediaSearchPlanner> logger,
    IIndexerQueryStatsRepository? indexerQueryStatsRepository = null,
    IReleaseProfileRepository? releaseProfileRepository = null,
    IGuidePackageStore? guidePackageStore = null,
    TrackerIndexers? trackerIndexers = null)
    : IMediaSearchPlanner
{
    private const int IndexerResultLimit = 100;

    /// <summary>
    /// How many indexers are queried at once. This bounds outbound sockets,
    /// not how many indexers are searched — every matching indexer is always
    /// searched, this only decides how many are in flight together.
    ///
    /// It does not pace anything: sixteen different indexers in flight is fine,
    /// sixteen requests to the <em>same</em> indexer is what gets an account
    /// flagged. That is what the throttle below is for.
    /// </summary>
    private const int MaxConcurrentIndexerSearches = 16;

    /// <summary>
    /// The longest a search will sit waiting for its turn at one indexer.
    ///
    /// A search job holds a two-minute lease, so waiting has to stay well
    /// inside it — a job that loses its lease gets leased again by another
    /// worker, which would send the request twice, which is the opposite of the
    /// point. Past this the indexer is skipped for this pass and said so out
    /// loud; the next pass will reach it.
    /// </summary>
    private static readonly TimeSpan MaxIndexerThrottleWait = TimeSpan.FromSeconds(20);

    public async Task<MediaSearchPlan> BuildPlanAsync(
        string title,
        int? year,
        string mediaType,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<LibrarySourceLinkItem> sources,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        IReadOnlyList<string>? allowedQualities = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? tagNames = null,
        string searchKind = AcquisitionSearchKinds.Automatic,
        DateTimeOffset? availableUtc = null,
        int? currentCustomFormatScore = null,
        string? currentReleaseName = null,
        bool upgradeUntilCutoff = true,
        string? numberingScheme = null,
        int? absoluteNumber = null,
        DateOnly? airDate = null,
        int? sceneSeasonNumber = null,
        int? sceneEpisodeNumber = null,
        PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
        ReleasePreferencePlan? preferencePlan = null,
        bool currentFilePresent = false,
        IReadOnlyList<ProfileSizeRule>? sizeRules = null,
        QualityUpgradeStopPolicy? upgradeStop = null,
        ProfileAcquisitionRules? profileAcquisition = null,
        int? runtimeMinutes = null,
        /// <summary>
        /// The title's own IMDb id, when metadata gave one. A tracker
        /// definition that only filters server-side by IMDb id (EZTV's JSON
        /// API, #606) needs this to search the right show at all, rather than
        /// falling back to a keywords-only request it cannot answer usefully.
        /// </summary>
        string? imdbId = null,
        ProfileScoreLimits? scoreLimits = null)
    {
        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var normalizedSearchKind = AcquisitionSearchKinds.Normalize(searchKind);
        var taggedProfiles = releaseProfileRepository is null
            ? []
            : await releaseProfileRepository.ListApplicableAsync(tagNames, cancellationToken);
        // #394: the profile's own acquisition answer joins the tag-keyed rules
        // through the same combiner, rather than becoming a second mechanism
        // beside them. Its own answer goes first so it is the one a reader sees
        // named when the two agree.
        var applicableProfiles = profileAcquisition is null
            ? taggedProfiles
            : new[] { AsReleaseProfile(profileAcquisition) }.Concat(taggedProfiles).ToArray();
        var sourceIndexers = sources
            .Join(
                indexers.Where(item => item.IsEnabled
                    && SearchKindEnabled(item, normalizedSearchKind)
                    && CoversMediaType(item, mediaType)),
                source => source.IndexerId,
                indexer => indexer.Id,
                (source, indexer) => (source, indexer),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(pair => pair.source.Priority)
            .ThenBy(pair => pair.indexer.Priority)
            .ToArray();

        if (sourceIndexers.Length == 0)
        {
            logger.LogWarning(
                "Search for {Title} has no enabled {MediaType} indexers linked to the library policy.",
                title,
                mediaType);
            return new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: $"No enabled {mediaType} indexers are linked to this library policy. Add or enable an indexer before searching for {title}.",
                Reason: MediaSearchReasons.NoIndexers);
        }

        var settings = await platformRepository.GetAsync(cancellationToken);
        // A profile-scoped immutable plan is the source of truth for this
        // search. Do not fetch the current guide package on that path: a guide
        // update (or a temporarily unavailable guide store) must not change or
        // invalidate the meaning of the plan already attached to the profile.
        var guidePackage = preferencePlan is not null || guidePackageStore is null
            ? null
            : (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
        preferencePlan ??= ReleasePreferencePlanFactory.CreateQualityPlan(
            mediaType,
            targetQuality,
            allowedQualities,
            upgradeUntilCutoff: upgradeUntilCutoff,
            customFormats: customFormats,
            guidePackage: guidePackage);
        var neverGrabPatterns = settings.ReleaseNeverGrabPatterns
            .Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scoringMode = settings.SearchScoringMode;
        // Every configured indexer is queried, and they are queried at the
        // same time. Searching them one after another made the wait the sum
        // of every indexer's latency instead of the slowest one, and a
        // previous .Take(4) meant a user with more than four matching
        // indexers silently never searched the rest.
        //
        // Concurrency is bounded so a large indexer list cannot open an
        // unbounded number of outbound connections at once. Per-indexer
        // failures are already contained inside TrySearchIndexerAsync, so one
        // slow or broken indexer cannot fail the whole plan.
        var searchResults = new IndexerSearchOutcome[sourceIndexers.Length];
        await Parallel.ForAsync(
            0,
            sourceIndexers.Length,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentIndexerSearches,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var (source, indexer) = sourceIndexers[index];
                searchResults[index] = await TrySearchIndexerAsync(
                    indexer, source, title, year, mediaType, currentQuality, targetQuality,
                    customFormats, neverGrabPatterns, scoringMode, seasonNumber, episodeNumber, allowedQualities,
                     applicableProfiles, normalizedSearchKind, availableUtc, currentCustomFormatScore, currentReleaseName, upgradeUntilCutoff,
                     numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber,
                     currentPreferenceEvaluation, preferencePlan, currentFilePresent, sizeRules, upgradeStop, runtimeMinutes, imdbId, token, scoreLimits);
            });

        await RecordQueryTelemetryAsync(searchResults, cancellationToken);

        // Indexed writes above, flattened in order here, so results stay in
        // source-then-indexer priority order regardless of who answered first.
        var liveCandidates = new List<MediaSearchCandidate>();
        foreach (var result in searchResults)
        {
            liveCandidates.AddRange(result.Candidates);
        }

        if (liveCandidates.Count == 0)
        {
            // Nobody was asked (#598). Every indexer was still inside its
            // request interval when this search's wait ran out. That is not an
            // empty search, and reporting it as one sent the title a whole
            // retry window away without a single indexer having been asked.
            if (searchResults.Length > 0 && searchResults.All(result => result.Outcome == "throttled"))
            {
                return new MediaSearchPlan(
                    BestCandidate: null,
                    Candidates: [],
                    Summary: $"No indexer was asked for {title}: each was still busy with other searches. Deluno will ask again in a few minutes.",
                    Reason: MediaSearchReasons.IndexersBusy);
            }

            var failedCount = searchResults.Count(result => result.Failed);
            var reason = searchResults.All(result => result.CircuitOpen)
                ? MediaSearchReasons.CircuitOpen
                : failedCount == searchResults.Length
                    ? MediaSearchReasons.AllIndexersFailed
                    : MediaSearchReasons.NoResults;
            logger.LogWarning(
                "Search for {Title} queried {IndexerCount} indexers; {FailedCount} failed and none returned results.",
                title,
                sourceIndexers.Length,
                failedCount);
            return new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: $"No live feed results were returned for {title}. Check indexer health, categories, credentials, and network access.",
                Reason: reason,
                Failures: searchResults
                    .Where(result => result.Failure is not null)
                    .Select(result => result.Failure!)
                    .ToArray());
        }

        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(targetQuality) ?? "WEB 1080p";
        var ordered = liveCandidates
            // Candidates that cannot become the automatic winner remain
            // visible for explanation, but sort after the ones that can:
            // a release the installed file beats, or one that is equivalent
            // to it, is a legitimate release and is not a rejection. Once
            // that stage is selected, the typed comparator alone owns the
            // plan order. ReleaseDecisionStatuses holds the single table.
            .OrderBy(item => preferencePlan is null
                ? LegacyCandidateStatusRank(item)
                : TypedCandidateStageRank(item))
            .ThenBy(item => item, Comparer<MediaSearchCandidate>.Create((left, right) =>
            {
                if (left.PreferenceEvaluation is not null && right.PreferenceEvaluation is not null)
                {
                    return ReleasePreferenceEvaluator.CompareForSelection(
                        preferencePlan,
                        left.PreferenceEvaluation,
                        right.PreferenceEvaluation);
                }

                return 0;
            }))
            // Typed plans own every candidate tie-break. Legacy searches keep
            // their historical seeder ordering, but typed selection must not
            // bypass the explicit transient family with a hidden numeric
            // fallback.
            .ThenByDescending(item => preferencePlan is null ? item.Seeders ?? 0 : 0)
            .ThenBy(item => item.IndexerName)
            .ToArray();
        var deduplicated = DeduplicateEquivalentCandidates(ordered);
        var best = deduplicated.FirstOrDefault();

        return new MediaSearchPlan(
            BestCandidate: best,
            Candidates: deduplicated,
            Summary: best is null
                ? $"No usable feed release was found for {title}."
                : $"Best feed candidate is {best.ReleaseName} from {best.IndexerName} targeting {normalizedTarget}.",
            Reason: best is null ? MediaSearchReasons.NoUsableRelease : MediaSearchReasons.Ok,
            CandidatesTruncatedByIndexer: searchResults.Any(result => result.CandidatesTruncatedByIndexer),
            Failures: searchResults
                .Where(result => result.Failure is not null)
                .Select(result => result.Failure!)
                .ToArray());
    }

    /// <summary>
    /// Removes the same release when an indexer returns it more than once or
    /// different sources vary only in case/separators. Ranking is performed
    /// first, so the retained row is the best deterministic representation of
    /// that release. A preference tie is deliberately not treated as content
    /// identity: two differently named releases can have the same preference
    /// vector and must remain selectable alternatives.
    /// </summary>
    internal static IReadOnlyList<MediaSearchCandidate> DeduplicateEquivalentCandidates(
        IEnumerable<MediaSearchCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<MediaSearchCandidate>();
        foreach (var candidate in candidates)
        {
            if (seen.Add(CandidateIdentity(candidate)))
            {
                unique.Add(candidate);
            }
        }

        return unique;
    }

    private static string CandidateIdentity(MediaSearchCandidate candidate)
    {
        var normalizedReleaseName = NormalizeReleaseName(candidate.ReleaseName);
        if (normalizedReleaseName.Length > 0)
        {
            return normalizedReleaseName;
        }

        return string.Join(
            "|",
            NormalizeReleaseName(candidate.Quality),
            candidate.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            candidate.DownloadUrl?.Trim().ToLowerInvariant() ?? string.Empty);
    }

    private static string NormalizeReleaseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(buffer)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The service an indexer's requests are paced against: host and port
    /// (#598). Keyed on the host alone, two services on one machine shared one
    /// pace. On the soak rig a Torznab indexer on :9117 and a Newznab one on
    /// :9118 waited for each other, and sixty queued episode searches mostly
    /// never reached either.
    /// </summary>
    public static string ThrottleKey(Uri uri) => uri.Authority.ToLowerInvariant();

    private async Task<IndexerSearchOutcome> TrySearchIndexerAsync(
        IndexerItem indexer,
        LibrarySourceLinkItem source,
        string title,
        int? year,
        string mediaType,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<CustomFormatItem>? customFormats,
        IReadOnlyList<string> neverGrabPatterns,
        string scoringMode,
        int? seasonNumber,
        int? episodeNumber,
        IReadOnlyList<string>? allowedQualities,
        IReadOnlyList<ReleaseProfileItem> releaseProfiles,
        string searchKind,
        DateTimeOffset? availableUtc,
        int? currentCustomFormatScore,
        string? currentReleaseName,
        bool upgradeUntilCutoff,
        string? numberingScheme,
        int? absoluteNumber,
        DateOnly? airDate,
        int? sceneSeasonNumber,
        int? sceneEpisodeNumber,
        PreferenceEvaluationSnapshot? currentPreferenceEvaluation,
        ReleasePreferencePlan preferencePlan,
        bool currentFilePresent,
        IReadOnlyList<ProfileSizeRule>? sizeRules,
        QualityUpgradeStopPolicy? upgradeStop,
        int? runtimeMinutes,
        string? imdbId,
        CancellationToken cancellationToken,
        ProfileScoreLimits? scoreLimits = null)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        var queryText = BuildQueryText(title, year, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber);
        var queryKind = string.Equals(indexer.Protocol, "rss", StringComparison.OrdinalIgnoreCase)
            ? "rss"
            : "search";

        // A tracker is searched by the engine from its own address (#606); its
        // host is what the throttle and circuit below are keyed on, as for any
        // other indexer.
        var isTracker = IndexerProtocols.IsTracker(indexer.Protocol);
        var searchAddress = isTracker
            ? indexer.BaseUrl
            : BuildSearchUrl(indexer, title, year, mediaType, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber);
        if (!Uri.TryCreate(searchAddress, UriKind.Absolute, out var uri))
        {
            logger.LogWarning(
                "Indexer {IndexerName} ({IndexerId}) has an unusable search URL; skipping.",
                indexer.Name,
                indexer.Id);
            return WithTelemetry(
                new IndexerSearchOutcome(
                    [],
                    CandidatesTruncatedByIndexer: false,
                    Failed: true,
                    CircuitOpen: false,
                    Outcome: "invalid_url",
                    ErrorMessage: "The indexer URL is not valid.",
                    Failure: IntegrationFailureFactory.FromLegacy(
                        "indexer",
                        indexer.Id,
                        indexer.Name,
                        "search",
                        "configuration",
                        "The indexer URL is not valid.")),
                indexer,
                queryText,
                mediaType,
                queryKind,
                startedTimestamp);
        }

        // Paced before the request, not after the indexer complains. Keyed on
        // the service rather than the indexer id, because two indexer entries
        // can point at the same tracker and it is the tracker that does the
        // counting.
        var waited = await outboundRequestThrottle.TryAcquireAsync(
            ThrottleKey(uri),
            indexer.RequestIntervalSeconds is { } interval
                ? OutboundRate.FromInterval(TimeSpan.FromSeconds(Math.Max(2, interval)))
                : OutboundRate.PerIndexerDefault,
            MaxIndexerThrottleWait,
            cancellationToken);

        if (waited is null)
        {
            // Skipping is a real outcome and has to be visible. A search that
            // quietly queried nine of ten indexers looks identical to one that
            // queried all ten and found nothing.
            logger.LogInformation(
                "Skipped {IndexerName} ({Host}) for this search: it is still inside its request interval after {Wait}.",
                indexer.Name,
                uri.Host,
                MaxIndexerThrottleWait);

            return WithTelemetry(
                new IndexerSearchOutcome([], CandidatesTruncatedByIndexer: false, Failed: false, CircuitOpen: false, Outcome: "throttled"),
                indexer,
                queryText,
                mediaType,
                queryKind,
                startedTimestamp);
        }

        if (waited > TimeSpan.FromSeconds(1))
        {
            logger.LogDebug("Waited {Wait} before querying {Host}.", waited, uri.Host);
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                $"indexer:{indexer.Id}:{SanitizeAddress(indexer.BaseUrl)}",
                "search",
                FailureThreshold: 2,
                ServiceType: "indexer",
                ServiceId: indexer.Id,
                ServiceName: indexer.Name),
            async token =>
            {
                try
                {
                    if (isTracker)
                    {
                        if (trackerIndexers is null)
                        {
                            throw new InvalidOperationException("This Deluno cannot search trackers.");
                        }

                        // A budget for the whole tracker search (#634). Without one, a
                        // dead tracker tried each mirror for 30 seconds, then the
                        // timeout was retried as transient, and every search waited
                        // minutes for it. Out of time, it is reported and the search
                        // goes on without it.
                        var budget = await trackerIndexers.BudgetAsync(token);
                        using var trackerTime = CancellationTokenSource.CreateLinkedTokenSource(token);
                        trackerTime.CancelAfter(budget);
                        XDocument trackerDocument;
                        try
                        {
                            trackerDocument = await trackerIndexers.SearchAsync(
                                indexer,
                                TrackerQueryFor(indexer, queryText, mediaType, year, seasonNumber, episodeNumber, imdbId),
                                trackerTime.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            var message = $"{indexer.Name} did not answer within {budget.TotalSeconds:0} seconds, so this search went on without it.";
                            return new IndexerSearchOutcome(
                                [],
                                CandidatesTruncatedByIndexer: false,
                                Failed: true,
                                CircuitOpen: false,
                                Outcome: "failed",
                                ErrorMessage: message,
                                Failure: IntegrationFailureFactory.FromLegacy("indexer", indexer.Id, indexer.Name, "search", "timeout", message));
                        }

                        return await ReadCandidatesAsync(trackerDocument, token);
                    }

                    var http = httpClientFactory.CreateClient("indexers");
                    http.Timeout = TimeSpan.FromSeconds(12);
                    using var response = await http.GetAsync(uri, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (IntegrationResiliencePolicy.IsTransientHttpStatusCode(response.StatusCode))
                        {
                            throw new HttpRequestException(
                                $"Indexer {indexer.Name} returned transient HTTP {(int)response.StatusCode}.",
                                null,
                                response.StatusCode);
                        }

                        logger.LogWarning(
                            "Indexer {IndexerName} ({IndexerId}) returned non-transient HTTP status {StatusCode}.",
                            indexer.Name,
                            indexer.Id,
                            (int)response.StatusCode);
                        return new IndexerSearchOutcome(
                            [],
                            CandidatesTruncatedByIndexer: false,
                            Failed: true,
                            CircuitOpen: false,
                            Outcome: "failed",
                            ErrorMessage: $"HTTP {(int)response.StatusCode}",
                            Failure: IntegrationFailureFactory.FromHttpStatus(
                                "indexer",
                                indexer.Id,
                                indexer.Name,
                                "search",
                                response.StatusCode,
                                $"The indexer returned HTTP {(int)response.StatusCode}.",
                                upstreamDetail: await ReadUpstreamDetailAsync(response, token)));
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    var document = await XDocument.LoadAsync(stream, LoadOptions.None, token);
                    return await ReadCandidatesAsync(document, token);
                }
                catch (Exception exception) when (exception is not HttpRequestException and not TaskCanceledException and not IOException)
                {
                    logger.LogWarning(
                        exception,
                        "Indexer {IndexerName} ({IndexerId}) returned a response Deluno could not read.",
                        indexer.Name,
                        indexer.Id);
                    return new IndexerSearchOutcome(
                        [],
                        CandidatesTruncatedByIndexer: false,
                        Failed: true,
                        CircuitOpen: false,
                        Outcome: "failed",
                        ErrorMessage: exception.Message,
                        Failure: IntegrationFailureFactory.FromException(
                            "indexer",
                            indexer.Id,
                            indexer.Name,
                            "search",
                            exception));
                }

                // The same reading for a Torznab or Newznab feed and for a
                // tracker's results, which the engine hands back as one.
                async Task<IndexerSearchOutcome> ReadCandidatesAsync(XDocument document, CancellationToken token)
                {
                    var qualityModel = await qualityModelService.GetAsync(token);
                    var parsed = ParseCandidates(
                        document,
                        indexer,
                        source,
                        currentQuality,
                        targetQuality,
                        customFormats,
                        neverGrabPatterns,
                        qualityModel,
                        scoringMode,
                        rankingModelService,
                        allowedQualities,
                        releaseProfiles,
                        availableUtc,
                        currentCustomFormatScore,
                        currentReleaseName,
                        upgradeUntilCutoff,
                     currentPreferenceEvaluation,
                        preferencePlan,
                        currentFilePresent,
                        sizeRules: sizeRules,
                        upgradeStop: upgradeStop,
                        mediaType: mediaType,
                        expectation: TitleExpectation(title, year, mediaType, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber),
                        runtimeMinutes: runtimeMinutes,
                        scoreLimits: scoreLimits);
                    return new IndexerSearchOutcome(
                        parsed.Candidates,
                        parsed.CandidatesTruncatedByIndexer,
                        Failed: false,
                        CircuitOpen: false,
                        Outcome: parsed.Candidates.Count == 0 ? "no_results" : "matched");
                }
            },
            _ => IntegrationResilienceOutcome.Success,
            cancellationToken);

        if (result.CircuitOpen)
        {
            logger.LogInformation(
                "Indexer {IndexerName} ({IndexerId}) search circuit is open; skipping until {RetryAfterUtc}.",
                indexer.Name,
                indexer.Id,
                result.RetryAfterUtc);

            // An open circuit that only appears in a search's failures list is
            // invisible to anyone not reading that one search's result: the
            // Indexers screen kept showing this indexer as healthy/untested
            // while every search silently skipped it (#606). Write the same
            // fact to the indexer's own health so the screen says it in plain
            // words and the existing Reset action can clear it.
            await MarkCircuitOpenHealthAsync(indexer, result.RetryAfterUtc, cancellationToken);
        }

        // IntegrationResilienceResult<T>.Value is declared `T?`, and
        // IndexerSearchOutcome is a struct, so `is { }` is a null test that a
        // value type can never fail: on an exhausted retry, an open circuit or
        // a refused connection the policy hands back default(T), whose
        // Candidates list is null. Every indexer failure therefore threw a
        // NullReferenceException out of WithTelemetry and returned HTTP 500,
        // instead of being reported as the typed failure this method has
        // already built. Ask whether the operation succeeded, which is a
        // question a value type can answer.
        var resolved = result.Succeeded && result.Value is { Candidates: not null } value
            ? value
            : new IndexerSearchOutcome(
                [],
                CandidatesTruncatedByIndexer: false,
                Failed: true,
                CircuitOpen: result.CircuitOpen,
                Outcome: result.CircuitOpen ? "circuit_open" : "failed",
                ErrorMessage: result.FailureMessage,
                Failure: result.Failure);
        var final = resolved with
        {
            Failed = resolved.Failed || result.CircuitOpen || result.CircuitOpened || result.FailureMessage is not null,
            CircuitOpen = result.CircuitOpen,
            Outcome = result.CircuitOpen
                ? "circuit_open"
                : result.CircuitOpened || result.FailureMessage is not null
                    ? "failed"
                    : resolved.Outcome,
            ErrorMessage = result.FailureMessage ?? resolved.ErrorMessage,
            Failure = result.Failure ?? resolved.Failure
        };
        return WithTelemetry(final, indexer, queryText, mediaType, queryKind, startedTimestamp);
    }

    /// <summary>
    /// Records an open search circuit on the indexer's own health, in plain
    /// words, with the time it will try again. Without this, an indexer whose
    /// circuit opened during background search stayed marked
    /// healthy/untested on the Indexers screen — nothing on that screen ever
    /// changed, so nobody knew to use the Reset action (#606).
    ///
    /// A failure here is logged and swallowed: a health write must never turn
    /// a skipped indexer into a failed search for every other indexer still
    /// running under the same <see cref="Parallel.ForAsync{TFrom}(TFrom,TFrom,ParallelOptions,Func{TFrom,CancellationToken,ValueTask})"/> call.
    /// </summary>
    private async Task MarkCircuitOpenHealthAsync(
        IndexerItem indexer,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken)
    {
        var message = retryAfterUtc is { } retryAt
            ? $"Deluno stopped asking {indexer.Name} after repeated failures; it will try again at {retryAt.UtcDateTime:HH:mm} UTC. Reset to try now."
            : $"Deluno stopped asking {indexer.Name} after repeated failures. Reset to try now.";

        try
        {
            await connectionsRepository.UpdateIndexerHealthAsync(
                indexer.Id,
                "circuitOpen",
                message,
                "circuitOpen",
                latencyMs: null,
                cancellationToken,
                failure: IntegrationFailureFactory.CircuitOpen(
                    "indexer",
                    indexer.Id,
                    indexer.Name,
                    "search",
                    retryAfterUtc,
                    "Deluno paused it after repeated failures."));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to record the open search circuit on indexer {IndexerName} ({IndexerId}).",
                indexer.Name,
                indexer.Id);
        }
    }

    private readonly record struct IndexerSearchOutcome(
        IReadOnlyList<MediaSearchCandidate> Candidates,
        bool CandidatesTruncatedByIndexer,
        bool Failed,
        bool CircuitOpen,
        string Outcome = "failed",
        string? ErrorMessage = null,
        IndexerQueryLogEntry? Telemetry = null,
        IntegrationFailure? Failure = null);

    private static async Task<string?> ReadUpstreamDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body)
                ? null
                : body.Length <= 1000 ? body : body[..1000];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task RecordQueryTelemetryAsync(
        IReadOnlyList<IndexerSearchOutcome> searchResults,
        CancellationToken cancellationToken)
    {
        if (indexerQueryStatsRepository is null)
        {
            return;
        }

        var entries = searchResults
            .Select(result => result.Telemetry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        try
        {
            await indexerQueryStatsRepository.RecordBatchAsync(entries, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A scoreboard is diagnostic telemetry. It must never turn a
            // successful search into a failed acquisition because its write
            // was temporarily unavailable.
            logger.LogWarning(exception, "Could not persist telemetry for {IndexerCount} indexer queries.", entries.Length);
        }
    }

    private static IndexerSearchOutcome WithTelemetry(
        IndexerSearchOutcome result,
        IndexerItem indexer,
        string queryText,
        string mediaType,
        string queryKind,
        long startedTimestamp)
        => result with
        {
            Telemetry = new IndexerQueryLogEntry(
                IndexerId: indexer.Id,
                IndexerName: indexer.Name,
                QueryText: queryText,
                Categories: indexer.Categories,
                MediaType: mediaType,
                QueryKind: queryKind,
                Outcome: result.Outcome,
                ElapsedMilliseconds: ElapsedMilliseconds(startedTimestamp),
                CandidateCount: result.Candidates.Count,
                CreatedUtc: DateTimeOffset.UtcNow,
                ErrorMessage: result.ErrorMessage,
                Failure: result.Failure)
        };

    private static string BuildQueryText(
        string title,
        int? year,
        int? seasonNumber,
        int? episodeNumber,
        string? numberingScheme = null,
        int? absoluteNumber = null,
        DateOnly? airDate = null,
        int? sceneSeasonNumber = null,
        int? sceneEpisodeNumber = null)
    {
        var normalizedScheme = numberingScheme?.Trim().ToLowerInvariant();
        var suffix = normalizedScheme == "absolute" && absoluteNumber is not null
            ? $" {absoluteNumber.Value.ToString(CultureInfo.InvariantCulture)}"
            : normalizedScheme == "airdate" && airDate is not null
                ? $" {airDate.Value:yyyy-MM-dd}"
                : normalizedScheme == "scene" && sceneSeasonNumber is not null
                    ? $" S{sceneSeasonNumber.Value:D2}{(sceneEpisodeNumber is null ? string.Empty : $"E{sceneEpisodeNumber.Value:D2}")}"
                    : year is not null && seasonNumber is null
            ? $" {year.Value.ToString(CultureInfo.InvariantCulture)}"
            : seasonNumber is null
                ? string.Empty
                : $" S{seasonNumber.Value:D2}{(episodeNumber is null ? string.Empty : $"E{episodeNumber.Value:D2}")}";
        return $"{title.Trim()}{suffix}";
    }

    /// <summary>
    /// A search as a tracker takes it (#606): the same text Newznab gets, the
    /// episode as numbers too, and only the indexer's categories for this kind
    /// of media, since a tracker files films and series apart.
    /// </summary>
    private static TrackerQuery TrackerQueryFor(IndexerItem indexer, string queryText, string mediaType, int? year, int? seasonNumber, int? episodeNumber, string? imdbId)
    {
        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var family = isTv ? 5 : 2;
        var categories = TrackerCategories.Parse(indexer.Categories).Where(category => category / 1000 == family).ToArray();
        return new TrackerQuery(
            queryText,
            isTv ? "tvsearch" : "movie",
            Season: isTv ? seasonNumber : null,
            Episode: isTv ? episodeNumber : null,
            Year: year,
            ImdbId: imdbId,
            Categories: categories.Length > 0 ? categories : [family * 1000]);
    }

    /// <summary>What a release's name has to be for (#613), numbered the way the search asked.</summary>
    private static ReleaseTitleExpectation TitleExpectation(string title, int? year, string mediaType, int? seasonNumber, int? episodeNumber, string? numberingScheme, int? absoluteNumber, DateOnly? airDate, int? sceneSeasonNumber, int? sceneEpisodeNumber)
    {
        var scheme = numberingScheme?.Trim().ToLowerInvariant();
        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        return scheme switch
        {
            "absolute" when absoluteNumber is not null => new ReleaseTitleExpectation(title, year, isTv, AbsoluteNumber: absoluteNumber),
            "airdate" when airDate is not null => new ReleaseTitleExpectation(title, year, isTv, AirDate: airDate),
            "scene" when sceneSeasonNumber is not null => new ReleaseTitleExpectation(title, year, isTv, sceneSeasonNumber, sceneEpisodeNumber),
            _ => new ReleaseTitleExpectation(title, year, isTv, seasonNumber, episodeNumber)
        };
    }

    private static int ElapsedMilliseconds(long startedTimestamp)
        => (int)Math.Clamp(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds, 0, int.MaxValue);

    private static ParsedCandidates ParseCandidates(
        XDocument document,
        IndexerItem indexer,
        LibrarySourceLinkItem source,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<CustomFormatItem>? customFormats,
        IReadOnlyList<string> neverGrabPatterns,
        QualityModelSnapshot qualityModel,
        string scoringMode,
        IReleaseRankingModelService rankingModelService,
        IReadOnlyList<string>? allowedQualities = null,
        IReadOnlyList<ReleaseProfileItem>? releaseProfiles = null,
        DateTimeOffset? availableUtc = null,
        int? currentCustomFormatScore = null,
        string? currentReleaseName = null,
        bool upgradeUntilCutoff = true,
        PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
        ReleasePreferencePlan? preferencePlan = null,
        bool currentFilePresent = false,
        int requestedLimit = IndexerResultLimit,
        IReadOnlyList<ProfileSizeRule>? sizeRules = null,
        QualityUpgradeStopPolicy? upgradeStop = null,
        string? mediaType = null,
        ReleaseTitleExpectation? expectation = null,
        int? runtimeMinutes = null,
        ProfileScoreLimits? scoreLimits = null)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";
        XNamespace newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";
        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(targetQuality) ?? "WEB 1080p";
        var results = new List<MediaSearchCandidate>();

        var feedItems = document.Descendants("item").ToArray();
        foreach (var item in feedItems)
        {
            var releaseName = ReadReleaseTitle(item);
            if (string.IsNullOrWhiteSpace(releaseName))
            {
                continue;
            }

            var downloadUrl =
                item.Elements("enclosure").FirstOrDefault()?.Attribute("url")?.Value ??
                item.Element("link")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            var attrs = item.Elements(torznab + "attr").Concat(item.Elements(newznab + "attr")).ToArray();
            var size = ReadLongAttr(attrs, "size") ?? ReadLong(item.Elements("enclosure").FirstOrDefault()?.Attribute("length")?.Value);
            var seeders = ReadIntAttr(attrs, "seeders");
            var torrentHashOrItemId = ReadHashOrItemId(attrs, item, indexer.Protocol, downloadUrl);
            var releaseAgeHours = ReadReleaseAgeHours(item);
            var indexerFlags = string.Join(", ", attrs
                .Where(attr => string.Equals(attr.Attribute("name")?.Value, "flags", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(attr.Attribute("name")?.Value, "flag", StringComparison.OrdinalIgnoreCase))
                .Select(attr => attr.Attribute("value")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var quality = InferQuality(releaseName);
            var customFormatBonus = 0;
            IReadOnlyList<CustomFormatMatchResult> matchedFormats;
            if (preferencePlan is null)
            {
                customFormatBonus = CustomFormatMatcher.EvaluateUpgradeScore(
                    releaseName,
                    customFormats,
                    out matchedFormats);
            }
            else
            {
                matchedFormats = CustomFormatMatcher.EvaluateMatches(releaseName, customFormats);
            }
            var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
                releaseName,
                quality,
                CurrentQuality: currentQuality,
                TargetQuality: normalizedTarget,
                size,
                seeders,
                downloadUrl,
                SourcePriorityScore: Math.Max(0, 200 - source.Priority),
                customFormatBonus,
                neverGrabPatterns,
                CurrentCustomFormatScore: currentCustomFormatScore,
                AllowedQualities: allowedQualities,
                SizeRules: sizeRules,
                UpgradeStop: upgradeStop,
                ReleaseProfiles: releaseProfiles,
                IndexerProtocol: indexer.Protocol,
                IndexerId: indexer.Id,
                ReleaseAgeHours: releaseAgeHours,
                MinimumAgeMinutes: indexer.MinimumAgeMinutes,
                RetentionDays: indexer.RetentionDays,
                MaximumSizeMb: indexer.MaximumSizeMb,
                IndexerFlags: indexerFlags,
                PreferIndexerFlags: indexer.PreferIndexerFlags,
                AvailableUtc: availableUtc,
                AvailabilityDelayDays: indexer.AvailabilityDelayDays,
                 PreferencePlan: preferencePlan,
                 CurrentReleaseName: currentReleaseName,
                 CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                 CurrentFilePresent: currentFilePresent,
                 MediaType: mediaType,
                 TitleMismatch: expectation is null ? null : ReleaseTitleMatch.Mismatch(releaseName, expectation),
                 RuntimeMinutes: runtimeMinutes,
                 ScoreLimits: scoreLimits));

            var boost = rankingModelService.Score(new ReleaseRankingFeatures(
                Seeders: seeders,
                SizeBytes: size,
                QualityDelta: decision.QualityDelta,
                CustomFormatScore: decision.CustomFormatScore,
                SourcePriorityScore: Math.Max(0, 200 - source.Priority),
                EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
                ReleaseAgeHours: releaseAgeHours), hardBlocked: decision.Status == "rejected");

            var scoreComputation = ReleaseScoringModePolicy.Compute(decision.Score, boost, scoringMode);
            var finalScore = preferencePlan is null ? scoreComputation.FinalScore : 0;
            var reasons = decision.Reasons.Concat(
                preferencePlan is null && scoreComputation.UsesModelSignal
                    ? [boost.Explanation, scoreComputation.Explanation]
                    : [scoreComputation.Explanation]).ToArray();
            var summary = preferencePlan is null
                ? BuildSummary(decision, matchedFormats, boost, scoreComputation)
                : decision.Summary;

            results.Add(new MediaSearchCandidate(
                ReleaseName: releaseName,
                IndexerId: indexer.Id,
                IndexerName: indexer.Name,
                Quality: quality,
                Score: finalScore,
                MeetsCutoff: decision.MeetsCutoff,
                Summary: summary,
                DownloadUrl: downloadUrl,
                SizeBytes: size,
                Seeders: seeders,
                DecisionStatus: decision.Status,
                DecisionReasons: reasons,
                RiskFlags: decision.RiskFlags,
                QualityDelta: decision.QualityDelta,
                CustomFormatScore: decision.CustomFormatScore,
                SeederScore: decision.SeederScore,
                SizeScore: decision.SizeScore,
                ReleaseGroup: decision.ReleaseGroup,
                EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
                PolicyVersion: decision.PolicyVersion,
                MatchedCustomFormats: matchedFormats,
                PreferenceEvaluation: decision.PreferenceEvaluation,
                PreferenceComparison: decision.PreferenceComparison,
                TorrentHashOrItemId: torrentHashOrItemId));
        }

        return new ParsedCandidates(results, feedItems.Length == requestedLimit);
    }

    private readonly record struct ParsedCandidates(
        IReadOnlyList<MediaSearchCandidate> Candidates,
        bool CandidatesTruncatedByIndexer);

    private static int TypedCandidateStageRank(MediaSearchCandidate candidate)
        => ReleaseDecisionStatuses.TypedStageRank(candidate.DecisionStatus);

    private static int LegacyCandidateStatusRank(MediaSearchCandidate candidate)
        => ReleaseDecisionStatuses.LegacyStageRank(candidate.DecisionStatus);

    private static string BuildSearchUrl(
        IndexerItem indexer,
        string title,
        int? year,
        string mediaType,
        int? seasonNumber,
        int? episodeNumber,
        string? numberingScheme = null,
        int? absoluteNumber = null,
        DateOnly? airDate = null,
        int? sceneSeasonNumber = null,
        int? sceneEpisodeNumber = null)
    {
        var builder = new UriBuilder(IndexerApiEndpoint.For(indexer.BaseUrl));
        var query = ParseQuery(builder.Query);

        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var normalizedScheme = numberingScheme?.Trim().ToLowerInvariant();
        var usesAlternateQuery = normalizedScheme == "absolute" && absoluteNumber is not null
            || normalizedScheme == "airdate" && airDate is not null;
        if (isTv && seasonNumber is not null && !usesAlternateQuery)
        {
            query["t"] = "tvsearch";
            query["q"] = title;
            query["season"] = (normalizedScheme == "scene" && sceneSeasonNumber is not null
                    ? sceneSeasonNumber.Value
                    : seasonNumber.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (episodeNumber is not null)
            {
                query["ep"] = (normalizedScheme == "scene" && sceneEpisodeNumber is not null
                        ? sceneEpisodeNumber.Value
                        : episodeNumber.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else
        {
            query["t"] = "search";
            query["q"] = BuildQueryText(title, year, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber);
        }

        return FinishIndexerUrl(builder, query, indexer, isTv ? "5000" : "2000");
    }

    /// <summary>
    /// An indexer's recent-releases feed: the same address as a search, with
    /// no search term (#587). Newznab and Torznab answer that with their newest
    /// releases, which is what RSS sync reads.
    /// </summary>
    internal static string BuildFeedUrl(IndexerItem indexer)
    {
        var builder = new UriBuilder(IndexerApiEndpoint.For(indexer.BaseUrl));
        var query = ParseQuery(builder.Query);
        query["t"] = "search";
        query.Remove("q");
        return FinishIndexerUrl(builder, query, indexer, "2000,5000");
    }

    /// <summary>Categories, limit and key: the same for a search and a feed read.</summary>
    private static string FinishIndexerUrl(UriBuilder builder, Dictionary<string, string> query, IndexerItem indexer, string defaultCategories)
    {
        query["cat"] = string.IsNullOrWhiteSpace(indexer.Categories)
            ? defaultCategories
            : indexer.Categories.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (!query.ContainsKey("limit"))
        {
            query["limit"] = IndexerResultLimit.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(indexer.ApiKey) && !query.ContainsKey("apikey"))
        {
            query["apikey"] = indexer.ApiKey;
        }

        builder.Query = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri.ToString();
    }

    /// <summary>A feed item's release name, as a search and RSS sync both read it.</summary>
    internal static string ReadReleaseTitle(XElement item)
        => WebUtility.HtmlDecode(item.Element("title")?.Value?.Trim() ?? string.Empty);

    /// <summary>When the indexer says a release was published, if it says.</summary>
    internal static DateTimeOffset? ReadPublishedUtc(XElement item)
    {
        var raw = item.Element("pubDate")?.Value?.Trim();
        return !string.IsNullOrWhiteSpace(raw)
               && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)
            ? published.ToUniversalTime()
            : null;
    }

    internal static string SanitizeAddress(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return value.Split('?', 2)[0].Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool CoversMediaType(IndexerItem indexer, string mediaType)
        => indexer.MediaScope == "both" ||
           string.Equals(indexer.MediaScope, mediaType == "tv" ? "tv" : "movies", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The profile's own acquisition answer, shaped as the rule the combiner
    /// already understands, so it joins the tag-keyed ones rather than becoming
    /// a second mechanism beside them.
    /// </summary>
    private static ReleaseProfileItem AsReleaseProfile(ProfileAcquisitionRules rules)
        => new(
            Id: "quality-profile",
            Name: "This profile",
            // Deliberately no tag. The rule applies because the library uses
            // this profile, not because anything was labelled.
            TagName: string.Empty,
            PreferredProtocol: rules.PreferredProtocol,
            UsenetDelayMinutes: rules.UsenetDelayMinutes,
            TorrentDelayMinutes: rules.TorrentDelayMinutes,
            MustContain: rules.MustContain,
            MustNotContain: rules.MustNotContain,
            PreferredTerms: [],
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch,
            // The profile's own acquisition answer always applies wherever the
            // library uses this profile: it cannot be disabled or scoped to a
            // subset of indexers the way a user-authored release profile can.
            Enabled: true,
            IndexerIds: []);

    private static bool SearchKindEnabled(IndexerItem indexer, string searchKind)
    {
        if (string.Equals(indexer.Protocol, "rss", StringComparison.OrdinalIgnoreCase) && !indexer.RssEnabled)
        {
            return false;
        }

        return searchKind == AcquisitionSearchKinds.Interactive
            ? indexer.InteractiveSearchEnabled
            : indexer.AutomaticSearchEnabled;
    }

    /// <summary>What a release name says its quality is, and Unknown when it says nothing (#610).</summary>
    private static string InferQuality(string releaseName) => ReleaseNameQuality.Read(releaseName);

    private static string BuildSummary(
        ReleaseDecision decision,
        IReadOnlyList<CustomFormatMatchResult> matchedFormats,
        ReleaseRankingBoostResult boost,
        ReleaseScoreComputation scoreComputation)
    {
        var parts = new List<string> { decision.Summary };
        if (matchedFormats.Count > 0 && decision.CustomFormatScore != 0)
        {
            var names = string.Join(", ", matchedFormats.Select(f => f.FormatName));
            parts.Add($"Matched {names} ({decision.CustomFormatScore.ToString("+#;-#;0", CultureInfo.InvariantCulture)}).");
        }

        if (scoreComputation.UsesModelSignal && boost.Applied)
        {
            parts.Add(boost.Explanation);
        }

        parts.Add(scoreComputation.Explanation);
        return string.Join(" ", parts);
    }

    private static double? ReadReleaseAgeHours(XElement item)
        => ReadPublishedUtc(item) is { } published
            ? Math.Max(0, (DateTimeOffset.UtcNow - published).TotalHours)
            : null;

    /// <summary>
    /// A candidate's info hash or NZB id/GUID, when the indexer's listing
    /// already names one (#802, #874): a torznab "infohash" attribute, the
    /// magnet the release links to, or — for a usenet indexer, which has no
    /// hash — the item's own GUID. Null for a torrent offered only as a
    /// .torrent link, since nothing names its hash before it is fetched.
    /// </summary>
    private static string? ReadHashOrItemId(IEnumerable<XElement> attrs, XElement item, string? protocol, string? downloadUrl)
    {
        var infoHash = attrs.FirstOrDefault(attr =>
                string.Equals(attr.Attribute("name")?.Value, "infohash", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;
        if (!string.IsNullOrWhiteSpace(infoHash))
        {
            return infoHash.Trim();
        }

        if (TrackerMagnet.InfoHashOf(downloadUrl) is { Length: >= 32 } magnetHash)
        {
            return magnetHash;
        }

        if (IndexerProtocols.Normalize(protocol) == IndexerProtocols.Newznab)
        {
            var guid = item.Element("guid")?.Value?.Trim();
            return string.IsNullOrWhiteSpace(guid) ? null : guid;
        }

        return null;
    }

    private static long? ReadLongAttr(IEnumerable<XElement> attrs, string name)
        => ReadLong(attrs.FirstOrDefault(attr => string.Equals(attr.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value);

    private static int? ReadIntAttr(IEnumerable<XElement> attrs, string name)
        => int.TryParse(attrs.FirstOrDefault(attr => string.Equals(attr.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? ReadLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
