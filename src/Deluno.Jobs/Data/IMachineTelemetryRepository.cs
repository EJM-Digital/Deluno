using Deluno.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// The stored history of how hard the machine has been working (#272).
///
/// Same narrow shape as the download-throughput store next door: record a
/// reading, read a window, drop what is past retention. A sample is a fact
/// about a moment, so there is no update path — revising one would make the
/// chart a fiction.
/// </summary>
public interface IMachineTelemetryRepository
{
    Task RecordSampleAsync(MachineTelemetrySample sample, CancellationToken cancellationToken);

    /// <summary>Readings from <paramref name="sinceUtc"/> onwards, oldest first.</summary>
    Task<IReadOnlyList<MachineTelemetrySample>> ListSamplesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The newest reading, or null before the sampler has run. This is what the
    /// dashboard shows as "now": the sampler is the only thing that probes, so
    /// two readers cannot disturb each other's rate baselines.
    /// </summary>
    Task<MachineTelemetrySample?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Drop everything older than the cutoff. Returns how many rows went.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}
