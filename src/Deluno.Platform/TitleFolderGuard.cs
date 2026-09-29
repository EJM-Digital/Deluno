using System.Text.RegularExpressions;

namespace Deluno.Platform;

/// <summary>
/// What the shared folder guard decided about one title (#1067). "Rename
/// folder" (a title page's preview and apply, and the bulk rename tools) may
/// only ever rename the folder that belongs to that one title. This is the
/// single place that decides which folder that is, used by the preview and by
/// the apply so they cannot disagree.
///
/// <para>It never renames anything. When it cannot prove a folder is the
/// title's own it returns no folder and a plain reason. The Code is one of
/// <c>no-files</c>, <c>flat-layout</c>, <c>library-folder</c>,
/// <c>outside-library</c>, <c>spread-out</c> or <c>shared-folder</c>.</para>
/// </summary>
public sealed record TitleFolderTarget(string? Folder, string Code, string Message)
{
    public bool IsSafe => Folder is not null;

    internal static TitleFolderTarget Safe(string folder) => new(folder, "ok", string.Empty);
    internal static TitleFolderTarget Refuse(string code, string message) => new(null, code, message);
}

public static class TitleFolderGuard
{
    private static readonly char[] Separators = ['/', '\\'];

    private static readonly Regex SeasonFolderName = new(
        @"^((season|series|saison|staffel|temporada|stagione)[ ._-]*\d+.*|s\d{1,3}|specials?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The folder that belongs to one movie: the folder its file sits in, but
    /// only when that folder is the movie's own. Refused when the file sits
    /// straight in a library folder (flat layout), when the folder is a drive
    /// root or holds a library, when it is outside every library, or when any
    /// other title has a file in it.
    /// </summary>
    public static async Task<TitleFolderTarget> ForMovieAsync(
        string? filePath,
        IReadOnlyList<string> libraryRoots,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        CancellationToken cancellationToken)
    {
        var directory = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return NoFiles();
        }

        var roots = Normalise(libraryRoots);
        var refusal = RefuseIfNotOwnFolder(directory, roots);
        return refusal ?? await CheckSharedAsync(directory, otherTitlesHaveFilesUnder, cancellationToken);
    }

    /// <summary>
    /// The folder that belongs to one show. With the library layout known it
    /// is the first folder under the library folder that all of the show's
    /// files sit in - so a show with a single season on disk still gets its
    /// show folder, never "Season 01". Without a matching library it is the
    /// folder all its files share, stepped up out of a season folder.
    /// </summary>
    public static async Task<TitleFolderTarget> ForSeriesAsync(
        IReadOnlyList<string> episodeFilePaths,
        IReadOnlyList<string> libraryRoots,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        CancellationToken cancellationToken)
    {
        var directories = episodeFilePaths
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .ToArray();
        if (episodeFilePaths.Count == 0 || directories.Length != episodeFilePaths.Count)
        {
            return NoFiles();
        }

        var roots = Normalise(libraryRoots);
        string showFolder;
        var root = roots.FirstOrDefault(candidate => IsSameOrUnder(directories[0], candidate));
        if (root is not null)
        {
            var first = Split(directories[0]).Skip(Split(root).Length).FirstOrDefault();
            if (first is null)
            {
                return FlatLayout(root);
            }

            showFolder = Path.Combine(root, first);
            if (!directories.All(directory => IsSameOrUnder(directory, showFolder)))
            {
                return TitleFolderTarget.Refuse(
                    "spread-out",
                    "This show's files are spread across more than one folder, so there is no single folder that is only this show's. Deluno leaves them alone.");
            }
        }
        else
        {
            var common = CommonFolder(directories);
            if (common is null)
            {
                return NoFiles();
            }

            showFolder = IsSeasonFolderName(Path.GetFileName(common)) && Path.GetDirectoryName(common) is { Length: > 0 } up
                ? up
                : common;
        }

        var refusal = RefuseIfNotOwnFolder(showFolder, roots);
        return refusal ?? await CheckSharedAsync(showFolder, otherTitlesHaveFilesUnder, cancellationToken);
    }

    /// <summary>True for "Season 01", "Season 1 (2019)", "S01", "Series 2", "Specials".</summary>
    public static bool IsSeasonFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SeasonFolderName.IsMatch(name.Trim());

    private static TitleFolderTarget NoFiles() => TitleFolderTarget.Refuse(
        "no-files",
        "There is no file on record for this title yet, so there is no folder to rename.");

    private static TitleFolderTarget FlatLayout(string libraryFolder) => TitleFolderTarget.Refuse(
        "flat-layout",
        $"This title's files sit straight in the library folder \"{Path.GetFileName(Trim(libraryFolder))}\", not in a folder of their own, so there is no folder to rename. Nothing would change.");

    private static TitleFolderTarget? RefuseIfNotOwnFolder(string folder, IReadOnlyList<string> roots)
    {
        var trimmed = Trim(folder);
        var parts = Split(trimmed);
        if (string.IsNullOrEmpty(Path.GetDirectoryName(trimmed)) || (parts.Length == 1 && parts[0].EndsWith(':')))
        {
            return TitleFolderTarget.Refuse(
                "library-folder",
                "That folder is the top of a drive, not a folder that belongs to this title, so Deluno leaves it alone.");
        }

        var equalRoot = roots.FirstOrDefault(root => Same(root, trimmed));
        if (equalRoot is not null)
        {
            return FlatLayout(equalRoot);
        }

        if (roots.Any(root => IsUnder(root, trimmed)))
        {
            return TitleFolderTarget.Refuse(
                "library-folder",
                "That folder holds one of your library folders, not just this title, so Deluno leaves it alone.");
        }

        if (roots.Count > 0 && !roots.Any(root => IsUnder(trimmed, root)))
        {
            return TitleFolderTarget.Refuse(
                "outside-library",
                "This title's files are not inside any of your library folders, so Deluno cannot tell which folder is its own and leaves it alone.");
        }

        return null;
    }

    private static async Task<TitleFolderTarget> CheckSharedAsync(
        string folder,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        CancellationToken cancellationToken)
    {
        return await otherTitlesHaveFilesUnder(folder, cancellationToken)
            ? TitleFolderTarget.Refuse(
                "shared-folder",
                $"The folder \"{Path.GetFileName(Trim(folder))}\" also holds files that belong to other titles, so it is not this title's own folder. Deluno leaves it alone.")
            : TitleFolderTarget.Safe(folder);
    }

    private static string[] Normalise(IReadOnlyList<string> roots) =>
        roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Trim).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Trim(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        return trimmed.Length == 0 ? path : trimmed;
    }

    private static bool Same(string a, string b) =>
        Split(a).SequenceEqual(Split(b), StringComparer.OrdinalIgnoreCase);

    private static bool IsSameOrUnder(string path, string folder) => Same(path, folder) || IsUnder(path, folder);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
    private static bool IsUnder(string path, string folder)
    {
        var p = Split(path);
        var f = Split(folder);
        if (f.Length == 0 || p.Length <= f.Length)
        {
            return false;
        }

        for (var i = 0; i < f.Length; i++)
        {
            if (!string.Equals(p[i], f[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Split(string path) => path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static string? CommonFolder(IReadOnlyList<string> directories)
    {
        var segmented = directories.Select(Split).ToArray();
        var length = segmented[0].Length;
        for (var i = 1; i < segmented.Length; i++)
        {
            var matched = 0;
            var max = Math.Min(length, segmented[i].Length);
            while (matched < max
                   && string.Equals(segmented[0][matched], segmented[i][matched], StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }

            length = matched;
        }

        if (length == 0)
        {
            return null;
        }

        var joined = string.Join(Path.DirectorySeparatorChar, segmented[0].Take(length));
        return Path.IsPathRooted(directories[0]) && !Path.IsPathRooted(joined)
            ? Path.DirectorySeparatorChar + joined
            : joined;
    }
}
