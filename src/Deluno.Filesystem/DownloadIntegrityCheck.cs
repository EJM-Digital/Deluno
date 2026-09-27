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
/// <para>Not yet checked: a folder-shaped download (a season pack, a movie
/// with extras) is not resolved to "the" file to probe -- see the
/// worker-side caller for where a folder is passed over today.</para>
/// </summary>
public static class DownloadIntegrityCheck
{
    /// <summary>Well past what any real film or episode overruns by; runs this long past the known length are their own kind of wrong (#917's "in either direction").</summary>
    private const double MaximumRuntimePercent = 200.0;

    public static async Task<DownloadIntegrityResult> RunAsync(
        string filePath,
        string releaseName,
        int? knownRuntimeMinutes,
        int minimumRuntimePercent,
        IMediaProbeService probeService,
        CancellationToken cancellationToken,
        ProfileLanguagePreference? languagePreference = null)
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

        return DownloadIntegrityResult.Passed;
    }
}
