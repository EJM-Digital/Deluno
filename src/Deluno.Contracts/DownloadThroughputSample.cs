namespace Deluno.Contracts;

/// <summary>One reading of combined download-client throughput.</summary>
/// <param name="CapturedUtc">When the reading was taken.</param>
/// <param name="SpeedMbps">Combined speed across every client, in MB/s.</param>
/// <param name="ActiveCount">
/// Transfers moving at that moment. Kept alongside the speed so a flat line can
/// be told apart from a stalled queue: nothing downloading and nothing to
/// download look identical on speed alone.
/// </param>
/// <param name="UploadMbps">
/// Combined upload across every client, in MB/s. Sharing is a first-class part
/// of what Deluno does now (#288), so "am I actually seeding?" is a question
/// this series has to be able to answer. Zero on readings taken before it was
/// measured — which is the truth, not a gap.
/// </param>
public sealed record DownloadThroughputSample(
    DateTimeOffset CapturedUtc,
    double SpeedMbps,
    int ActiveCount,
    double UploadMbps = 0);
