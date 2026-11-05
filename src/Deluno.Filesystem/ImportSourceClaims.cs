using System.Collections.Concurrent;

namespace Deluno.Filesystem;

/// <summary>
/// One import per file (#1498). The normal import path and restart recovery can both reach for the same source file in the
/// moments after a restart; whichever takes the claim first is the import, and the other leaves the file to it and tries
/// again later. The claim is held for the whole import, and what a crash leaves behind (the staging file and its restart
/// marker, which now carries the whole request) is the durable half: recovery finishes it with the same title and grab,
/// and a second import of the same title lands on the same destination, where the identical file is linked, not copied.
/// </summary>
public static class ImportSourceClaims
{
    private static readonly ConcurrentDictionary<string, byte> Held = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The claim on a source file, or null when another import already holds it.</summary>
    public static IDisposable? TryClaim(string sourcePath)
        => Held.TryAdd(Key(sourcePath), 0) ? new Lease(Key(sourcePath)) : null;

    public static bool IsClaimed(string sourcePath) => Held.ContainsKey(Key(sourcePath));

    private static string Key(string sourcePath) => Path.GetFullPath(sourcePath);

    private sealed class Lease(string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Held.TryRemove(key, out _);
            }
        }
    }
}
