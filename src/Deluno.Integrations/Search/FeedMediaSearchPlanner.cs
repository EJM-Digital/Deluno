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
    IGuidePackageStore? guidePackageStore = null)
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
        bool currentFilePresent = false)
    {
        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var normalizedSearchKind = AcquisitionSearchKinds.Normalize(searchKind);
        var applicableProfiles = releaseProfileRepository is null
            ? []
            : await releaseProfileRepository.ListApplicableAsync(tagNames, cancellationToken);
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
                     currentPreferenceEvaluation, preferencePlan, currentFilePresent, token);
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
            // Rejected candidates remain visible for explanation, but cannot
            // become the automatic winner. Needs-review/held candidates are
            // likewise visible after safe candidates. Once that stage is
            // selected, the typed comparator alone owns the plan order.
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
        CancellationToken cancellationToken)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        var queryText = BuildQueryText(title, year, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber);
        var queryKind = string.Equals(indexer.Protocol, "rss", StringComparison.OrdinalIgnoreCase)
            ? "rss"
            : "search";

        if (!Uri.TryCreate(BuildSearchUrl(indexer, title, year, mediaType, seasonNumber, episodeNumber, numberingScheme, absoluteNumber, airDate, sceneSeasonNumber, sceneEpisodeNumber), UriKind.Absolute, out var uri))
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
        // the host rather than the indexer id, because two indexer entries can
        // point at the same tracker and it is the tracker that does the
        // counting.
        var waited = await outboundRequestThrottle.TryAcquireAsync(
            uri.Host,
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
                "indexer.search",
                FailureThreshold: 2),
            async token =>
            {
                try
                {
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
                        currentFilePresent);
                    return new IndexerSearchOutcome(
                        parsed.Candidates,
                        parsed.CandidatesTruncatedByIndexer,
                        Failed: false,
                        CircuitOpen: false,
                        Outcome: parsed.Candidates.Count == 0 ? "no_results" : "matched");
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
        int requestedLimit = IndexerResultLimit)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";
        XNamespace newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";
        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(targetQuality) ?? "WEB 1080p";
        var results = new List<MediaSearchCandidate>();

        var feedItems = document.Descendants("item").ToArray();
        foreach (var item in feedItems)
        {
            var releaseName = WebUtility.HtmlDecode(item.Element("title")?.Value?.Trim() ?? string.Empty);
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
                ReleaseProfiles: releaseProfiles,
                IndexerProtocol: indexer.Protocol,
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
                 CurrentFilePresent: currentFilePresent), qualityModel);

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
                PreferenceComparison: decision.PreferenceComparison));
        }

        return new ParsedCandidates(results, feedItems.Length == requestedLimit);
    }

    private readonly record struct ParsedCandidates(
        IReadOnlyList<MediaSearchCandidate> Candidates,
        bool CandidatesTruncatedByIndexer);

    private static int TypedCandidateStageRank(MediaSearchCandidate candidate)
        => candidate.DecisionStatus switch
        {
            "rejected" => 2,
            "delayed" or "held" or "risky" => 1,
            _ => 0
        };

    private static int LegacyCandidateStatusRank(MediaSearchCandidate candidate)
        => candidate.DecisionStatus switch
        {
            "preferred" => 0,
            "acceptable" or "eligible" => 1,
            "equivalent" => 2,
            "delayed" or "held" or "risky" => 3,
            "rejected" => 4,
            _ => 3
        };

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
        var builder = new UriBuilder(EnsureApiEndpoint(indexer.BaseUrl));
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

        query["cat"] = string.IsNullOrWhiteSpace(indexer.Categories)
            ? isTv ? "5000" : "2000"
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

    private static string EnsureApiEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (trimmed.Contains("?", StringComparison.Ordinal) ||
            trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/api?", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.TrimEnd('/') + "/api";
    }

    private static string SanitizeAddress(string value)
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

    private static string InferQuality(string releaseName)
    {
        var normalized = releaseName.ToLowerInvariant();
        var source = normalized.Contains("remux") ? "Remux" :
            normalized.Contains("bluray") || normalized.Contains("blu-ray") ? "Bluray" :
            normalized.Contains("web") ? "WEB" :
            normalized.Contains("hdtv") ? "HDTV" :
            "WEB";
        var resolution = normalized.Contains("2160") || normalized.Contains("4k") ? "2160p" :
            normalized.Contains("720") ? "720p" :
            "1080p";
        return $"{source} {resolution}";
    }

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
    {
        var raw = item.Element("pubDate")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - published.ToUniversalTime();
        return Math.Max(0, age.TotalHours);
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
