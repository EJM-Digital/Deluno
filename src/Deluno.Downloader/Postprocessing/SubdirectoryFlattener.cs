namespace Deluno.Downloader.Postprocessing;

/// <summary>
/// Moves all files into the working dir root, removing the nested
/// directory layout that some releases ship with (e.g.
/// <c>Release.Name/CD1/file.mkv</c> → <c>Release.Name/file.mkv</c>).
///
/// Disabled by default for torrents (the layout often IS the release —
/// e.g. Bluray remuxes ship the full BDMV structure). The orchestrator
/// decides whether to apply this step per category.
///
/// Filename collisions are resolved by appending a numeric suffix
/// (<c>file.mkv</c> → <c>file (2).mkv</c>) so we never silently overwrite.
/// </summary>
public sealed class SubdirectoryFlattener : IPostProcessor
{
    public Task<IReadOnlyList<string>> ProcessAsync(
        string workingDir,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(workingDir);
        var moved = new List<string>(files.Count);

        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var absSrc = Path.GetFullPath(src);
            var dir = Path.GetDirectoryName(absSrc);
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            {
                moved.Add(absSrc);
                continue;
            }

            var fileName = Path.GetFileName(absSrc);
            var dst = ResolveUniquePath(Path.Combine(root, fileName));
            try
            {
                File.Move(absSrc, dst);
                moved.Add(dst);
            }
            catch
            {
                // If the move fails (cross-volume, permission, etc.), keep
                // the original path in the returned set so the importer
                // still sees the file.
                moved.Add(absSrc);
            }
        }

        // Best-effort: remove empty subdirectories left behind.
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
            }
        }
        catch { /* best-effort */ }

        return Task.FromResult<IReadOnlyList<string>>(moved);
    }

    private static string ResolveUniquePath(string candidate)
    {
        if (!File.Exists(candidate)) return candidate;

        var dir = Path.GetDirectoryName(candidate)!;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var n = 2; n < 1000; n++)
        {
            var attempt = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(attempt)) return attempt;
        }
        // Give up after 1000 collisions — vanishingly unlikely. Append a
        // GUID so the move at least succeeds.
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }
}
