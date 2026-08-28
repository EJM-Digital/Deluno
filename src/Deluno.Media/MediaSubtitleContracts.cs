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

/// <summary>
/// One file that is short of a language somebody asked for, with the words a
/// provider is searched with.
///
/// <para><see cref="MissingLanguages"/> is the gap, not the wish: the languages
/// the library asked for that this file does not already hold. Working it out
/// here rather than in the search means a provider is never asked for something
/// already on disk, which is the difference between a nightly cycle that costs
/// one request per real gap and one that costs a request per title forever.</para>
/// </summary>
public sealed record MediaSubtitleWantedItem(
    string MediaId,
    string FilePath,
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? ReleaseName,
    IReadOnlyList<string> MissingLanguages);

public interface IMediaSubtitleRepository
{
    /// <summary>
    /// The next files short of a wanted language.
    ///
    /// <para><b>Held means the same thing here as it does on the bar.</b> The
    /// predicate is <c>forced = 0</c> and the language matching, which is
    /// character for character what <c>CatalogueSubtitleRollup.Sql</c> counts —
    /// because a file the shelf paints green and the fetcher keeps searching for
    /// would be two answers to one question, and this codebase has paid for that
    /// shape more than once.</para>
    /// </summary>
    Task<IReadOnlyList<MediaSubtitleWantedItem>> ListWantedAsync(
        MediaKind kind,
        string libraryId,
        IReadOnlyList<string> languages,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a subtitle Deluno just wrote.
    ///
    /// <para>Separate from <c>RecordScanAsync</c> because a scan replaces
    /// everything it learnt about a file and a fetch adds one row to it. Handing
    /// a fetch to the scan path would delete every other language the file has
    /// every time one was downloaded.</para>
    /// </summary>
    Task RecordFetchedAsync(
        MediaKind kind,
        string mediaId,
        MediaSubtitleRow subtitle,
        CancellationToken cancellationToken);

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
