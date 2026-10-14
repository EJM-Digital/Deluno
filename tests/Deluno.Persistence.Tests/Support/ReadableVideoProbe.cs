using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Every file reads as a two-hour 1080p H.264 film, the way a real download would. The route tests write a few
/// bytes, not real video, and whether the installed ffprobe calls those corrupt is a fact about the machine and not
/// about the route; this takes the probe out of the question.
/// </summary>
internal sealed class ReadableVideoProbe : IMediaProbeService
{
    public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(new MediaProbeInfo(
            Status: "succeeded",
            Tool: "test",
            Message: null,
            DurationSeconds: 7200,
            Container: "matroska",
            Bitrate: 12_000_000,
            VideoStreams: [new MediaVideoStreamInfo(0, "h264", "High", 1920, 1080, "yuv420p", 23.976, 10_000_000, "eng")],
            AudioStreams: [],
            SubtitleStreams: []));
}
