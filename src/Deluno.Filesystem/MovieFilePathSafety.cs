namespace Deluno.Filesystem;

/// <summary>
/// A requested path is only ever accepted when it resolves to somewhere
/// under the movie's own folder - never an arbitrary absolute path the
/// client happened to send, which is what would let one title's edit surface
/// delete or rename a file anywhere else on the host. Its own class, and
/// public, so this rule is tested directly rather than only through the
/// endpoints that lean on it (#782).
/// </summary>
public static class MovieFilePathSafety
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm"
    };

    private static readonly string[] ArtworkSuffixes =
        ["-poster", "-fanart", "-thumb", "-banner", "-clearlogo", "-logo", "-landscape", "-disc", "-backdrop"];

    public static bool TryResolveWithinFolder(string folder, string? requestedPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        string fullFolder;
        string fullRequested;
        try
        {
            fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullRequested = Path.GetFullPath(requestedPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!IsInside(fullFolder, fullRequested))
        {
            return false;
        }

        // A junction or symlink inside the folder must not lead out of it: the
        // place the path really lands has to be inside the folder's real place too.
        if (!IsInside(ResolveLinks(fullFolder), ResolveLinks(fullRequested)))
        {
            return false;
        }

        resolved = fullRequested;
        return true;
    }

    /// <summary>
    /// True when the tracked file sits directly in a library's root folder (a
    /// film filed flat, with no folder of its own), so its "folder" is shared
    /// with every other title and must never be treated as this film's own.
    /// </summary>
    public static bool IsFiledFlat(string primaryFilePath, IEnumerable<string> libraryRoots)
    {
        var folder = Path.GetDirectoryName(primaryFilePath);
        if (folder is null)
        {
            return false;
        }

        var full = Normalise(folder);
        return libraryRoots.Any(root => !string.IsNullOrWhiteSpace(root)
            && string.Equals(Normalise(root), full, PathComparison));
    }

    /// <summary>
    /// True when the film's folder is not its own: it is a library's root
    /// itself, or it holds another tracked primary file (another film or an
    /// episode, in any library), or the film's library no longer exists. Only
    /// the film's own named files count as its files there.
    /// </summary>
    public static bool IsSharedFolder(
        string primaryFilePath,
        IEnumerable<string> libraryRoots,
        IEnumerable<string> otherTrackedPrimaryPaths,
        bool libraryStillExists = true)
    {
        if (!libraryStillExists || IsFiledFlat(primaryFilePath, libraryRoots))
        {
            return true;
        }

        var ownFull = Path.GetFullPath(primaryFilePath);
        var folder = Path.GetDirectoryName(ownFull);
        if (folder is null)
        {
            return true;
        }

        var own = Normalise(folder);
        foreach (var other in otherTrackedPrimaryPaths)
        {
            if (string.IsNullOrWhiteSpace(other))
            {
                continue;
            }

            string otherFull;
            try { otherFull = Path.GetFullPath(other); }
            catch (ArgumentException) { continue; }

            if (string.Equals(otherFull, ownFull, PathComparison))
            {
                continue;
            }

            var otherFolder = Path.GetDirectoryName(otherFull);
            if (otherFolder is not null && string.Equals(Normalise(otherFolder), own, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For a film filed flat: only its tracked file and the files that carry its
    /// name (subtitles, nfo, artwork), in that same folder - never another
    /// title's files.
    /// </summary>
    public static bool BelongsToFlatFilm(string primaryFilePath, string candidatePath, IEnumerable<string>? alternateBaseNames = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(primaryFilePath));
        var candidateFolder = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
        if (folder is null || candidateFolder is null
            || !string.Equals(Normalise(folder), Normalise(candidateFolder), PathComparison))
        {
            return false;
        }

        var full = Path.GetFullPath(candidatePath);
        if (string.Equals(full, Path.GetFullPath(primaryFilePath), PathComparison))
        {
            return true;
        }

        var extension = Path.GetExtension(candidatePath);
        if (VideoExtensions.Contains(extension))
        {
            return false;
        }

        var bases =new List<string> { Path.GetFileNameWithoutExtension(primaryFilePath) };
        if (alternateBaseNames is not null)
        {
            bases.AddRange(alternateBaseNames.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        var name = Path.GetFileNameWithoutExtension(candidatePath);
        return bases.Any(baseName => NameBelongsTo(baseName, name));
    }

    private static bool NameBelongsTo(string baseName, string name)
    {
        if (name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[baseName.Length..];
        if (ArtworkSuffixes.Any(suffix => rest.Equals(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Only tags the subtitle reader itself understands (a language, a forced or
        // hearing-impaired flag): "Alien.Man.srt" is not "Alien"'s subtitle.
        return rest.StartsWith('.')
            && SubtitleInventoryService.AreAllTagsRecognised(rest[1..]);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalise(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsInside(string fullFolder, string fullRequested)
        => fullRequested.StartsWith(fullFolder + Path.DirectorySeparatorChar, PathComparison)
            || string.Equals(fullRequested, fullFolder, PathComparison);

    /// <summary>
    /// The path with every existing junction or symlink along it followed, so it
    /// names where the file really is. Parts that do not exist yet are kept as written.
    /// </summary>
    private static string ResolveLinks(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            var current = root;
            var parts = fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }

            return current;
        }
        catch (IOException)
        {
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return fullPath;
        }
    }
}
