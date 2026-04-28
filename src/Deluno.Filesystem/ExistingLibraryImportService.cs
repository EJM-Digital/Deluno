using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
using Deluno.Series.Data;

namespace Deluno.Filesystem;

public sealed class ExistingLibraryImportService(
    IPlatformSettingsRepository platformSettingsRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository)
    : IExistingLibraryImportService
{
    private static readonly string[] VideoExtensions =
    [
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts"
    ];

    private static readonly Regex YearPattern = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"^(?<title>.+?)[\s._-]+S\d{1,2}E\d{1,2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeNumberPattern = new(@"S(?<season>\d{1,2})(?<episodes>(?:E\d{1,2})+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiEpisodeSegmentPattern = new(@"E(?<episode>\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanupTokensPattern = new(
        @"\b(remux|bluray|blu-ray|bdrip|web[-\s]?dl|webrip|web|hdtv|sdtv|dvd|x264|x265|hevc|av1|720p|1080p|2160p)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<ExistingLibraryImportResult?> ImportLibraryAsync(string libraryId, CancellationToken cancellationToken)
    {
        var library = (await platformSettingsRepository.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));

        if (library is null || string.IsNullOrWhiteSpace(library.RootPath) || !Directory.Exists(library.RootPath))
        {
            return null;
        }

        var discovered = library.MediaType == "movies"
            ? DiscoverMovies(library.RootPath)
            : DiscoverSeries(library.RootPath);

        var imported = 0;
        var skipped = 0;
        var samples = new List<string>();

        foreach (var item in discovered)
        {
            if (samples.Count < 8)
            {
                samples.Add(item.Title);
            }

            var wasImported = library.MediaType == "movies"
                ? await ImportMovieAsync(library, item, cancellationToken)
                : await ImportSeriesAsync(library, item, cancellationToken);

            if (wasImported)
            {
                imported++;
            }
            else
            {
                skipped++;
            }
        }

        return new ExistingLibraryImportResult(
            LibraryId: library.Id,
            LibraryName: library.Name,
            MediaType: library.MediaType,
            RootPath: library.RootPath,
            DiscoveredCount: discovered.Count,
            ImportedCount: imported,
            SkippedCount: skipped,
            SampleTitles: samples);
    }

    private async Task<bool> ImportMovieAsync(
        Deluno.Platform.Contracts.LibraryItem library,
        DetectedLibraryItem item,
        CancellationToken cancellationToken)
    {
        var decision = LibraryQualityDecider.Decide(
            mediaLabel: "movie",
            hasFile: true,
            currentQuality: item.DetectedQuality,
            cutoffQuality: library.CutoffQuality,
            upgradeUntilCutoff: library.UpgradeUntilCutoff,
            upgradeUnknownItems: library.UpgradeUnknownItems);

        return await movieCatalogRepository.ImportExistingAsync(
            library.Id,
            item.Title,
            item.Year,
            decision.WantedStatus,
            decision.WantedReason,
            decision.CurrentQuality,
            decision.TargetQuality,
            decision.QualityCutoffMet,
            false,
            cancellationToken);
    }

    private async Task<bool> ImportSeriesAsync(
        Deluno.Platform.Contracts.LibraryItem library,
        DetectedLibraryItem item,
        CancellationToken cancellationToken)
    {
        var decision = LibraryQualityDecider.Decide(
            mediaLabel: "TV show",
            hasFile: true,
            currentQuality: item.DetectedQuality,
            cutoffQuality: library.CutoffQuality,
            upgradeUntilCutoff: library.UpgradeUntilCutoff,
            upgradeUnknownItems: library.UpgradeUnknownItems);

        return await seriesCatalogRepository.ImportExistingAsync(
            library.Id,
            item.Title,
            item.Year,
            decision.WantedStatus,
            decision.WantedReason,
            decision.CurrentQuality,
            decision.TargetQuality,
            decision.QualityCutoffMet,
            false,
            item.Episodes,
            cancellationToken);
    }

    private static List<DetectedLibraryItem> DiscoverMovies(string rootPath)
    {
        var items = new List<DetectedLibraryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            if (!ContainsVideo(directory))
            {
                continue;
            }

            var rawName = Path.GetFileName(directory);
            var parsed = ParseTitle(rawName);
            var quality = LibraryQualityDecider.DetectQuality(rawName);
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed with { DetectedQuality = quality });
            }
        }

        foreach (var file in Directory.EnumerateFiles(rootPath))
        {
            if (!IsVideoFile(file))
            {
                continue;
            }

            var rawName = Path.GetFileNameWithoutExtension(file);
            var parsed = ParseTitle(rawName);
            var quality = LibraryQualityDecider.DetectQuality(rawName);
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed with { DetectedQuality = quality });
            }
        }

        return items;
    }

    private static List<DetectedLibraryItem> DiscoverSeries(string rootPath)
    {
        var items = new Dictionary<string, DetectedLibraryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            var videoFiles = EnumerateVideoFilesSafe(directory).ToArray();
            if (videoFiles.Length == 0)
            {
                continue;
            }

            var rawName = Path.GetFileName(directory);
            var parsed = ParseTitle(rawName);
            var quality = videoFiles
                .Select(file => LibraryQualityDecider.DetectQuality(Path.GetFileNameWithoutExtension(file)))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var episodes = DetectEpisodes(videoFiles);
            MergeSeriesCandidate(items, parsed with { DetectedQuality = quality, Episodes = episodes });
        }

        foreach (var file in Directory.EnumerateFiles(rootPath))
        {
            if (!IsVideoFile(file))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            var match = EpisodePattern.Match(name);
            if (!match.Success)
            {
                continue;
            }

            var parsed = ParseTitle(match.Groups["title"].Value);
            var quality = LibraryQualityDecider.DetectQuality(name);
            var episodes = DetectEpisodes([file]);
            MergeSeriesCandidate(items, parsed with { DetectedQuality = quality, Episodes = episodes });
        }

        return items.Values
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Year)
            .ToList();
    }

    private static bool ContainsVideo(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Any(IsVideoFile);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateVideoFilesSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(IsVideoFile);
        }
        catch
        {
            return [];
        }
    }

    private static DetectedLibraryItem ParseTitle(string raw)
    {
        var normalized = raw
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Trim();

        int? year = null;
        var yearMatches = YearPattern.Matches(normalized);
        var yearMatch = yearMatches.Count > 0 ? yearMatches[^1] : Match.Empty;
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear))
        {
            year = parsedYear;
            normalized = normalized.Remove(yearMatch.Index, yearMatch.Length).Trim();
        }

        normalized = CleanupTokensPattern.Replace(normalized, " ").Trim();
        normalized = Regex.Replace(normalized, @"\[[^\]]+\]|\([^\)]+\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\(\s*\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim('-', ' ');

        return new DetectedLibraryItem(string.IsNullOrWhiteSpace(normalized) ? raw.Trim() : normalized, year);
    }

    private static IReadOnlyList<Deluno.Series.Contracts.ImportedEpisodeItem> DetectEpisodes(IEnumerable<string> files)
    {
        return files
            .SelectMany(file => ExtractEpisodes(Path.GetFileNameWithoutExtension(file)))
            .Distinct()
            .OrderBy(item => item.SeasonNumber)
            .ThenBy(item => item.EpisodeNumber)
            .ToArray();
    }

    private static IEnumerable<Deluno.Series.Contracts.ImportedEpisodeItem> ExtractEpisodes(string fileName)
    {
        var match = EpisodeNumberPattern.Match(fileName);
        if (!match.Success)
        {
            yield break;
        }

        var seasonNumber = int.Parse(match.Groups["season"].Value);
        foreach (Match episodeMatch in MultiEpisodeSegmentPattern.Matches(match.Groups["episodes"].Value))
        {
            yield return new Deluno.Series.Contracts.ImportedEpisodeItem(
                SeasonNumber: seasonNumber,
                EpisodeNumber: int.Parse(episodeMatch.Groups["episode"].Value),
                HasFile: true);
        }
    }

    private static void MergeSeriesCandidate(
        Dictionary<string, DetectedLibraryItem> items,
        DetectedLibraryItem item)
    {
        var key = $"{item.Title}|{item.Year}";
        if (!items.TryGetValue(key, out var existing))
        {
            items[key] = item;
            return;
        }

        var detectedQuality = existing.DetectedQuality ?? item.DetectedQuality;
        var episodes = (existing.Episodes ?? [])
            .Concat(item.Episodes ?? [])
            .Distinct()
            .OrderBy(entry => entry.SeasonNumber)
            .ThenBy(entry => entry.EpisodeNumber)
            .ToArray();

        items[key] = existing with
        {
            DetectedQuality = detectedQuality,
            Episodes = episodes
        };
    }

    private sealed record DetectedLibraryItem(
        string Title,
        int? Year,
        string? DetectedQuality = null,
        IReadOnlyList<Deluno.Series.Contracts.ImportedEpisodeItem>? Episodes = null);
}
