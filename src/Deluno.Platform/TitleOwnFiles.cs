namespace Deluno.Platform;

/// <summary>One thing that leaves a library for a title: a single file, or a folder that holds nothing else.</summary>
public sealed record TitleOwnUnit(string Path, bool IsDirectory);

/// <summary>
/// The one rule for what leaves a library when a title's files are removed (#1317). Only the title's own tracked
/// files go, with the sidecars that sit beside each one (subtitles, artwork and notes named after the video). A
/// folder goes as a whole only when every file left in it is one of those or title artwork, and no other video is
/// in it. A shared folder (two titles in one) or a group folder (<c>A/Dune (2021)</c> beside <c>A/Arrival (2016)</c>)
/// therefore keeps the other titles' media where it is. Every path that takes a title's files out of a library
/// (film and series removal, retention, collection clean-up, an external re-download) goes through this.
/// </summary>
public static class TitleOwnFiles
{
    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static readonly StringComparer PathComparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".iso", ".vob", ".webm", ".flv", ".ogm", ".divx", ".img", ".bdmv"
    };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".sup", ".smi", ".nfo", ".jpg", ".jpeg", ".png", ".tbn", ".webp", ".gif", ".xml", ".txt", ".json"
    };

    /// <summary>
    /// Works out what to move for <paramref name="removedFiles"/> (full paths of the title's own files, all inside
    /// <paramref name="libraryRoot"/>). Paths that do not exist are skipped.
    /// </summary>
    public static IReadOnlyList<TitleOwnUnit> Plan(string libraryRoot, IEnumerable<string> removedFiles)
    {
        var root = TrimmedRoot(libraryRoot);
        var removed = removedFiles
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(PathComparer)
            .ToArray();
        var removedSet = new HashSet<string>(removed, PathComparer);
        var units = new Dictionary<string, TitleOwnUnit>(PathComparer);
        var coveredFolders = new List<string>();

        foreach (var file in removed.OrderBy(path => path.Length))
        {
            if (coveredFolders.Any(folder => IsUnder(folder, file)))
            {
                continue;
            }

            var folder = HighestOwnFolder(root, file, removedSet);
            if (folder is not null)
            {
                coveredFolders.Add(folder);
                units[folder] = new TitleOwnUnit(folder, true);
                continue;
            }

            units[file] = new TitleOwnUnit(file, false);
            foreach (var sidecar in SidecarsOf(file))
            {
                units.TryAdd(sidecar, new TitleOwnUnit(sidecar, false));
            }
        }

        // A file or sidecar claimed first may sit inside a folder that was then taken whole.
        return units.Values
            .Where(unit => unit.IsDirectory || !coveredFolders.Any(folder => IsUnder(folder, unit.Path)))
            .OrderBy(unit => unit.Path, PathComparer)
            .ToArray();
    }

    /// <summary>The folders above these units, deepest first, never the library root itself: tidy each only if it is empty.</summary>
    public static IReadOnlyList<string> AncestorsToTidy(string libraryRoot, IEnumerable<TitleOwnUnit> units)
    {
        var root = TrimmedRoot(libraryRoot);
        var folders = new HashSet<string>(PathComparer);
        foreach (var unit in units)
        {
            var current = Path.GetDirectoryName(unit.Path);
            while (!string.IsNullOrEmpty(current) && IsUnder(root, current))
            {
                folders.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }

        return folders.OrderByDescending(folder => folder.Length).ToArray();
    }

    private static string TrimmedRoot(string libraryRoot)
        => Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? HighestOwnFolder(string root, string file, HashSet<string> removedSet)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var candidate = root;
        foreach (var segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
            if (HoldsOnlyTitleFiles(candidate, removedSet))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HoldsOnlyTitleFiles(string folder, HashSet<string> removedSet)
    {
        var found = new List<string>();
        try
        {
            if (!Walk(folder, found))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var path in found)
        {
            if (removedSet.Contains(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            if (VideoExtensions.Contains(extension) || !SidecarExtensions.Contains(extension))
            {
                return false;
            }
        }

        return found.Any(removedSet.Contains);
    }

    /// <summary>Lists every file under a folder; false when a link is in it, since what it points at is not this title's to take.</summary>
    private static bool Walk(string folder, List<string> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!Walk(entry, files))
                {
                    return false;
                }
            }
            else
            {
                files.Add(Path.GetFullPath(entry));
            }
        }

        return true;
    }

    private static IEnumerable<string> SidecarsOf(string videoPath)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.Length == 0)
        {
            yield break;
        }

        var otherStems = Directory.EnumerateFiles(folder)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToArray();

        foreach (var candidate in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(candidate);
            var extension = Path.GetExtension(name);
            if (!SidecarExtensions.Contains(extension) || VideoExtensions.Contains(extension))
            {
                continue;
            }

            var nameStem = Path.GetFileNameWithoutExtension(name);
            // Dune.srt, Dune.en.srt, Dune-poster.jpg: the video's name, then nothing, a dot or a dash.
            // Another video in the folder with a longer matching name (Dune.Part.Two.mkv) owns it instead.
            var matches = Belongs(nameStem, stem)
                && !otherStems.Any(other => other.Length > stem.Length && Belongs(nameStem, other));
            if (matches)
            {
                yield return Path.GetFullPath(candidate);
            }
        }
    }

    private static bool Belongs(string nameStem, string videoStem)
        => nameStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase)
           || (nameStem.Length > videoStem.Length
               && nameStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase)
               && (nameStem[videoStem.Length] == '.' || nameStem[videoStem.Length] == '-'));

    private static bool IsUnder(string folder, string path)
        => path.Length > folder.Length
           && path.StartsWith(folder, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
           && (path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar);
}
