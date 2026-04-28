namespace Deluno.Filesystem;

public interface IMediaProbeService
{
    Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken);
}

public sealed record MediaProbeInfo(
    string Status,
    string Tool,
    string? Message,
    double? DurationSeconds,
    string? Container,
    long? Bitrate,
    IReadOnlyList<MediaVideoStreamInfo> VideoStreams,
    IReadOnlyList<MediaAudioStreamInfo> AudioStreams,
    IReadOnlyList<MediaSubtitleStreamInfo> SubtitleStreams);

public sealed record MediaVideoStreamInfo(
    int Index,
    string? Codec,
    string? Profile,
    int? Width,
    int? Height,
    string? PixelFormat,
    double? FrameRate,
    long? Bitrate,
    string? Language);

public sealed record MediaAudioStreamInfo(
    int Index,
    string? Codec,
    string? Profile,
    int? Channels,
    string? ChannelLayout,
    int? SampleRate,
    long? Bitrate,
    string? Language);

public sealed record MediaSubtitleStreamInfo(
    int Index,
    string? Codec,
    string? Language);
