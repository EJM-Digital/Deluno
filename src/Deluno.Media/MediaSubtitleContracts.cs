using Deluno.Contracts;
namespace Deluno.Media;

/// <summary>
/// One subtitle Deluno holds for one title or episode.
///
/// The row is the same whether Deluno fetched it, found it beside the video or
/// read it out of the container — <see cref="Source"/> is the difference, and
/// it is the only difference. That is the point: the bar under a poster asks
/// "do you have English", and where English came from does not change the
/// answer.
/// </summary>
public sealed record MediaSubtitleRow(
    string Language,
    string Source,
    bool Forced,
    bool HearingImpaired,
    string? FilePath,
    int? StreamIndex,
    string? Codec,
    string? Provider);

/// <summary>
/// When a file was last read for subtitles, and what it looked like at the
/// time, so an unchanged file is not read again.
/// </summary>
public sealed record MediaSubtitleScan(
    string FilePath,
    long? FileSizeBytes,
    string ProbeStatus,
    int SubtitleCount,
    DateTimeOffset ScannedUtc);

/// <summary>A file whose subtitles Deluno has not read, or has read before it changed.</summary>
public sealed record MediaSubtitleScanCandidate(
    string MediaId,
    string FilePath,
    long? FileSizeBytes);

/// <summary>
/// How much of what a library asked for one title actually holds.
///
/// Two numbers because a library asks in one of two ways.
/// <see cref="Languages"/> is every wanted language present, counted per file,
/// and answers "English and Japanese". <see cref="Files"/> is how many files
/// have at least one of them, and answers "English, or Spanish if English
/// cannot be found". Counting the first and reporting it for the second would
/// let an episode holding both languages fill the bar for an episode holding
/// none.
/// </summary>
public sealed record MediaSubtitleHeld(int Languages, int Files);

public interface IMediaSubtitleRepository
{
    Task<IReadOnlyList<MediaSubtitleScanCandidate>> ListPendingScansAsync(
        MediaKind kind,
        string libraryId,
        int limit,
        CancellationToken cancellationToken);

    Task RecordScanAsync(
        MediaKind kind,
        string mediaId,
        MediaSubtitleScan scan,
        IReadOnlyList<MediaSubtitleRow> subtitles,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaSubtitleRow>> ListSubtitlesAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken);
}
