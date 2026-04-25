using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;

namespace Deluno.Integrations.Search;

public sealed class FeedMediaSearchPlanner(
    IPlatformSettingsRepository platformRepository,
    IHttpClientFactory httpClientFactory)
    : IMediaSearchPlanner
{
    public async Task<MediaSearchPlan> BuildPlanAsync(
        string title,
        int? year,
        string mediaType,
        string? currentQuality,
        string? targetQuality,
        IReadOnlyList<LibrarySourceLinkItem> sources,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        CancellationToken cancellationToken = default)
    {
        var indexers = await platformRepository.ListIndexersAsync(cancellationToken);
        var sourceIndexers = sources
            .Join(
                indexers.Where(item => item.IsEnabled && CoversMediaType(item, mediaType)),
                source => source.IndexerId,
                indexer => indexer.Id,
                (source, indexer) => (source, indexer),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(pair => pair.source.Priority)
            .ThenBy(pair => pair.indexer.Priority)
            .Take(4)
            .ToArray();

        var liveCandidates = new List<MediaSearchCandidate>();
        foreach (var (source, indexer) in sourceIndexers)
        {
            var candidates = await TrySearchIndexerAsync(indexer, source, title, year, mediaType, targetQuality, customFormats, cancellationToken);
            liveCandidates.AddRange(candidates);
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
            .OrderByDescending(item => item.MeetsCutoff)
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
        string? targetQuality,
        IReadOnlyList<CustomFormatItem>? customFormats,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(BuildSearchUrl(indexer, title, year, mediaType), UriKind.Absolute, out var uri))
        {
            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient("indexers");
            http.Timeout = TimeSpan.FromSeconds(12);
            using var response = await http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            return ParseCandidates(document, indexer, source, targetQuality, customFormats);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<MediaSearchCandidate> ParseCandidates(
        XDocument document,
        IndexerItem indexer,
        LibrarySourceLinkItem source,
        string? targetQuality,
        IReadOnlyList<CustomFormatItem>? customFormats)
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
            var quality = InferQuality(releaseName);
            var meetsCutoff = QualityRank(quality) >= QualityRank(normalizedTarget);
            var score = 900 - source.Priority + QualityRank(quality) * 20 + Math.Min(seeders ?? 0, 200);
            var customFormatBonus = EvaluateCustomFormats(releaseName, customFormats, out var matchedFormats);
            score += customFormatBonus;

            results.Add(new MediaSearchCandidate(
                ReleaseName: releaseName,
                IndexerId: indexer.Id,
                IndexerName: indexer.Name,
                Quality: quality,
                Score: score,
                MeetsCutoff: meetsCutoff,
                Summary: BuildSummary(
                    meetsCutoff
                        ? "Feed result meets or exceeds the cutoff."
                        : "Feed result is below cutoff but may still be useful.",
                    matchedFormats,
                    customFormatBonus,
                    seeders),
                DownloadUrl: downloadUrl,
                SizeBytes: size,
                Seeders: seeders));
        }

        return results;
    }

    private static string BuildSearchUrl(IndexerItem indexer, string title, int? year, string mediaType)
    {
        var builder = new UriBuilder(EnsureApiEndpoint(indexer.BaseUrl));
        var query = ParseQuery(builder.Query);
        query["t"] = "search";
        query["q"] = year is null ? title : $"{title} {year}";
        query["cat"] = string.IsNullOrWhiteSpace(indexer.Categories)
            ? mediaType == "tv" ? "5000" : "2000"
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

    private static int QualityRank(string? quality)
    {
        var normalized = LibraryQualityDecider.NormalizeQuality(quality) ?? "WEB 1080p";
        return normalized switch
        {
            "WEB 720p" => 1,
            "HDTV 1080p" => 2,
            "WEB 1080p" => 3,
            "Bluray 1080p" => 4,
            "Remux 1080p" => 5,
            "WEB 2160p" => 6,
            "Bluray 2160p" => 7,
            "Remux 2160p" => 8,
            _ => 3
        };
    }

    private static int EvaluateCustomFormats(string releaseName, IReadOnlyList<CustomFormatItem>? customFormats, out string[] matchedFormats)
    {
        if (customFormats is null || customFormats.Count == 0)
        {
            matchedFormats = [];
            return 0;
        }

        var matches = new List<string>();
        var bonus = 0;
        foreach (var format in customFormats)
        {
            var token = ExtractMatchToken(format.Conditions);
            if (string.IsNullOrWhiteSpace(token) || !releaseName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(format.Name);
            bonus += format.Score;
        }

        matchedFormats = matches.ToArray();
        return bonus;
    }

    private static string ExtractMatchToken(string? conditions)
    {
        if (string.IsNullOrWhiteSpace(conditions)) return string.Empty;
        var separatorIndex = conditions.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex < conditions.Length - 1
            ? conditions[(separatorIndex + 1)..].Trim()
            : conditions.Trim();
    }

    private static string BuildSummary(string baseSummary, IReadOnlyList<string> matchedFormats, int bonus, int? seeders)
    {
        var parts = new List<string> { baseSummary };
        if (seeders is not null) parts.Add($"{seeders.Value.ToString(CultureInfo.InvariantCulture)} seeders reported.");
        if (matchedFormats.Count > 0 && bonus != 0) parts.Add($"Matched {string.Join(", ", matchedFormats)} (+{bonus}).");
        return string.Join(" ", parts);
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
