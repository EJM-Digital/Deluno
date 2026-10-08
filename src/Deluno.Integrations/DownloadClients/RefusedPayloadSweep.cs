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
        TimeSpan minAge)
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
        var found = new List<RefusedPayload>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            Walk(root.TrimEnd('\\', '/'), 1);
        }

        return found.OrderBy(payload => payload.Path, StringComparer.OrdinalIgnoreCase).ToArray();

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
                if (!byName.TryGetValue(name.Trim(), out var release))
                {
                    if (isDirectory && depth < MaxDepth)
                    {
                        Walk(entry.FullName, depth + 1);
                    }

                    continue;
                }

                if (!seen.Add(key)
                    || now - new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero) < minAge
                    || listed.Any(path => Related(key, path))
                    || claimed.Any(path => Related(key, path))
                    || libraryKeys.Any(root => key.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, root, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var executable = CarriesExecutable(entry);
                if (!executable && !payloadShouldBeDeleted(release))
                {
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
