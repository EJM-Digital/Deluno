namespace Deluno.Contracts;

/// <summary>One reading of combined download-client throughput.</summary>
/// <param name="CapturedUtc">When the reading was taken.</param>
/// <param name="SpeedMbps">Combined speed across every client, in MB/s.</param>
/// <param name="ActiveCount">
/// Transfers moving at that moment. Kept alongside the speed so a flat line can
/// be told apart from a stalled queue: nothing downloading and nothing to
/// download look identical on speed alone.
/// </param>
public sealed record DownloadThroughputSample(
    DateTimeOffset CapturedUtc,
    double SpeedMbps,
    int ActiveCount);
