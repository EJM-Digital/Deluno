namespace Deluno.Filesystem;

/// <summary>
/// Resolves what Checkpoint A (#917) actually has to open: a single file
/// path, unchanged, or -- for a folder-shaped download (a film in its own
/// folder, or a season pack) -- every video file inside it worth checking.
///
/// <para>A film in a folder is the common case, not an edge case: most
/// torrent releases arrive as "Title (Year) 1080p/movie.mkv" alongside an
/// NFO, a sample, and sometimes a subs folder. Before this, Checkpoint A's
/// own <c>File.Exists</c> guard silently passed over every one of them --
/// see the caller in WorkPlanner.</para>
/// </summary>
public static class DownloadIntegrityFileResolver
{
    /// <summary>
    /// A filename fragment that marks a sample clip rather than the release
    /// itself, matched the same conservative way release-quality checks
    /// elsewhere in Deluno already do: a whole path segment, not a substring
    /// that could appear inside a real title (a film called "Sample" would
    /// never be released as one file called exactly that).
    /// </summary>
    private static bool LooksLikeSample(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("sample", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sample-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sample.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-sample", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file(s) Checkpoint A should open for <paramref name="sourcePath"/>.
    ///
    /// <list type="bullet">
    /// <item>A file path resolves to itself, exactly as before this existed.</item>
    /// <item>A folder with exactly one recognised, non-sample video file
    /// resolves to that file -- the ordinary "film in a folder" case.</item>
    /// <item>A folder with more than one resolves to every one of them, largest
    /// first: a season pack, where each file is its own episode and each one
    /// is worth its own check.</item>
    /// <item>A folder with no recognised video file yet (still unpacking, or
    /// only a sample has landed so far) resolves to nothing -- not a failure,
    /// just nothing ready to check this pass.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Resolve(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            return [sourcePath];
        }

        if (!Directory.Exists(sourcePath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(SupportedVideoExtensions.Contains)
                .Where(path => !LooksLikeSample(path))
                .Select(path => (Path: path, Size: TryGetLength(path)))
                .OrderByDescending(entry => entry.Size)
                .Select(entry => entry.Path)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A share that dropped mid-listing, or a permission problem —
            // nothing here to check this pass; tried again next time.
            return [];
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
