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
    string? Provider,
    /// <summary>
    /// Which rung of the match ladder this subtitle sits on — 0 any release,
    /// 1 same source, 2 made for this file. See <c>SubtitleMatch</c>.
    ///
    /// <para>Zero is the honest default for everything Deluno did not fetch
    /// itself. A file found beside the video or a track read out of the
    /// container is a subtitle nobody checked the release of, and saying
    /// otherwise would invent the fact this column exists to stop inventing.
    /// </para>
    /// </summary>
    int MatchRung = 0,
    /// <summary>
    /// <see cref="Deluno.Contracts.SubtitleBlockKeys"/> for the exact candidate
    /// Deluno fetched, so a later "reject this one" (#895, #897, #841) can
    /// blocklist the file it actually downloaded rather than every candidate
    /// that provider ever offers for the language.
    ///
    /// <para>Null for anything Deluno did not fetch itself — embedded, or
    /// found beside the video — because there is no provider candidate to
    /// name, and for a subtitle fetched before this column existed, which is
    /// an honest gap rather than one worth inventing history for.</para>
    /// </summary>
    string? SourceKey = null,
    /// <summary>
    /// What the last timing sync attempt found — <c>"moved"</c>,
    /// <c>"already_in_time"</c>, or <c>"not_checked"</c> when it could not
    /// decide either way (#922). Null for a subtitle no sync has run against
    /// yet.
    /// </summary>
    string? TimingStatus = null,
    /// <summary>The one-sentence reason behind <see cref="TimingStatus"/>, for the title's own page.</summary>
    string? TimingReason = null);

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
    long? FileSizeBytes,
    /// <summary>
    /// Whether the video itself is different from the one last read — new,
    /// renamed, resized, or never successfully probed.
    ///
    /// <para><b>False means only the folder needs looking at again.</b> The
    /// tracks inside a container cannot change while the container does not, so
    /// re-running ffprobe over an unchanged file is a process per file for an
    /// answer already recorded. What <i>can</i> change under a still video is
    /// the subtitles beside it — somebody deletes one, or drops one in — and
    /// finding that out is one directory listing.</para>
    ///
    /// <para>This is the whole reason Deluno needs no equivalent of Bazarr's
    /// <i>"use cached embedded subtitles parser results"</i>. That setting
    /// exists because Bazarr re-parses everything on every pass and had to give
    /// people a way to stop it; Deluno already records what the video was, so
    /// it can tell the two halves apart without being told.</para>
    /// </summary>
    bool VideoChanged);

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
public sealed record MediaSubtitleHeld(
    int Languages,
    int Files,
    /// <summary>
    /// How many of <see cref="Languages"/> are at the cutoff — made for the file
    /// they sit beside, so Deluno has stopped looking.
    ///
    /// <para>The difference between this and <see cref="Languages"/> is the
    /// green on the bar: held, watchable, and still being improved.</para>
    /// </summary>
    int Settled = 0);

/// <summary>
/// One file with subtitle work outstanding, with the words a provider is
/// searched with.
///
/// <para><see cref="LanguagesToFetch"/> is the gap, not the wish: the languages
/// the library asked for that this file does not yet have <i>well enough</i>.
/// Working it out here rather than in the search means a provider is never asked
/// for something already settled, which is the difference between a cycle that
/// costs one request per real gap and one that costs a request per title for
/// ever.</para>
///
/// <para><b>It was called <c>MissingLanguages</c>, and that stopped being
/// true.</b> Since the cutoff arrived a language can be present and still
/// outstanding — a subtitle on disk that is not known to match your release is
/// watchable tonight and worth replacing tomorrow. Leaving the old name would
/// have been a small version of the thing the cutoff exists to prevent: the code
/// saying "missing" about a file that has one.</para>
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
    IReadOnlyList<string> LanguagesToFetch,
    IntegrationFailure? LastFailure = null,
    /// <summary>
    /// This title's single most recent subtitle search attempt (#982), from
    /// the same append-only history the title's own subtitle panel reads —
    /// so the Wanted screen's status is never a second idea of what was
    /// tried. Null when nothing has been attempted yet.
    /// </summary>
    MediaSubtitleAttemptHistoryEntry? LastAttempt = null);

/// <summary>
/// One subtitle Deluno has decided never to offer again for one file, and why.
///
/// <para>The subtitle equivalent of <c>BlockedRelease</c>: permanent until
/// somebody clears it, and never hidden — every block is listed with its
/// reason (#897, #895, #898, #841).</para>
/// </summary>
public sealed record MediaSubtitleBlock(
    string Language,
    string BlockKey,
    string? ProviderKey,
    string? ReleaseName,
    string Reason,
    DateTimeOffset BlockedUtc);

/// <summary>
/// One subtitle search attempt for one title, kept for as long as the
/// retention cap allows (#840) — what a person sees on the Wanted screen and
/// in a title's own subtitle panel when they ask "what has Deluno already
/// tried".
/// </summary>
public sealed record MediaSubtitleAttemptHistoryEntry(
    string Language,
    string? Provider,
    string Outcome,
    DateTimeOffset AttemptedUtc,
    /// <summary>
    /// The rung (<c>SubtitleMatch</c>, as an int) of the best candidate this
    /// attempt found — the same number <see cref="MediaSubtitleRow.MatchRung"/>
    /// records for the subtitle actually written. Null for an attempt that
    /// found nothing, because there is no candidate to score.
    /// </summary>
    int? Score = null);

public interface IMediaSubtitleRepository
{

    /// <summary>
    /// Forget that these files were probed, so the next subtitle pass reads
    /// them again. Returns how many rows were cleared.
    /// </summary>
    Task<int> ClearScansAsync(MediaKind kind, IReadOnlyList<string> mediaIds, CancellationToken cancellationToken);
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
    /// <summary>
    /// One title's own wanted languages, read directly rather than as part of
    /// a library's bounded slice (#840) — a person-triggered search asks for
    /// this title right now, regardless of whether it is mid-backoff.
    /// </summary>
    Task<MediaSubtitleWantedItem?> GetWantedItemAsync(
        MediaKind kind,
        string mediaId,
        IReadOnlyList<string> languages,
        bool embeddedCounts,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaSubtitleWantedItem>> ListWantedAsync(
        MediaKind kind,
        string libraryId,
        IReadOnlyList<string> languages,
        int limit,
        bool embeddedCounts,
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

    /// <summary>
    /// Remembers a search that found nothing, so the next slice asks something
    /// else. Without it the search has no memory and the same titles are asked
    /// for ever while the rest of the library is never asked at all.
    /// </summary>
    Task RecordAttemptAsync(
        MediaKind kind,
        string mediaId,
        string language,
        string? result,
        TimeSpan baseDelay,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null);

    /// <summary>Forgets an outstanding attempt, because the subtitle arrived.</summary>
    Task ClearAttemptAsync(
        MediaKind kind,
        string mediaId,
        string language,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one row to this title's own subtitle search history (#840),
    /// then trims it back to the retention cap — unlike
    /// <see cref="RecordAttemptAsync"/>, which upserts one row per language
    /// to decide when the next try is due, this never overwrites: every
    /// attempt is its own row, so a person can see what was actually tried.
    /// </summary>
    Task RecordAttemptHistoryAsync(
        MediaKind kind,
        string mediaId,
        string language,
        string? provider,
        string outcome,
        CancellationToken cancellationToken,
        int? score = null);

    /// <summary>The most recent attempts for one title, newest first.</summary>
    Task<IReadOnlyList<MediaSubtitleAttemptHistoryEntry>> ListAttemptHistoryAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken);

    /// <param name="staleBefore">
    /// A file last read before this is read again even when nothing about the
    /// video changed, because what is beside it may have.
    /// </param>
    Task<IReadOnlyList<MediaSubtitleScanCandidate>> ListPendingScansAsync(
        MediaKind kind,
        string libraryId,
        int limit,
        DateTimeOffset staleBefore,
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

    /// <summary>
    /// Refuses one subtitle for one file. Automatic and manual search alike
    /// skip it from now on; deleting the file it is already on disk as is the
    /// caller's job, not this repository's.
    /// </summary>
    Task BlockSubtitleAsync(
        MediaKind kind,
        string mediaId,
        string language,
        string blockKey,
        string? providerKey,
        string? releaseName,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>Every subtitle refused for one file, for the screen that lists them.</summary>
    Task<IReadOnlyList<MediaSubtitleBlock>> ListBlockedAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken);

    /// <summary>The block keys already refused for this file, so a search can skip them.</summary>
    Task<IReadOnlySet<string>> ListBlockedKeysAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken);

    /// <summary>Undoes a block. Returns whether one was actually there to undo.</summary>
    Task<bool> UnblockSubtitleAsync(
        MediaKind kind,
        string mediaId,
        string blockKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what the last timing sync attempt found for one subtitle
    /// (#922) — moved, already in time, or unable to decide either way.
    /// </summary>
    Task RecordTimingStatusAsync(
        MediaKind kind,
        string mediaId,
        string language,
        bool forced,
        bool hearingImpaired,
        string status,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops one held subtitle row — used when a subtitle turns out to be for
    /// the wrong release or is otherwise rejected (#897, #922) and the
    /// language should go back to being searched for, not merely re-timed.
    /// Deleting the file itself is the caller's job, same as blocking one.
    /// </summary>
    Task RemoveSubtitleAsync(
        MediaKind kind,
        string mediaId,
        string language,
        bool forced,
        bool hearingImpaired,
        CancellationToken cancellationToken);
}
