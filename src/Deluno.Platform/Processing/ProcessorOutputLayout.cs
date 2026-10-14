using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Processing;

/// <summary>
/// Where a processor puts the cleaned copy of a source (#1262). Weir (the recorded replies in
/// <c>tests/fixtures/recorded/weir</c>, and its source) writes the source's path relative to the folder it watches,
/// re-rooted under the workflow's output folder:
///
/// <list type="bullet">
/// <item>a single file at the watched folder's root lands flat, as <c>&lt;output&gt;\Film.mkv</c>;</item>
/// <item>a file inside a release folder keeps that folder, as <c>&lt;output&gt;\&lt;release folder&gt;\file.mkv</c>;</item>
/// <item>a folder hand-off lands as the release folder;</item>
/// <item>a name collision renames only the file, as <c>stem (n).ext</c>.</item>
/// </list>
///
/// <para>Every path here is a path <em>inside</em> the library's own processor output folder; nothing outside it is
/// ever produced. The watched folder is the one the processor's own library-folders list names (translated through the
/// path mapping), or the library's downloads folder when it names none. Without any watched folder the relative path is
/// unknown, and callers fall back to the older generic layout: a folder named after the source, with or without its
/// extension, which the drop-folder processor contract documents.</para>
/// </summary>
public static class ProcessorOutputLayout
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".wmv", ".mpg", ".mpeg", ".iso"];

    /// <summary>
    /// The folder the processor watches for a library, in Deluno's own path space: the entry the processor's folder list
    /// holds for the library (translated through the path mapping), otherwise the library's downloads folder.
    /// </summary>
    public static string? WatchedFolderFor(
        string libraryId,
        string? downloadsPath,
        IEnumerable<ProcessorLibraryFolder> folders,
        IReadOnlyList<ProcessorPathMappingItem> mappings)
    {
        var reported = ProcessorLibraryFolder.ForLibrary(folders, libraryId)?.WatchedFolder;
        if (!string.IsNullOrWhiteSpace(reported))
        {
            var translated = mappings.Count > 0 ? ProcessorPathMappingTranslator.ToDeluno(reported, mappings) : reported;
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        return string.IsNullOrWhiteSpace(downloadsPath) ? null : downloadsPath;
    }

    /// <summary>
    /// The source's path below the watched folder, or null when the source is not strictly inside it (or climbs out of
    /// it). Both separators count, whatever this machine calls its own: the paths were written on other machines.
    /// </summary>
    public static IReadOnlyList<string>? RelativeSegments(string? sourcePath, string? watchedFolder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(watchedFolder))
        {
            return null;
        }

        var source = Split(sourcePath);
        var watched = Split(watchedFolder);
        if (watched.Length == 0 || source.Length <= watched.Length)
        {
            return null;
        }

        for (var index = 0; index < watched.Length; index++)
        {
            if (!string.Equals(source[index], watched[index], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var relative = source[watched.Length..];
        return relative.Any(segment => segment is "." or "..") ? null : relative;
    }

    /// <summary>
    /// The files and folders the layout puts in the output folder for this source that exist right now: the path itself
    /// and, for a video file, the same folder's copies renamed for a name collision. Each is inside the output folder.
    /// </summary>
    public static IReadOnlyList<string> ExistingOutputPaths(string outputRoot, string sourcePath, string? watchedFolder)
    {
        var relative = RelativeSegments(sourcePath, watchedFolder);
        if (relative is null || string.IsNullOrWhiteSpace(outputRoot))
        {
            return [];
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
            var target = Path.GetFullPath(Path.Combine([root, .. relative]));
            if (!IsInside(root, target))
            {
                return [];
            }

            var found = new List<string>();
            if (File.Exists(target) || Directory.Exists(target))
            {
                found.Add(target);
            }

            var name = relative[^1];
            var parent = Path.GetDirectoryName(target);
            if (IsVideoName(name) && parent is not null && Directory.Exists(parent) && IsInside(root, parent + Path.DirectorySeparatorChar))
            {
                var renamed = CollisionPattern(name);
                found.AddRange(Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => renamed.IsMatch(Path.GetFileName(file))));
            }

            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The names the layout can give the output's top-level item (directly in the output folder): the first folder or
    /// the file below the watched folder. Empty when the relative path is unknown.
    /// </summary>
    public static IReadOnlyList<string> TopLevelNames(string sourcePath, string? watchedFolder)
        => RelativeSegments(sourcePath, watchedFolder) is { Count: > 0 } relative ? [relative[0]] : [];

    /// <summary>
    /// Whether a top-level item of the output folder is this source's: the older generic names (the source's own name,
    /// with and without its extension), the first folder or the file the layout names, or a single file renamed for a
    /// name collision (<c>stem (n).ext</c>).
    /// </summary>
    public static bool ClaimsTopLevelName(string sourcePath, string? watchedFolder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        var leaf = Split(sourcePath).LastOrDefault();
        if (string.IsNullOrEmpty(leaf) || leaf is "." or "..")
        {
            return false;
        }

        if (string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Path.GetFileNameWithoutExtension(leaf), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = RelativeSegments(sourcePath, watchedFolder);
        if (relative is null)
        {
            return false;
        }

        return string.Equals(name, relative[0], StringComparison.OrdinalIgnoreCase)
            || (relative.Count == 1 && IsVideoName(leaf) && CollisionPattern(leaf).IsMatch(name));
    }

    /// <summary>Whether a name is the file renamed for a collision: <c>stem (n).ext</c> for a video named <c>stem.ext</c>.</summary>
    public static bool IsCollisionRename(string name, string originalFileName)
        => IsVideoName(originalFileName) && CollisionPattern(originalFileName).IsMatch(name);

    /// <summary>Whether a path segment (a file name without its extension) is a collision rename of the stem: <c>stem (n)</c>.</summary>
    public static bool IsCollisionRenamedStem(string? segment, string? stem)
        => !string.IsNullOrEmpty(stem) && segment is not null
           && Regex.IsMatch(segment, "^" + Regex.Escape(stem) + @" \(\d+\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex CollisionPattern(string fileName)
        => new(
            "^" + Regex.Escape(Path.GetFileNameWithoutExtension(fileName)) + @" \(\d+\)" + Regex.Escape(Path.GetExtension(fileName)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsVideoName(string fileName)
        => VideoExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static string[] Split(string path)
        => path.Trim().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsInside(string root, string path) => Deluno.Contracts.SafePath.IsInside(root, path);
}
