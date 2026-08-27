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
    string? Language,
    /// <summary>
    /// Covers foreign dialogue only, not the whole film.
    ///
    /// It has to be read, because a file whose only English track is forced has
    /// English subtitles for four lines of Elvish and nothing else. Counting it
    /// as English coverage would tell somebody they were done when they were
    /// not, and stop Deluno fetching the track they actually wanted.
    /// </summary>
    bool Forced = false,
    /// <summary>Includes sound effects and speaker labels (SDH).</summary>
    bool HearingImpaired = false,
    string? Title = null);
