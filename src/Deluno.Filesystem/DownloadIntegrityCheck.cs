using Deluno.Quality;

namespace Deluno.Filesystem;

/// <summary>Where <see cref="DownloadIntegrityCheck.RunAsync"/> left a file (#917).</summary>
public enum DownloadIntegrityOutcome
{
    /// <summary>Every rule Deluno can check today passed.</summary>
    Pass,

    /// <summary>Still being written or locked. Not a verdict either way -- wait and ask again.</summary>
    StillWriting,

    /// <summary>A bad release: the reason is in plain words, ready for Activity.</summary>
    Failed
}

/// <param name="Outcome">What Deluno decided.</param>
/// <param name="Reason">Set only when <see cref="Outcome"/> is <see cref="DownloadIntegrityOutcome.Failed"/>.</param>
public sealed record DownloadIntegrityResult(DownloadIntegrityOutcome Outcome, string? Reason)
{
    public static readonly DownloadIntegrityResult StillWriting = new(DownloadIntegrityOutcome.StillWriting, null);
    public static readonly DownloadIntegrityResult Passed = new(DownloadIntegrityOutcome.Pass, null);
    public static DownloadIntegrityResult Failed(string reason) => new(DownloadIntegrityOutcome.Failed, reason);
}

/// <summary>
/// Checkpoint A (#917): as soon as a download is finished and settled, and
/// before it is handed to a processor or imported directly, Deluno opens the
/// file and checks it against what its own name and the catalogue's known
/// runtime claim. A file that fails is a bad release -- the processor never
/// sees it.
///
/// <para>Off by default for every install (#936): this is a brand-new check
/// that can reject a release outright, so nothing about it is enabled until a
/// person turns it on in Settings.</para>
///
/// <para>The audio-language rule reads a library's own quality profile
/// (#769's <see cref="ProfileLanguagePreference"/>): only a profile
/// that requires a language at all can fail a file for lacking one, so an
/// install that never set a language preference sees no change here.</para>
///
/// <para>The source may be a single file or a folder:
/// <see cref="DownloadIntegrityFileResolver"/> resolves a folder to the
/// film's own file (a movie in a folder, extras and samples set aside) or,
/// for a season pack, to every episode file inside it -- each one gets its
/// own run of every rule below, and the first failure ends the whole
/// check.</para>
/// </summary>
public static class DownloadIntegrityCheck
{
    /// <summary>Well past what any real film or episode overruns by; runs this long past the known length are their own kind of wrong (#917's "in either direction").</summary>
    private const double MaximumRuntimePercent = 200.0;

    public static async Task<DownloadIntegrityResult> RunAsync(
        string sourcePath,
        string releaseName,
        int? knownRuntimeMinutes,
        int minimumRuntimePercent,
        IMediaProbeService probeService,
        CancellationToken cancellationToken,
        ProfileLanguagePreference? languagePreference = null,
        // The title Deluno already knows for this grab (the catalogue's own
        // title, not the release name) -- read by the "not the film/episode
        // the title says" rule below. Null means Deluno has nothing to
        // compare an embedded title tag against, so that rule never fires.
        string? knownTitle = null)
    {
        var files = DownloadIntegrityFileResolver.Resolve(sourcePath);
        if (files.Count == 0)
        {
            // Nothing recognisable to check yet -- still unpacking, or only a
            // sample has arrived so far. Waited for, not failed.
            return DownloadIntegrityResult.StillWriting;
        }

        // A season pack's files are each checked in turn; the first one that
        // fails or is still settling ends the check for the whole download,
        // the same as it would for a single file.
        foreach (var filePath in files)
        {
            var result = await RunOnFileAsync(
                filePath, releaseName, knownRuntimeMinutes, minimumRuntimePercent, probeService, cancellationToken, languagePreference, knownTitle);
            if (result.Outcome != DownloadIntegrityOutcome.Pass)
            {
                return result;
            }
        }

        return DownloadIntegrityResult.Passed;
    }

    private static async Task<DownloadIntegrityResult> RunOnFileAsync(
        string filePath,
        string releaseName,
        int? knownRuntimeMinutes,
        int minimumRuntimePercent,
        IMediaProbeService probeService,
        CancellationToken cancellationToken,
        ProfileLanguagePreference? languagePreference,
        string? knownTitle)
    {
        // A file still being written or locked is waited for, not failed.
        if (!ImportFileReadiness.IsReady(filePath))
        {
            return DownloadIntegrityResult.StillWriting;
        }

        var probe = await probeService.ProbeAsync(filePath, cancellationToken);

        // "Won't open or play": corrupt, no video, no audio. Media probing
        // itself being unavailable (no ffprobe on this install) is not a
        // verdict on the file -- see IMediaProbeService's own contract -- so
        // only a definite "failed" or a definite absence of a stream counts.
        if (probe.Status == "failed")
        {
            return DownloadIntegrityResult.Failed(
                probe.Message is { Length: > 0 } message
                    ? $"The file will not open or play: {message}"
                    : "The file will not open or play.");
        }

        if (probe.Status == "succeeded" && probe.VideoStreams.Count == 0)
        {
            return DownloadIntegrityResult.Failed("No video stream was detected in this file.");
        }

        if (probe.Status == "succeeded" && probe.AudioStreams.Count == 0)
        {
            return DownloadIntegrityResult.Failed("No audio stream was detected in this file.");
        }

        // "Missing the audio language you want": none of its audio tracks is
        // in a wanted language. Only a profile that requires one (rather than
        // merely preferring it for ranking) can fail a file over this -- a
        // ranking-only preference never refuses a release outright.
        if (probe.Status == "succeeded" &&
            languagePreference is { RequireLanguage: true, PreferredLanguages.Count: > 0 } preference)
        {
            var streamLanguages = probe.AudioStreams.Select(stream => stream.Language).ToArray();
            var wanted = preference.PreferredLanguages
                .Where(code => AudioLanguageMatch.Recognizes(code))
                .ToArray();
            if (wanted.Length > 0 && !wanted.Any(code => AudioLanguageMatch.AnyMatches(streamLanguages, code)))
            {
                var wantedLabel = string.Join(" or ", wanted.Select(ProfileLanguageCodes.Label));
                return DownloadIntegrityResult.Failed(
                    $"None of this file's audio tracks are in {wantedLabel}, which this library wants.");
            }
        }

        if (probe.Status != "succeeded")
        {
            // Unavailable (no ffprobe): every other rule needs the picture and
            // the runtime, neither of which Deluno can read. Pass rather than
            // block on a check it cannot run.
            return DownloadIntegrityResult.Passed;
        }

        // "Lower resolution than the name says", judged by width (#917).
        var claimed = ReleaseNameQuality.ClaimedResolution(releaseName);
        var actualWidth = probe.VideoStreams.Select(stream => stream.Width ?? 0).DefaultIfEmpty(0).Max();
        if (claimed is { } resolution && actualWidth > 0 && actualWidth < resolution.MinimumWidth)
        {
            return DownloadIntegrityResult.Failed(
                $"Name said {resolution.Label}; the picture is {actualWidth} pixels wide.");
        }

        // "Much shorter than the film or episode" / "not the film/episode the
        // title says" (runtime far in either direction). Both read the same
        // known-runtime comparison; #917 only fails the far side on strong
        // evidence, which a runtime this far out already is.
        if (knownRuntimeMinutes is > 0 && probe.DurationSeconds is > 0)
        {
            var actualMinutes = probe.DurationSeconds.Value / 60.0;
            var percent = actualMinutes / knownRuntimeMinutes.Value * 100.0;
            if (percent < minimumRuntimePercent)
            {
                return DownloadIntegrityResult.Failed(
                    $"Name said {knownRuntimeMinutes} minutes; the file runs {actualMinutes:0} minutes, under {minimumRuntimePercent}% of that.");
            }

            if (percent > MaximumRuntimePercent)
            {
                return DownloadIntegrityResult.Failed(
                    $"The file runs {actualMinutes:0} minutes, far more than the {knownRuntimeMinutes} minutes this is known to run. This may not be the right film or episode.");
            }
        }

        // "Not the film/episode the title says", the other strong-evidence
        // route: the file's own embedded title tag names something else
        // entirely. Anything less than a clean, confident mismatch -- no tag
        // at all, a tag too short to mean much, or one that overlaps at all
        // with the known title -- is weak or missing evidence and never fails
        // a file (#917).
        if (EmbeddedTitleContradicts(probe.EmbeddedTitle, knownTitle))
        {
            return DownloadIntegrityResult.Failed(
                $"The file's own title says \"{probe.EmbeddedTitle}\", not \"{knownTitle}\". This may not be the right film or episode.");
        }

        return DownloadIntegrityResult.Passed;
    }

    /// <summary>
    /// Whether an embedded title tag is strong evidence of a different film,
    /// per #917's "only on strong evidence" rule. Deliberately conservative:
    /// a missing tag, a short/generic one, or one that shares any overlap
    /// with the known title (a studio's own longer or shorter naming, a
    /// subtitle appended to the main title) is left alone.
    /// </summary>
    private static bool EmbeddedTitleContradicts(string? embeddedTitle, string? knownTitle)
    {
        if (string.IsNullOrWhiteSpace(embeddedTitle) || string.IsNullOrWhiteSpace(knownTitle))
        {
            return false;
        }

        var normalizedEmbedded = Normalize(embeddedTitle);
        var normalizedKnown = Normalize(knownTitle);
        if (normalizedEmbedded.Length < 3 || normalizedKnown.Length < 3)
        {
            return false;
        }

        if (normalizedEmbedded == normalizedKnown ||
            normalizedEmbedded.Contains(normalizedKnown, StringComparison.Ordinal) ||
            normalizedKnown.Contains(normalizedEmbedded, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
