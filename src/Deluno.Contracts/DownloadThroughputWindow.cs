namespace Deluno.Contracts;

/// <summary>
/// A stored window of throughput readings.
/// </summary>
/// <param name="Hours">The window actually served, after clamping.</param>
/// <param name="Samples">Readings oldest first. Empty until the sampler has run.</param>
public sealed record DownloadThroughputWindow(
    int Hours,
    IReadOnlyList<DownloadThroughputSample> Samples);
