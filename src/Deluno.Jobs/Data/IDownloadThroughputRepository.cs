using Deluno.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The stored history behind the dashboard's download-speed chart.
///
/// Deliberately narrow: record a reading, read a window, drop what is older than
/// the retention. There is no update path, because a sample is a fact about a
/// moment and revising it would make the chart a fiction.
/// </summary>
public interface IDownloadThroughputRepository
{
    /// <summary>
    /// Store one reading. Re-recording the same instant overwrites rather than
    /// duplicating, so a restart that re-samples the same second cannot put two
    /// points on the chart at the same x.
    /// </summary>
    Task RecordSampleAsync(DownloadThroughputSample sample, CancellationToken cancellationToken);

    /// <summary>Readings from <paramref name="sinceUtc"/> onwards, oldest first.</summary>
    Task<IReadOnlyList<DownloadThroughputSample>> ListSamplesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>Drop everything older than the cutoff. Returns how many rows went.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}
