namespace Deluno.Contracts;

/// <summary>
/// How well a subtitle fits the file it is for.
///
/// <para><b>Read out of Bazarr rather than invented.</b> DESIGN-002 originally
/// offered three phrasings and James refused all three: <i>"this is the thing
/// that we need to look into with bazaar and how it does it properly."</i> So
/// <c>custom_libs/subliminal_patch/score.py</c> and <c>bazarr/app/config.py</c>
/// were read at master, and the eleven weights turn out not to be a quality
/// model at all.</para>
///
/// <para>They are gates with a tiebreaker tail. For an episode, at Bazarr's
/// shipped 90%: the right show and the right episode scores 86% and <i>fails</i>;
/// add <c>source</c> (25 points) and it is 93% and passes. For a film at 70%,
/// title and year is 56% and fails; add source and it is 72% and passes. On both
/// media the only thing between fail and pass is <b>which master the release was
/// cut from</b>. Meanwhile <c>resolution</c>, <c>video_codec</c>,
/// <c>audio_codec</c>, <c>streaming_service</c> and <c>hearing_impaired</c> are
/// one point each and can never turn a pass into a fail.</para>
///
/// <para>And <c>hash</c> is always <c>max − 1</c> — Bazarr <i>validates</i> that
/// and discards your weights if you break it. So "the provider matched the file
/// itself" is structural, not a weight, and it beats every combination of
/// metadata.</para>
///
/// <para>Three rungs, then, in the words a person can hold, rather than a
/// percentage nobody can reason about.</para>
/// </summary>
public enum SubtitleMatch
{
    /// <summary>
    /// The right film or episode, and nothing more is known. Watchable. The
    /// timing may need a nudge, because a subtitle cut for the Blu-ray starts at
    /// a different moment from the WEB release of the same thing.
    /// </summary>
    AnyRelease = 0,

    /// <summary>
    /// Cut from the same master — WEB, Blu-ray, HDTV. Timing is right almost
    /// every time. <b>This is exactly what Bazarr's shipped default means</b>,
    /// and it is the rung its 90% and 70% both land on.
    /// </summary>
    SameSource = 1,

    /// <summary>
    /// Made for this file: the subtitle names your exact release group, so it was
    /// cut against the very encode you have. Timing is guaranteed.
    ///
    /// <para><b>Deluno's cutoff.</b> James: <i>"we need the best method, no point
    /// spreading lies about subs that may be out of sync etc etc."</i> So Deluno
    /// keeps looking past Bazarr's default until it finds this, and only calls a
    /// language done when it has.</para>
    /// </summary>
    MadeForThisFile = 2
}

/// <summary>
/// Where Deluno stops looking, named once.
///
/// <para>Two things have to agree about this and they live in different
/// assemblies: the fetcher, which decides whether a language is finished with,
/// and the store, whose "what is still outstanding" query has to ask the same
/// question in SQL. Two copies of a cutoff is the shape every defect in this
/// codebase has had, so there is one, here, beside the rest of the subtitle
/// vocabulary.</para>
/// </summary>
public static class SubtitleCutoff
{
    /// <summary>
    /// James, asked which rung: <i>"we need the best method, no point spreading
    /// lies about subs that may be out of sync etc etc."</i> So it is the top of
    /// the ladder — past Bazarr's shipped default, which decodes to
    /// <see cref="SubtitleMatch.SameSource"/>.
    /// </summary>
    public const SubtitleMatch Rung = SubtitleMatch.MadeForThisFile;
}

