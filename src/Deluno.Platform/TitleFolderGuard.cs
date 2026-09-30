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
/// <c>outside-library</c>, <c>spread-out</c>, <c>bucket-folder</c>,
/// <c>shared-folder</c> or <c>unreadable-folder</c>.</para>
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

    /// <summary>A folder that groups many titles: a letter, a range, a collection, a whole category.</summary>
    private static readonly Regex BucketFolderName = new(
        @"^(.|#|[a-z0-9]\s*-\s*[a-z0-9]|\d+\s*-\s*\d+|.*\bcollection|anime|cartoons?|documentar(y|ies)|kids|movies|films?|tv|tv shows?|tv series|shows?|series)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts",
        ".webm", ".flv", ".ogm", ".ogv", ".divx", ".iso", ".vob", ".3gp"
    };

    /// <summary>
    /// The folder that belongs to one movie: the folder its file sits in, but
    /// only when that folder is the movie's own. Refused when the file sits
    /// straight in a library folder (flat layout), when the folder is a drive
    /// root or holds a library, when it is outside every library, when it is
    /// a group folder (a letter, a collection), or when anything else is in
    /// it: another title on record (movie or show), or any other video file
    /// on the disk.
    /// </summary>
    public static async Task<TitleFolderTarget> ForMovieAsync(
        string? filePath,
        IReadOnlyList<string> libraryRoots,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        CancellationToken cancellationToken,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        var directory = string.IsNullOrWhiteSpace(filePath) ? null : DirectoryOf(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return NoFiles();
        }

        var roots = Normalise(libraryRoots);
        var refusal = RefuseIfNotOwnFolder(directory, roots);
        return refusal ?? await CheckSharedAsync(directory, [filePath!], otherTitlesHaveFilesUnder, enumerateFiles, cancellationToken);
    }

    /// <summary>
    /// The folder that belongs to one show: the folder all of the show's
    /// files share, stepped up out of season folders ("Season 1", "S01",
    /// "Specials") and never above the library folder. It comes from the
    /// show's own files, never from "the first folder under the library", so
    /// <c>/tv/Anime/Bleach/Season 1</c> gives <c>Bleach</c>, not <c>Anime</c>.
    /// The same checks as for a movie then apply.
    /// </summary>
    public static async Task<TitleFolderTarget> ForSeriesAsync(
        IReadOnlyList<string> episodeFilePaths,
        IReadOnlyList<string> libraryRoots,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        CancellationToken cancellationToken,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        var directories = episodeFilePaths.Select(DirectoryOf).ToArray();
        if (episodeFilePaths.Count == 0 || directories.Any(string.IsNullOrEmpty))
        {
            return NoFiles();
        }

        var roots = Normalise(libraryRoots);
        var showFolder = CommonFolder(directories);
        if (showFolder is null)
        {
            return NoFiles();
        }

        while (IsSeasonFolderName(LastSegment(showFolder)) && DirectoryOf(showFolder) is { Length: > 0 } up)
        {
            showFolder = up;
        }

        var root = roots.FirstOrDefault(candidate => IsSameOrUnder(directories[0], candidate));
        if (root is not null && Same(showFolder, root))
        {
            return directories.All(directory => Same(directory, root)
                                                || (Same(DirectoryOf(directory), root) && IsSeasonFolderName(LastSegment(directory))))
                ? FlatLayout(root)
                : TitleFolderTarget.Refuse(
                    "spread-out",
                    "This show's files are spread across more than one folder, so there is no single folder that is only this show's. Deluno leaves them alone.");
        }

        var refusal = RefuseIfNotOwnFolder(showFolder, roots);
        return refusal ?? await CheckSharedAsync(showFolder, episodeFilePaths, otherTitlesHaveFilesUnder, enumerateFiles, cancellationToken);
    }

    /// <summary>True for "Season 01", "Season 1 (2019)", "S01", "Series 2", "Specials".</summary>
    public static bool IsSeasonFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SeasonFolderName.IsMatch(name.Trim());

    private static TitleFolderTarget NoFiles() => TitleFolderTarget.Refuse(
        "no-files",
        "There is no file on record for this title yet, so there is no folder to rename.");

    private static TitleFolderTarget FlatLayout(string libraryFolder) => TitleFolderTarget.Refuse(
        "flat-layout",
        $"This title's files sit straight in the library folder \"{LastSegment(Trim(libraryFolder))}\", not in a folder of their own, so there is no folder to rename. Nothing would change.");

    private static TitleFolderTarget? RefuseIfNotOwnFolder(string folder, IReadOnlyList<string> roots)
    {
        var trimmed = Trim(folder);
        var parts = Split(trimmed);
        if (string.IsNullOrEmpty(DirectoryOf(trimmed)) || (parts.Length == 1 && parts[0].EndsWith(':')))
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

        if (!roots.Any(root => IsUnder(trimmed, root)))
        {
            return TitleFolderTarget.Refuse(
                "outside-library",
                "This title's files are not inside any of your library folders, so Deluno cannot tell which folder is its own and leaves it alone.");
        }

        if (BucketFolderName.IsMatch(LastSegment(trimmed).Trim()))
        {
            return TitleFolderTarget.Refuse(
                "bucket-folder",
                $"The folder \"{LastSegment(trimmed)}\" looks like a group folder (a letter, a collection or a whole category), not a folder that belongs to one title, so Deluno leaves it alone.");
        }

        return null;
    }

    private static async Task<TitleFolderTarget> CheckSharedAsync(
        string folder,
        IReadOnlyList<string> ownFiles,
        Func<string, CancellationToken, Task<bool>> otherTitlesHaveFilesUnder,
        Func<string, IEnumerable<string>>? enumerateFiles,
        CancellationToken cancellationToken)
    {
        var name = LastSegment(Trim(folder));
        if (await otherTitlesHaveFilesUnder(folder, cancellationToken))
        {
            return TitleFolderTarget.Refuse(
                "shared-folder",
                $"The folder \"{name}\" also holds files that belong to other titles, so it is not this title's own folder. Deluno leaves it alone.");
        }

        // The database only knows tracked titles. Look at the disk too: any
        // video file in the folder, or under it, that is not this title's own
        // belongs to something else, tracked or not.
        var own = new HashSet<string>(ownFiles.Select(Canonical), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in (enumerateFiles ?? EnumerateFilesOnDisk)(folder))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file)) && !own.Contains(Canonical(file)))
                {
                    return TitleFolderTarget.Refuse(
                        "shared-folder",
                        $"The folder \"{name}\" also holds video files that are not this title's (for example \"{LastSegment(file)}\"), so it is not this title's own folder. Deluno leaves it alone.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TitleFolderTarget.Refuse(
                "unreadable-folder",
                $"Deluno could not look inside the folder \"{name}\" to check that it holds only this title, so it leaves it alone.");
        }

        return TitleFolderTarget.Safe(folder);
    }

    private static IEnumerable<string> EnumerateFilesOnDisk(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray()
            : [];

    private static string Canonical(string path) => string.Join('/', Split(path));

    private static string LastSegment(string path) => Split(path).LastOrDefault() ?? string.Empty;

    /// <summary>The parent folder of a path, understanding both separators on every platform (UNC included).</summary>
    private static string DirectoryOf(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var index = trimmed.LastIndexOfAny(Separators);
        return index switch
        {
            < 0 => string.Empty,
            0 => trimmed[..1],
            _ => trimmed[..index]
        };
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

    /// <summary>
    /// The deepest folder all the directories share, cut from the first
    /// directory's own spelling so the result is exactly what is on disk
    /// (case, UNC prefix and separators kept).
    /// </summary>
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

        var path = directories[0];
        var index = 0;
        var seen = 0;
        while (index < path.Length && Array.IndexOf(Separators, path[index]) >= 0)
        {
            index++;
        }

        while (index < path.Length)
        {
            if (Array.IndexOf(Separators, path[index]) >= 0 && Array.IndexOf(Separators, path[index - 1]) < 0)
            {
                seen++;
                if (seen == length)
                {
                    return path[..index];
                }
            }

            index++;
        }

        return path.TrimEnd(Separators);
    }
}
