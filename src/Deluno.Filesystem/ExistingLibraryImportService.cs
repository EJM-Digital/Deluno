using Deluno.Contracts;
using System.Text.RegularExpressions;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
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
                ? await movieCatalogRepository.ImportExistingAsync(library.Id, item.Title, item.Year, cancellationToken)
                : await seriesCatalogRepository.ImportExistingAsync(library.Id, item.Title, item.Year, cancellationToken);

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

            var parsed = ParseTitle(Path.GetFileName(directory));
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed);
            }
        }

        foreach (var file in Directory.EnumerateFiles(rootPath))
        {
            if (!IsVideoFile(file))
            {
                continue;
            }

            var parsed = ParseTitle(Path.GetFileNameWithoutExtension(file));
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed);
            }
        }

        return items;
    }

    private static List<DetectedLibraryItem> DiscoverSeries(string rootPath)
    {
        var items = new List<DetectedLibraryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            if (!ContainsVideo(directory))
            {
                continue;
            }

            var parsed = ParseTitle(Path.GetFileName(directory));
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed);
            }
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
            var key = $"{parsed.Title}|{parsed.Year}";
            if (seen.Add(key))
            {
                items.Add(parsed);
            }
        }

        return items;
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

    private static DetectedLibraryItem ParseTitle(string raw)
    {
        var normalized = raw
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Trim();

        int? year = null;
        var yearMatch = YearPattern.Match(normalized);
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear))
        {
            year = parsedYear;
            normalized = normalized.Replace(yearMatch.Value, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }

        normalized = Regex.Replace(normalized, @"\[[^\]]+\]|\([^\)]+\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\(\s*\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim('-', ' ');

        return new DetectedLibraryItem(string.IsNullOrWhiteSpace(normalized) ? raw.Trim() : normalized, year);
    }

    private sealed record DetectedLibraryItem(string Title, int? Year);
}
