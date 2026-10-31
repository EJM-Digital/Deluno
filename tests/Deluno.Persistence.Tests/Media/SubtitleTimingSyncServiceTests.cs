using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Filesystem.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Every answer the subtitle timing service can give, run on real files in a
/// temporary folder against a probe and a speech detector that say what the
/// test needs them to. #1268.
/// </summary>
public sealed class SubtitleTimingSyncServiceTests
{
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(600);

    private static List<SubtitleCue> Dialogue(int seconds = 9, int count = 60)
    {
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds((i * seconds) + (i * i * 7 % 5));
            cues.Add(new SubtitleCue(start, start + TimeSpan.FromSeconds((i * 3 % 4) + 1), "Line " + i));
        }

        return cues;
    }

    private static List<SubtitleCue> Moved(IEnumerable<SubtitleCue> cues, double seconds)
        => [.. cues.Select(cue => cue with { Start = cue.Start + TimeSpan.FromSeconds(seconds), End = cue.End + TimeSpan.FromSeconds(seconds) })];

    private static SpeechMask MaskOf(IEnumerable<SubtitleCue> cues)
    {
        var mask = new SpeechMask(SpeechMask.ToFrames(Length));
        foreach (var cue in cues)
        {
            mask.Mark(cue.Start, cue.End);
        }

        return mask;
    }

    private static MediaProbeInfo Probe(
        string status = "succeeded", double? duration = 600, string? message = null, params MediaAudioStreamInfo[] audio)
        => new(status, "ffprobe", message, duration, "matroska", null, [], audio, []);

    private static readonly MediaAudioStreamInfo English = new(1, "eac3", null, 6, "5.1", 48000, 640, "eng");
    private static readonly MediaAudioStreamInfo French = new(2, "eac3", null, 6, "5.1", 48000, 640, "fre");

    private sealed class Rig : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("deluno-timing-").FullName;
        public string Video => Path.Combine(Folder, "Dune.mkv");
        public string Subtitle => Path.Combine(Folder, "Dune.en.srt");
        public int? AskedForStream { get; set; }

        public Rig(IReadOnlyList<SubtitleCue> cues)
        {
            File.WriteAllText(Video, "not a film");
            File.WriteAllBytes(Subtitle, SubtitleTimeline.Shift(cues, TimeSpan.Zero));
        }

        public SubtitleTimingSyncService Service(MediaProbeInfo probe, Func<SpeechMask?> audio)
            => new(new StubProbe(probe), new StubDetector(this, audio), NullLogger<SubtitleTimingSyncService>.Instance);

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class StubProbe(MediaProbeInfo probe) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(probe);
    }

    private sealed class StubDetector(Rig rig, Func<SpeechMask?> audio) : ISpeechDetector
    {
        public Task<SpeechMask?> DetectAsync(string videoPath, int audioStreamIndex, TimeSpan duration, CancellationToken cancellationToken)
        {
            rig.AskedForStream = audioStreamIndex;
            return Task.FromResult(audio());
        }
    }

    private static SubtitleTimingPolicy NoStretch(double sigma = 3.0) => new(RepairFramerate: false, RequiredPeakSigma: sigma);

    [Fact]
    public async Task It_refuses_a_disabled_policy_missing_files_and_a_subtitle_with_too_few_lines()
    {
        using var rig = new Rig(Dialogue());
        var service = rig.Service(Probe(audio: English), () => MaskOf(Dialogue()));

        var off = await service.SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, new SubtitleTimingPolicy(Enabled: false));
        Assert.False(off.Adjusted);
        Assert.Contains("disabled", off.Reason);

        var noVideo = await service.SyncAsync(Path.Combine(rig.Folder, "gone.mkv"), rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("no longer on disk", noVideo.Reason);
        var noSubtitle = await service.SyncAsync(rig.Video, Path.Combine(rig.Folder, "gone.srt"), "en", CancellationToken.None);
        Assert.Contains("no longer on disk", noSubtitle.Reason);

        using var few = new Rig(Dialogue(count: 3));
        var tooFew = await few.Service(Probe(audio: English), () => MaskOf(Dialogue())).SyncAsync(few.Video, few.Subtitle, "en", CancellationToken.None);
        Assert.Contains("only 3 line(s)", tooFew.Reason);
    }

    [Fact]
    public async Task It_says_why_when_the_video_cannot_be_listened_to()
    {
        using var rig = new Rig(Dialogue());

        var failed = await rig.Service(Probe("failed", message: "ffprobe broke"), () => null).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("could not be read", failed.Reason);
        Assert.EndsWith("ffprobe broke", failed.Reason);

        var noLength = await rig.Service(Probe(duration: null, audio: English), () => null).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("how long it is", noLength.Reason);
        var zeroLength = await rig.Service(Probe(duration: 0, audio: English), () => null).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("how long it is", zeroLength.Reason);

        var noAudio = await rig.Service(Probe(), () => null).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("no audio track", noAudio.Reason);

        var deaf = await rig.Service(Probe(audio: English), () => null).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("could not be listened to", deaf.Reason);

        var silent = await rig.Service(Probe(audio: English), () => new SpeechMask(SpeechMask.ToFrames(Length))).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None);
        Assert.Contains("nothing in it to line up", silent.Reason);
    }

    [Fact]
    public async Task It_listens_to_the_track_in_the_titles_original_language_and_otherwise_the_first()
    {
        using var rig = new Rig(Dialogue());
        var probe = Probe(audio: [English, French]);

        await rig.Service(probe, () => null).SyncAsync(rig.Video, rig.Subtitle, "fr", CancellationToken.None);
        Assert.Equal(2, rig.AskedForStream);

        await rig.Service(probe, () => null).SyncAsync(rig.Video, rig.Subtitle, "ja", CancellationToken.None);
        Assert.Equal(1, rig.AskedForStream);

        await rig.Service(probe, () => null).SyncAsync(rig.Video, rig.Subtitle, null, CancellationToken.None);
        Assert.Equal(1, rig.AskedForStream);
    }

    [Fact]
    public async Task A_subtitle_that_is_early_is_moved_later_and_one_that_is_late_is_moved_earlier()
    {
        foreach (var (shift, wording) in new[] { (5.0, "later"), (-4.0, "earlier") })
        {
            using var rig = new Rig(Dialogue());
            var audio = Moved(Dialogue(), shift);

            var result = await rig.Service(Probe(audio: English), () => MaskOf(audio)).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch());

            Assert.True(result.Adjusted);
            Assert.Equal(SubtitleTimingOutcome.Moved, result.Outcome);
            Assert.Equal(shift, result.Offset.TotalSeconds, 1);
            Assert.Contains(wording, result.Reason);
            var written = SubtitleTimeline.Parse(await File.ReadAllBytesAsync(rig.Subtitle));
            Assert.Equal(Dialogue()[5].Start.TotalSeconds + shift, written[5].Start.TotalSeconds, 1);
            Assert.False(File.Exists(rig.Subtitle + ".partial"));
        }
    }

    [Fact]
    public async Task A_subtitle_that_is_already_in_time_is_left_alone()
    {
        using var rig = new Rig(Dialogue());
        var before = await File.ReadAllBytesAsync(rig.Subtitle);

        var exact = await rig.Service(Probe(audio: English), () => MaskOf(Dialogue())).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch());
        var hair = await rig.Service(Probe(audio: English), () => MaskOf(Moved(Dialogue(), 0.05))).SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch());

        Assert.Equal(SubtitleTimingOutcome.AlreadyInTime, exact.Outcome);
        Assert.Equal(SubtitleTimingOutcome.AlreadyInTime, hair.Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(rig.Subtitle));
    }

    [Fact]
    public async Task A_small_move_that_would_not_clearly_help_is_not_made()
    {
        var wide = Dialogue(seconds: 4).Select(cue => cue with { End = cue.Start + TimeSpan.FromSeconds(3.8) }).ToList();
        using var rig = new Rig(wide);

        var result = await rig.Service(Probe(audio: English), () => MaskOf(Moved(wide, 0.3)))
            .SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch(sigma: 1.0));

        Assert.False(result.Adjusted);
        Assert.Equal(SubtitleTimingOutcome.AlreadyInTime, result.Outcome);
        Assert.Contains("would not clearly improve", result.Reason);
    }

    [Fact]
    public async Task A_subtitle_that_only_fits_at_the_edge_of_the_window_is_called_the_wrong_release()
    {
        using var rig = new Rig(Dialogue());

        var result = await rig.Service(Probe(audio: English), () => MaskOf(Moved(Dialogue(), 59.95)))
            .SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch());

        Assert.Equal(SubtitleTimingOutcome.Mismatched, result.Outcome);
        Assert.Contains("different release", result.Reason);
    }

    [Fact]
    public async Task A_subtitle_that_does_not_line_up_anywhere_is_left_as_it_was()
    {
        using var rig = new Rig(Dialogue());
        var unrelated = Enumerable.Range(0, 60).Select(i => new SubtitleCue(
            TimeSpan.FromSeconds((i * 10) + 3), TimeSpan.FromSeconds((i * 10) + 6), "x")).ToList();
        var before = await File.ReadAllBytesAsync(rig.Subtitle);

        var result = await rig.Service(Probe(audio: English), () => MaskOf(unrelated))
            .SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, NoStretch(sigma: 10.0));

        Assert.Equal(SubtitleTimingOutcome.Inconclusive, result.Outcome);
        Assert.Contains("does not line up", result.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(rig.Subtitle));
    }

    [Theory]
    [InlineData(25d / (24000d / 1001d), "slowed by")]
    [InlineData((24000d / 1001d) / 25d, "sped up by")]
    public async Task A_subtitle_for_another_framerate_is_stretched_back_and_moved(double factor, string wording)
    {
        using var rig = new Rig(Dialogue());
        var audio = SubtitleTimeline.Parse(SubtitleTimeline.Shift(SubtitleTimeline.Rescale(Dialogue(), factor), TimeSpan.Zero));

        var result = await rig.Service(Probe(audio: English), () => MaskOf(audio))
            .SyncAsync(rig.Video, rig.Subtitle, "en", CancellationToken.None, new SubtitleTimingPolicy());

        Assert.True(result.Adjusted);
        Assert.Equal(SubtitleTimingOutcome.Moved, result.Outcome);
        Assert.Contains("different speed", result.Reason);
        Assert.Contains(wording, result.Reason);
    }
}
