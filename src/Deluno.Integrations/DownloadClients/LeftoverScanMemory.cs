namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// What the last leftover-download scan found, kept in memory so the Dashboard
/// can print "N leftovers" without walking a single folder. The scan itself
/// (<see cref="IOrphanedDownloadService.ListAsync"/>) reads every download
/// folder, so it runs when the schedule or a person asks; this only remembers
/// its answer. Before the first scan there is no answer, and it says so
/// rather than claiming none.
/// </summary>
public sealed class LeftoverScanMemory
{
    private readonly object _gate = new();
    private LeftoverScanReading? _last;

    public LeftoverScanReading? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>
    /// One leftover left the count (the person kept it or removed it): the bell and the Dashboard read this, so they stop counting it
    /// at once instead of at the next scan. The scan time stays: it is when the folders were last walked.
    /// </summary>
    public void Forget(long bytes)
    {
        lock (_gate)
        {
            if (_last is { } last)
            {
                _last = last with { Count = Math.Max(0, last.Count - 1), Bytes = Math.Max(0, last.Bytes - Math.Max(0, bytes)) };
            }
        }
    }

    public void Record(int count, long bytes, DateTimeOffset scannedUtc)
    {
        lock (_gate)
        {
            _last = new LeftoverScanReading(count, bytes, scannedUtc);
        }
    }
}

public sealed record LeftoverScanReading(int Count, long Bytes, DateTimeOffset ScannedUtc);
