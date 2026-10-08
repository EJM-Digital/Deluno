using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>One leftover payload of a refused release, and the refusal it belongs to.</summary>
public sealed record RefusedPayload(string Path, bool IsDirectory, long SizeBytes, BlockedRelease Release, bool CarriesExecutable);

/// <summary>
/// The rule that finds what a refused release left on disk after its client entry was removed without its files (#1167).
///
/// <para>A refused release's entry could be removed from the client while the payload stayed (an older build, or a
/// client that kept it). Nothing listed it any more, so no client, no scan and no sweep ever reached it. This finds
/// those: an entry under one of Deluno's own category or incomplete folders whose name is the name of a release Deluno
/// sent and then refused, that no client lists, no import or hand-off is using and no library holds.</para>
///
/// <para>Ownership is the whole safety: only paths under the given roots, only a name Deluno itself sent, never a path a
/// client still lists and never anything inside a library.</para>
/// </summary>
public static class RefusedPayloadSweep
{
    /// <summary>How deep under a root a payload is looked for: the root, a client's folder, its category folder.</summary>
    public const int MaxDepth = 3;

    public static IReadOnlyList<RefusedPayload> Find(
        IEnumerable<string> roots,
        IReadOnlyCollection<BlockedRelease> refused,
        IReadOnlyCollection<string> listedByClients,
        IReadOnlyCollection<string> libraryRoots,
        IReadOnlyCollection<string> claimedPaths,
        Func<BlockedRelease, bool> payloadShouldBeDeleted,
        DateTimeOffset now,
        TimeSpan minAge,
        Action<string, string>? onSkipped = null)
    {
        var byName = new Dictionary<string, BlockedRelease>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in refused.Where(release => !string.IsNullOrWhiteSpace(release.ReleaseName)
            && !string.IsNullOrWhiteSpace(release.DownloadClientId)
            && !string.IsNullOrWhiteSpace(release.TorrentHashOrItemId)))
        {
            byName.TryAdd(release.ReleaseName.Trim(), release);
        }

        if (byName.Count == 0)
        {
            return [];
        }

        var listed = listedByClients.Where(path => !string.IsNullOrWhiteSpace(path)).Select(OrphanedDownloads.Key).ToArray();
        var libraryKeys = libraryRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Select(OrphanedDownloads.Key).ToArray();
        var claimed = claimedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(OrphanedDownloads.Key).ToArray();
        var rootKeys = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(OrphanedDownloads.Key).ToArray();
        var found = new List<RefusedPayload>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            Walk(root.TrimEnd('\\', '/'), 1);
        }

        return found.OrderBy(payload => payload.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        // A path above a sweep root, or a root itself, is where a client saves things (its save or category
        // folder): it says nothing about what lies under it, so it never shields an entry from the sweep.
        bool IsRootOrAbove(string path)
            => rootKeys.Any(root => string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

        // A client lists a torrent by its own content path: the file for a single-file torrent, its folder otherwise.
        // A folder above a loose file is a save folder, not the torrent, so it does not shield that file.
        bool ProtectedByClientPath(string entryKey, string path, bool entryIsDirectory)
        {
            if (!Related(entryKey, path))
            {
                return false;
            }

            if (!entryKey.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsRootOrAbove(path))
            {
                return false;
            }

            var parent = entryKey[..entryKey.LastIndexOf('/')];
            return entryIsDirectory || !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase);
        }

        void Walk(string folder, int depth)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                var isDirectory = entry is DirectoryInfo;
                var name = isDirectory ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name);
                var key = OrphanedDownloads.Key(entry.FullName);
                // A release name can end like an extension (".x264-GRP"), so a file is also tried by its whole name.
                if (!byName.TryGetValue(name.Trim(), out var release)
                    && (isDirectory || !byName.TryGetValue(entry.Name.Trim(), out release)))
                {
                    if (isDirectory && depth < MaxDepth)
                    {
                        Walk(entry.FullName, depth + 1);
                    }

                    continue;
                }

                string? skippedBecause = null;
                if (!seen.Add(key))
                {
                    skippedBecause = "it was already looked at through another root";
                }
                else if (now - new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero) < minAge)
                {
                    skippedBecause = "it is younger than the grace period, so it may still be written";
                }
                else if (listed.FirstOrDefault(path => ProtectedByClientPath(key, path, isDirectory)) is { } listedPath)
                {
                    skippedBecause = $"a download client lists {listedPath}";
                }
                else if (claimed.FirstOrDefault(path => Related(key, path) && !IsRootOrAbove(path)) is { } claimedPath)
                {
                    skippedBecause = $"an import or hand-off is using {claimedPath}";
                }
                else if (libraryKeys.Any(root => key.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, root, StringComparison.OrdinalIgnoreCase)))
                {
                    skippedBecause = "it is inside a library";
                }

                if (skippedBecause is not null)
                {
                    onSkipped?.Invoke(entry.FullName, skippedBecause);
                    continue;
                }

                var executable = CarriesExecutable(entry);
                if (!executable && !payloadShouldBeDeleted(release))
                {
                    onSkipped?.Invoke(entry.FullName, $"the refusal ({release.ReasonCode}) does not justify deleting a file that holds no executable");
                    continue;
                }

                found.Add(new RefusedPayload(entry.FullName, isDirectory, SizeOf(entry), release, executable));
            }
        }
    }

    /// <summary>Whether a file, or any file inside a folder, is an executable or script that has no place in a video release.</summary>
    public static bool CarriesExecutable(FileSystemInfo entry)
    {
        try
        {
            IEnumerable<string> names = entry is DirectoryInfo directory
                ? directory.EnumerateFiles("*", SearchOption.AllDirectories).Select(file => file.Name)
                : [entry.Name];
            return names.Any(name => ContentBlocklistDefaults.BuiltInPatterns.Any(pattern => OrphanedDownloads.MatchesGlob(pattern, name)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SizeOf(FileSystemInfo entry)
    {
        try
        {
            return entry is DirectoryInfo directory
                ? directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                : entry is FileInfo file ? file.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool Related(string entry, string other)
        => string.Equals(entry, other, StringComparison.OrdinalIgnoreCase)
            || other.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase);
}
