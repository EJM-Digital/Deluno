using System.Text.RegularExpressions;
using Deluno.Quality;

namespace Deluno.Filesystem;

/// <summary>
/// Manual import scans a folder rather than asking for one file at a time
/// (#783). Radarr and Sonarr call this Interactive Import: point it at a
/// download's output folder, every video file inside gets a best-guess title
/// and year from its own name, and the person reviews or corrects each guess
/// before anything imports.
///
/// <para>This does not replace <see cref="IImportPipelineService"/> — every
/// row still goes through <see cref="IImportPipelineService.PreviewAsync"/>
/// for the destination path, quality and warnings a single-file manual import
/// already showed. The scan only adds what a folder needs on top: finding the
/// files, and guessing what each one is named after.</para>
/// </summary>
public static class ManualImportFolderScanner
{
    /// <summary>A 4-digit year between 1900 and 2099, the same window the release parser elsewhere in Deluno uses.</summary>
    private static readonly Regex YearPattern = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);

    /// <summary>A tracker or index site's advert in front of a release name: "www.UIndex.org - ", "[ site.com ] ".</summary>
    private static readonly Regex SitePrefixPattern = new(
        @"^\s*(?:\[\s*[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\s*\]\s*|(?:www\.)?[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.(?:org|com|net|info|to|cc|me|io|tv|ws|xyz|biz|eu|co|se|nl)\s+-\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Where a release name's title ends and its quality/source/codec noise
    /// begins — S01E02, 1080p, WEB-DL and the like. Everything from the first
    /// match onward is dropped rather than offered as part of a guessed title.
    /// </summary>
    private static readonly Regex TitleBoundaryPattern = new(
        @"(?<![A-Za-z0-9])(?:S\d{1,3}(?:E\d{1,3})?|Season[.\s_-]?\d{1,3}|\d{3,4}p|remux|blu[-\s]?ray|bdrip|brrip|web[-\s]?dl|webrip|web|hdtv|sdtv|dvdrip|proper|repack|hdr|uhd|4k|x264|x265|h[-\s]?26[45]|hevc|xvid)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every video file under <paramref name="folderPath"/>, in a stable
    /// order. Recurses so a season pack's per-episode files are found the
    /// same way a flat folder of movies is, capped so one enormous folder
    /// cannot turn a scan into a multi-minute directory walk.
    /// </summary>
    public static IReadOnlyList<string> FindVideoFiles(string folderPath, int take = 500)
        => Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(SupportedVideoExtensions.Contains)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToArray();

    /// <summary>
    /// A best-effort title and year from a release-style file name — the same
    /// question <c>ExistingLibraryImportService.ParseTitle</c> answers for an
    /// already-organised library folder, asked instead of a loose release name
    /// with no folder convention to lean on.
    /// </summary>
    public static (string? Title, int? Year) GuessTitleAndYear(string fileName)
    {
        // The site advert goes first: its dots would otherwise be read as an extension.
        var raw = Path.GetFileNameWithoutExtension(SitePrefixPattern.Replace(fileName ?? string.Empty, string.Empty));
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        var facts = MediaFileNameFacts.Parse(raw);
        var withoutGroup = string.IsNullOrWhiteSpace(facts.ReleaseGroup)
            ? raw
            : Regex.Replace(raw, $@"-{Regex.Escape(facts.ReleaseGroup)}$", string.Empty, RegexOptions.IgnoreCase);

        var normalized = withoutGroup.Replace('.', ' ').Replace('_', ' ').Trim();

        int? year = null;
        var yearMatches = YearPattern.Matches(normalized);
        if (yearMatches.Count > 0)
        {
            var match = yearMatches[^1];
            if (int.TryParse(match.Value, out var parsed))
            {
                year = parsed;
                normalized = normalized.Remove(match.Index, match.Length).Trim();
            }
        }

        // Whatever comes after the year (or after the release group, if there
        // was no year) is quality/codec noise a person did not type in.
        var boundary = TitleBoundaryPattern.Match(normalized);
        var title = (boundary.Success ? normalized[..boundary.Index] : normalized).Trim(' ', '.', '-', '_');

        return (string.IsNullOrWhiteSpace(title) ? null : title, year);
    }
}

public sealed record ImportFolderScanRequest(
    string FolderPath,
    string? MediaType,
    string? LibraryId,
    string? SeriesId);

public sealed record ImportFolderScanItem(
    string SourcePath,
    string FileName,
    string? GuessedTitle,
    int? GuessedYear,
    /// <summary>Whether Deluno could guess anything at all — an unmatched row still appears, flagged for a person to fill in rather than silently dropped (#783).</summary>
    bool Matched,
    ImportPreviewResponse Preview);

public sealed record ImportFolderScanResponse(
    string FolderPath,
    IReadOnlyList<ImportFolderScanItem> Items,
    IReadOnlyList<string> Issues);

public sealed record ImportBatchExecuteRequest(IReadOnlyList<ImportExecuteRequest> Items);

public sealed record ImportBatchExecuteResult(
    string SourcePath,
    bool Succeeded,
    string Message,
    ImportExecuteResponse? Response);

public sealed record ImportBatchExecuteResponse(
    int RequestedCount,
    int ImportedCount,
    IReadOnlyList<ImportBatchExecuteResult> Results);
