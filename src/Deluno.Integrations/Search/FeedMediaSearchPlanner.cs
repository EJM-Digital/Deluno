using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Integrations.Search;

public sealed class FeedMediaSearchPlanner(
    IPlatformSettingsRepository platformRepository,
    IConnectionsRepository connectionsRepository,
    IHttpClientFactory httpClientFactory,
    IIntegrationResiliencePolicy resiliencePolicy,
    IQualityModelService qualityModelService,
    IReleaseRankingModelService rankingModelService,
    IOutboundRequestThrottle outboundRequestThrottle,
    ILogger<FeedMediaSearchPlanner> logger)
    : IMediaSearchPlanner
{
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
        CancellationToken cancellationToken = default)
    {
        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var sourceIndexers = sources
            .Join(
                indexers.Where(item => item.IsEnabled && CoversMediaType(item, mediaType)),
                source => source.IndexerId,
                indexer => indexer.Id,
                (source, indexer) => (source, indexer),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(pair => pair.source.Priority)
            .ThenBy(pair => pair.indexer.Priority)
            .ToArray();

        var settings = await platformRepository.GetAsync(cancellationToken);
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
        var searchResults = new IReadOnlyList<MediaSearchCandidate>[sourceIndexers.Length];
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
                    customFormats, neverGrabPatterns, scoringMode, seasonNumber, episodeNumber, token);
            });

        // Indexed writes above, flattened in order here, so results stay in
        // source-then-indexer priority order regardless of who answered first.
        var liveCandidates = new List<MediaSearchCandidate>();
        foreach (var result in searchResults)
        {
            liveCandidates.AddRange(result);
        }

        if (sourceIndexers.Length == 0)
        {
            return new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: $"No enabled {mediaType} indexers are linked to this library policy. Add or enable an indexer before searching for {title}.");
        }

        if (liveCandidates.Count == 0)
        {
            return new MediaSearchPlan(
                BestCandidate: null,
                Candidates: [],
                Summary: $"No live feed results were returned for {title}. Check indexer health, categories, credentials, and network access.");
        }

        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(targetQuality) ?? "WEB 1080p";
        var ordered = liveCandidates
            .OrderBy(item => item.DecisionStatus == "rejected")
            .ThenByDescending(item => item.MeetsCutoff)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Seeders ?? 0)
            .ThenBy(item => item.IndexerName)
            .ToArray();
        var best = ordered.FirstOrDefault();

        return new MediaSearchPlan(
            BestCandidate: best,
            Candidates: ordered,
            Summary: best is null
                ? $"No usable feed release was found for {title}."
                : $"Best feed candidate is {best.ReleaseName} from {best.IndexerName} targeting {normalizedTarget}.");
    }

    private async Task<IReadOnlyList<MediaSearchCandidate>> TrySearchIndexerAsync(
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
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(BuildSearchUrl(indexer, title, year, mediaType, seasonNumber, episodeNumber), UriKind.Absolute, out var uri))
        {
            return [];
        }

        // Paced before the request, not after the indexer complains. Keyed on
        // the host rather than the indexer id, because two indexer entries can
        // point at the same tracker and it is the tracker that does the
        // counting.
        var waited = await outboundRequestThrottle.TryAcquireAsync(
            uri.Host,
            OutboundRate.PerIndexerDefault,
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

            return [];
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

                        return Array.Empty<MediaSearchCandidate>();
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    var document = await XDocument.LoadAsync(stream, LoadOptions.None, token);
                    var qualityModel = await qualityModelService.GetAsync(token);
                    return ParseCandidates(document, indexer, source, currentQuality, targetQuality, customFormats, neverGrabPatterns, qualityModel, scoringMode, rankingModelService);
                }
                catch (Exception exception) when (exception is not HttpRequestException and not TaskCanceledException and not IOException)
                {
                    return Array.Empty<MediaSearchCandidate>();
                }
            },
            _ => IntegrationResilienceOutcome.Success,
            cancellationToken);

        return result.Value ?? [];
    }

    private static IReadOnlyList<MediaSearchCandidate> ParseCandidates(
        XDocument document,
        IndexerItem indexer,
        LibrarySourceLinkItem source,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<CustomFormatItem>? customFormats,
        IReadOnlyList<string> neverGrabPatterns,
        QualityModelSnapshot qualityModel,
        string scoringMode,
        IReleaseRankingModelService rankingModelService)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";
        XNamespace newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";
        var normalizedTarget = LibraryQualityDecider.NormalizeQuality(targetQuality) ?? "WEB 1080p";
        var results = new List<MediaSearchCandidate>();

        foreach (var item in document.Descendants("item").Take(30))
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
            var quality = InferQuality(releaseName);
            var customFormatBonus = CustomFormatMatcher.Evaluate(releaseName, customFormats, out var matchedFormats);
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
                neverGrabPatterns), qualityModel);

            var boost = rankingModelService.Score(new ReleaseRankingFeatures(
                Seeders: seeders,
                SizeBytes: size,
                QualityDelta: decision.QualityDelta,
                CustomFormatScore: decision.CustomFormatScore,
                SourcePriorityScore: Math.Max(0, 200 - source.Priority),
                EstimatedBitrateMbps: decision.EstimatedBitrateMbps,
                ReleaseAgeHours: releaseAgeHours), hardBlocked: decision.Status == "rejected");

            var scoreComputation = ReleaseScoringModePolicy.Compute(decision.Score, boost, scoringMode);
            var finalScore = scoreComputation.FinalScore;
            var reasons = scoreComputation.UsesModelSignal
                ? decision.Reasons.Concat([boost.Explanation, scoreComputation.Explanation]).ToArray()
                : decision.Reasons.Concat([scoreComputation.Explanation]).ToArray();
            var summary = BuildSummary(decision, matchedFormats, boost, scoreComputation);

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
                MatchedCustomFormats: matchedFormats));
        }

        return results;
    }

    private static string BuildSearchUrl(IndexerItem indexer, string title, int? year, string mediaType, int? seasonNumber, int? episodeNumber)
    {
        var builder = new UriBuilder(EnsureApiEndpoint(indexer.BaseUrl));
        var query = ParseQuery(builder.Query);

        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        if (isTv && seasonNumber is not null)
        {
            query["t"] = "tvsearch";
            query["q"] = title;
            query["season"] = seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (episodeNumber is not null)
            {
                query["ep"] = episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else
        {
            query["t"] = "search";
            query["q"] = year is null ? title : $"{title} {year}";
        }

        query["cat"] = string.IsNullOrWhiteSpace(indexer.Categories)
            ? isTv ? "5000" : "2000"
            : indexer.Categories.Replace(" ", string.Empty, StringComparison.Ordinal);

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
