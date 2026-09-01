namespace Deluno.Contracts;

/// <summary>
/// What happened when Deluno tried to put a subtitle in time with its video.
/// </summary>
/// <param name="Adjusted">Whether the file on disk was rewritten.</param>
/// <param name="Offset">
/// How far every cue was moved. Positive means the subtitle was late and has
/// been pushed later still — a subtitle cut for a release with a longer
/// distributor's logo at the front.
/// </param>
/// <param name="Reason">
/// One sentence, in the words a person uses, for Activity and for the file's own
/// page. This is the whole point of the feature being explainable: "moved 2.4 s
/// later" is a fact somebody can check against the film, where "synced" is not.
/// </param>
public sealed record SubtitleTimingResult(bool Adjusted, TimeSpan Offset, string Reason);

/// <summary>
/// Puts a subtitle in time with the video it belongs to.
///
/// <para><b>Declared here for the same reason <see cref="ISubtitleFileWriter"/>
/// is.</b> The code that decides a subtitle needs timing help lives in
/// Integrations, and the code that can open a video and rewrite a file lives in
/// Filesystem, and those two modules cannot see each other. Contracts is where
/// they meet — and putting this beside the writer keeps the whole of what the
/// subtitle feature asks of the filesystem in one place, rather than one half
/// here and one half discovered later.</para>
///
/// <para><b>Why it exists at all.</b> A subtitle that is out by two seconds is
/// worse than no subtitle: it is readable, it is confidently wrong, and the
/// person watching spends the film doing arithmetic. #321 records it as the
/// single biggest reason anybody touches subtitles by hand, and DESIGN-002's
/// rung ladder already says which ones are at risk —
/// <see cref="SubtitleMatch.MadeForThisFile"/> was cut against this exact
/// encode and is in time by construction, and everything below it was
/// not.</para>
///
/// <para>The default trigger follows that same cutoff. A library can narrow it
/// to the safer "below same source" rung, disable automatic repair, or exclude
/// providers whose timing is known to be unreliable. Those choices travel with
/// the queued job so a later worker run cannot silently fall back to a different
/// library setting.</para>
/// </summary>
public interface ISubtitleTimingSync
{
    /// <summary>
    /// Aligns <paramref name="subtitlePath"/> against <paramref name="videoPath"/>,
    /// rewriting it in place only if that is an improvement.
    ///
    /// <para><b>Doing nothing is a normal outcome and not a failure.</b> A
    /// subtitle that is already in time, a video whose audio cannot be read, and
    /// a file with too little dialogue to align against all come back with
    /// <c>Adjusted</c> false and a sentence saying which — because a feature that
    /// silently declined to act is how the release-search switches came to be
    /// starving subtitles for a whole session with nothing on screen.</para>
    /// </summary>
    /// <param name="originalLanguage">
    /// The title's own language, when Deluno knows it, so the audio track that
    /// actually carries the dialogue is preferred over a dub. Null is fine.
    /// </param>
    /// <param name="policy">
    /// The library's timing policy. Null preserves the default of syncing below
    /// "made for this file", within a 60-second search window and a 3-sigma
    /// confidence threshold.
    /// </param>
    Task<SubtitleTimingResult> SyncAsync(
        string videoPath,
        string subtitlePath,
        string? originalLanguage,
        CancellationToken cancellationToken,
        SubtitleTimingPolicy? policy = null);
}
