namespace Deluno.Contracts;

/// <summary>
/// One reading of how hard the machine is working (#272).
///
/// Deluno could say how full a drive was and nothing about how busy it was, so
/// when an import crawled the dashboard could not say whether the cause was
/// Deluno, the disk, or something else on the box entirely. That is a question
/// the arr suite also fails to answer.
/// </summary>
/// <param name="CpuPercent">
/// Deluno's own share of the machine, already divided by processor count, so
/// 100 means every core saturated by Deluno rather than one core of sixteen.
/// </param>
/// <param name="MemoryBytes">Deluno's working set.</param>
/// <param name="TotalMemoryBytes">
/// What the machine has, so the working set can be read as a proportion.
/// Null where Deluno could not ask.
/// </param>
/// <param name="ProcessReadBytesPerSecond">
/// What Deluno itself is doing to the disk. Answers "is this Deluno?" and works
/// on any Windows locale without a performance counter.
/// </param>
/// <param name="DiskBusyPercent">
/// How loaded the library drive is, including everything else on the machine.
/// Null when the reading is unavailable — a missing series, never a failure:
/// the whole-disk figure comes from the volume itself and can be refused.
/// </param>
public sealed record MachineTelemetrySample(
    DateTimeOffset CapturedUtc,
    double CpuPercent,
    long MemoryBytes,
    long? TotalMemoryBytes,
    long ProcessReadBytesPerSecond,
    long ProcessWriteBytesPerSecond,
    double? DiskBusyPercent,
    long? DiskReadBytesPerSecond,
    long? DiskWriteBytesPerSecond)
{
    /// <summary>Working set as a share of the machine, or null when the total is unknown.</summary>
    public double? MemoryPercent => TotalMemoryBytes is > 0
        ? Math.Round(MemoryBytes / (double)TotalMemoryBytes.Value * 100, 1)
        : null;
}

/// <summary>
/// A stored window of machine readings.
/// </summary>
/// <param name="Hours">The window actually served, after clamping.</param>
/// <param name="Samples">Readings oldest first. Empty until the sampler has run.</param>
public sealed record MachineTelemetryWindow(
    int Hours,
    IReadOnlyList<MachineTelemetrySample> Samples);
