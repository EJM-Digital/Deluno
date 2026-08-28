using System.Globalization;
using Deluno.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem.Subtitles;

/// <summary>
/// Timing sync, end to end: listen to the video, read the subtitle, find the
/// offset that fits, and rewrite the file only if moving it is an improvement.
///
/// <para><b>The rule this feature has to obey is "first, do no harm".</b> Most
/// subtitles Deluno fetches are already in time. A sync that shifted them
/// anyway — because the correlation found a peak somewhere, as a correlation
/// always will — would take a library that was fine and quietly break part of
/// it, and nothing on screen would say so until somebody sat down to watch. So
/// the default is to leave the file alone, and every reason to touch it has to
/// clear a bar that is stated here rather than discovered in the numbers.</para>
/// </summary>
public sealed class SubtitleTimingSyncService(
    IMediaProbeService mediaProbeService,
    ISpeechDetector speechDetector,
    ILogger<SubtitleTimingSyncService> logger)
    : ISubtitleTimingSync
{
    /// <summary>
    /// The furthest a subtitle is assumed to be out.
    ///
    /// <para>Sixty seconds covers everything that happens in practice — a
    /// different distributor's logo, a missing recap, a PAL master starting on a
    /// different frame — and stops well short of the offsets where a correlation
    /// starts finding convincing peaks in the wrong place. The search is cheap
    /// enough that this is a correctness bound rather than a performance
    /// one.</para>
    /// </summary>
    private static readonly TimeSpan MaxOffset = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Below this, moving the subtitle is not worth the risk of being wrong.
    ///
    /// <para>A tenth of a second is roughly where a person begins to notice a
    /// subtitle is off, and the mask's own resolution is a hundredth, so
    /// anything under this is inside the noise of the method itself.</para>
    /// </summary>
    private static readonly TimeSpan SmallestWorthMoving = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How much better the new position has to be than the old one.
    ///
    /// <para>Twenty per cent more overlapping speech, not "any improvement at
    /// all". Two masks of the same film always share a little more at some shift
    /// than at none — dialogue is not evenly spread, so sliding a subtitle a
    /// second either way lands some of it on speech by luck. That luck is worth
    /// a few per cent. A genuine offset is worth far more than twenty, and the
    /// gap between those two numbers is where this threshold sits.</para>
    /// </summary>
    private const double RequiredImprovement = 0.20;

    /// <summary>
    /// How far the best shift has to stand above the noise before the answer is
    /// believed at all, in standard deviations.
    ///
    /// <para>Guards the case the improvement ratio cannot see: a subtitle for a
    /// completely different film still has a best shift, and it can still be
    /// twenty per cent better than sitting at rest. Two earlier guards were
    /// wrong here and both were caught by measuring rather than by
    /// reasoning.</para>
    ///
    /// <para><b>Coverage was the first, and it does not work at all.</b> It asked
    /// how much of the subtitle landed on speech, on the reasoning that an
    /// unrelated subtitle would land on very little. It lands on a great deal:
    /// two films with talking in them are both talking most of the time, so an
    /// unrelated pair overlapped 41%, past the third it was about to require.
    /// </para>
    ///
    /// <para><b>The ratio to the mean was the second, and it works but not with
    /// any room.</b> Run against the lab episode it gave 1.64 for a subtitle
    /// that genuinely matched and 1.13 for one that did not — either side of a
    /// threshold of 1.5, a margin of 0.14. A ratio to the average says nothing
    /// about how much the overlap moves from shift to shift, which is precisely
    /// what makes a peak a peak.</para>
    ///
    /// <para><b>Measured, on the lab episode's real audio through the real
    /// FFmpeg.</b> A matching subtitle displaced by anything from 300&#160;ms to
    /// 30&#160;s, in both directions, peaked at <b>4.7–4.8&#160;σ</b> and its
    /// offset came back exact every time. The unrelated Severance subtitle over
    /// the same audio reached <b>1.3–2.0&#160;σ</b>. Three sits between them with
    /// about half again in hand on each side.</para>
    /// </summary>
    private const double RequiredPeakSigma = 3.0;

    public async Task<SubtitleTimingResult> SyncAsync(
        string videoPath,
        string subtitlePath,
        string? originalLanguage,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(videoPath) || !File.Exists(subtitlePath))
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "The video or its subtitle is no longer on disk.");
        }

        var cues = SubtitleTimeline.Parse(await File.ReadAllBytesAsync(subtitlePath, cancellationToken));
        if (cues.Count < 10)
        {
            // Ten is not a quality bar, it is an arithmetic one: a handful of
            // cues cannot produce a correlation peak that means anything, and
            // forced-narrative tracks — a dozen lines across a whole film — are
            // exactly the files that would be moved on a coincidence.
            return new SubtitleTimingResult(false, TimeSpan.Zero,
                $"There are only {cues.Count} line(s) in this subtitle, which is too few to time it against the audio.");
        }

        var probe = await mediaProbeService.ProbeAsync(videoPath, cancellationToken);
        if (probe.Status != "succeeded")
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero,
                $"The video's streams could not be read, so there was nothing to time this against. {probe.Message}".TrimEnd());
        }

        if (probe.DurationSeconds is not { } seconds || seconds <= 0)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "The video does not report how long it is.");
        }

        var track = ChooseAudioTrack(probe, originalLanguage);
        if (track is null)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "The video has no audio track to time the subtitle against.");
        }

        var duration = TimeSpan.FromSeconds(seconds);
        var audio = await speechDetector.DetectAsync(videoPath, track.Index, duration, cancellationToken);
        if (audio is null)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "The video's audio could not be listened to.");
        }

        var subtitle = BuildMask(cues, duration);
        if (subtitle.Population == 0 || audio.Population == 0)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "Either the subtitle or the audio has nothing in it to line up.");
        }

        var best = audio.Correlate(subtitle, SpeechMask.ToFrames(MaxOffset));

        logger.LogDebug(
            "Timing {Subtitle}: best shift {Shift}, overlap {Score} against {Zero} at rest and {Mean:F0} +/- {Deviation:F1} across the search, {Sigma:F1} sigma.",
            subtitlePath, best.Shift, best.Score, best.ScoreAtZero, best.MeanScore, best.ScoreDeviation, best.PeakSigma);

        if (best.PeakSigma < RequiredPeakSigma)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero,
                "This subtitle does not line up with the video's dialogue at any one point, so it has been left exactly as it was.");
        }

        var offset = best.Shift;
        if (offset.Duration() < SmallestWorthMoving)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero, "This subtitle is already in time.");
        }

        if (best.Improvement < RequiredImprovement)
        {
            return new SubtitleTimingResult(false, TimeSpan.Zero,
                "Moving this subtitle would not clearly improve it, so it has been left alone.");
        }

        // Written to a temporary name and moved, for the reason the fetch writer
        // does it: a half-written subtitle is a file a player opens and shows
        // nothing from, and the scan would count it as held.
        var temporary = subtitlePath + ".partial";
        await File.WriteAllBytesAsync(temporary, SubtitleTimeline.Shift(cues, offset), cancellationToken);
        File.Move(temporary, subtitlePath, overwrite: true);

        var described = Describe(offset);
        logger.LogInformation("Moved {Subtitle} {Description} to match the audio.", subtitlePath, described);

        return new SubtitleTimingResult(true, offset, $"Timed against the video's audio and moved {described}.");
    }

    /// <summary>
    /// Where the subtitle says there is talking.
    ///
    /// <para>A cue's own start and end, and nothing cleverer. Reading rate,
    /// character counts and line breaks would all be guesses about how much of
    /// the cue's window is really speech; the window itself is what the person
    /// who timed the subtitle actually asserted.</para>
    /// </summary>
    private static SpeechMask BuildMask(IReadOnlyList<SubtitleCue> cues, TimeSpan duration)
    {
        // Sized to the longer of the two, because a subtitle for a longer cut
        // than the file you have must not be silently truncated into agreeing.
        var last = cues[^1].End;
        var mask = new SpeechMask(SpeechMask.ToFrames(last > duration ? last : duration));

        foreach (var cue in cues)
        {
            mask.Mark(cue.Start, cue.End);
        }

        return mask;
    }

    /// <summary>
    /// Which audio track carries the dialogue the subtitle was written for.
    ///
    /// <para>The title's own language first, when Deluno knows it — a Korean film
    /// with an English dub has two tracks that say different things at different
    /// moments, and aligning Korean subtitles against the dub would produce a
    /// confident, wrong answer rather than no answer. Failing that, the first
    /// track, which is the one every muxer puts the original in.</para>
    /// </summary>
    private static MediaAudioStreamInfo? ChooseAudioTrack(MediaProbeInfo probe, string? originalLanguage)
    {
        if (probe.AudioStreams.Count == 0)
        {
            return null;
        }

        var wanted = SubtitleLanguages.Normalize(originalLanguage);
        if (wanted is not null)
        {
            var match = probe.AudioStreams.FirstOrDefault(stream =>
                SubtitleLanguages.Normalize(stream.Language) == wanted);

            if (match is not null)
            {
                return match;
            }
        }

        return probe.AudioStreams[0];
    }

    private static string Describe(TimeSpan offset)
    {
        var seconds = Math.Abs(offset.TotalSeconds).ToString("0.##", CultureInfo.InvariantCulture);
        return offset > TimeSpan.Zero ? $"{seconds}s later" : $"{seconds}s earlier";
    }
}
