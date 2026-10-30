using System.Collections.Concurrent;

namespace Deluno.Api.Monitoring;

/// <summary>
/// Whether each library's folder is there, checked off the request (#1081).
///
/// <para><c>Directory.Exists</c> on a share whose NAS is off does not answer
/// for many seconds, and the monitoring screen used to ask it once per library,
/// on the request thread, every time it was opened. A request now reads what the
/// last check found and never waits for a dead share: a folder that has not been
/// checked for thirty seconds is checked again in the background, and a check
/// that has not finished in five seconds counts as "not reachable", which is what
/// a folder that will not answer is.</para>
///
/// <para>The first look at a folder waits briefly (a quarter of a second) for its
/// check, so a folder that is plainly missing is reported at once and one that is
/// slow to answer is reported on the next look rather than holding the page.</para>
/// </summary>
public sealed class LibraryRootReachability(TimeProvider clock, Func<string, bool>? folderExists = null)
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstLookWait = TimeSpan.FromMilliseconds(250);

    // Only a test passes folderExists, to stand in for a share that never answers; the product asks the file system.
    private readonly ConcurrentDictionary<string, Probe> _probes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths that answered "not there" or did not answer in time, among those asked about.</summary>
    public async Task<IReadOnlySet<string>> UnreachableAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = clock.GetUtcNow();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = _probes.AddOrUpdate(
                path,
                _ => Start(path, now),
                (_, existing) => existing.Finished is { } finished && now - finished >= RecheckAfter
                    ? Start(path, now)
                    : existing);

            if (probe.Result.IsCompleted)
            {
                if (!probe.Result.Result) unreachable.Add(path);
                continue;
            }

            // Not answered yet: a brand-new probe is given a moment, an older one is
            // not waited for (reachable until it has gone five seconds unanswered).
            if (probe.StartedUtc == now)
            {
                var finished = await Task.WhenAny(probe.Result, Task.Delay(FirstLookWait, cancellationToken));
                if (finished == probe.Result && !probe.Result.Result) unreachable.Add(path);
            }
            else if (now - probe.StartedUtc >= GiveUpAfter)
            {
                unreachable.Add(path);
            }
        }

        return unreachable;
    }

    private Probe Start(string path, DateTimeOffset now)
    {
        var probe = new Probe(now);
        probe.Result = Task.Run(async () =>
        {
            try
            {
                var exists = Task.Run(() => (folderExists ?? Directory.Exists)(path));
                var answered = await Task.WhenAny(exists, Task.Delay(GiveUpAfter));
                return answered == exists && await exists;
            }
            catch
            {
                return false;
            }
            finally
            {
                probe.Finished = clock.GetUtcNow();
            }
        });
        return probe;
    }

    private sealed class Probe(DateTimeOffset startedUtc)
    {
        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public DateTimeOffset? Finished { get; set; }

        public Task<bool> Result { get; set; } = null!;
    }
}
