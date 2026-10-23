using Deluno.Filesystem;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Checkpoint A (#917): as soon as a download is finished and settled, and
/// before it is handed to a processor or imported directly, Deluno opens the
/// file and checks it against what its own name and the catalogue's known
/// runtime claim.
/// </summary>
public sealed class DownloadIntegrityCheckTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"deluno-integrity-{Guid.NewGuid():n}.mkv");

    public DownloadIntegrityCheckTests()
    {
        File.WriteAllText(_path, "content");
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(-5));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public async Task A_file_still_being_written_is_waited_for_not_failed()
    {
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow);

        var result = await Run(Probe.Ok(1920, 1080, 120), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.StillWriting, result.Outcome);
    }

    [Fact]
    public async Task A_file_that_will_not_open_fails_with_ffprobes_own_words()
    {
        var result = await Run(Probe.Failed("moov atom not found"), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("moov atom not found", result.Reason);
    }

    [Fact]
    public async Task A_file_ffprobe_could_not_read_fails_in_plain_words_and_keeps_ffmpegs_text_as_the_detail()
    {
        const string raw = @"[matroska,webm @ 000001b280590a00] EBML header parsing failed C:\Weir\Ready\Movies\Reject.Film.mkv: Invalid data found when processing input";
        var result = await Run(new Probe(FfprobeMediaProbeService.Failed(raw)), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("can't be read as video", result.Reason);
        Assert.DoesNotContain("EBML", result.Reason);
        Assert.DoesNotContain("matroska", result.Reason);
        Assert.DoesNotContain("Invalid data", result.Reason);
        Assert.Equal(raw, result.Detail);
    }

    [Fact]
    public async Task A_file_with_no_video_stream_fails()
    {
        var result = await Run(Probe.NoVideo(120), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("No video stream", result.Reason);
    }

    [Fact]
    public async Task A_file_with_no_audio_stream_fails()
    {
        var result = await Run(Probe.NoAudio(1920, 1080, 120), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("No audio stream", result.Reason);
    }

    [Fact]
    public async Task A_lower_resolution_than_the_name_claims_fails()
    {
        var result = await Run(Probe.Ok(1920, 1080, 120), "Sintel.2010.2160p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("Name said 2160p", result.Reason);
        Assert.Contains("1920x1080", result.Reason);
    }

    /// <summary>#1148: a picture meets its claim by width or by height, so 4:3 and cropped widescreen both pass.</summary>
    [Theory]
    [InlineData(1440, 1080, "1080p")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(2880, 2160, "2160p")]
    [InlineData(960, 720, "720p")]
    public async Task A_picture_that_reaches_the_claim_by_width_or_height_passes(int width, int height, string claimed)
    {
        var result = await Run(Probe.Ok(width, height, 120 * 60), $"Nosferatu.1922.{claimed}.BluRay.x264-HD4U", 120);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task A_720p_picture_named_1080p_still_fails()
    {
        var result = await Run(Probe.Ok(1280, 720, 120 * 60), "Nosferatu.1922.1080p.BluRay.x264-HD4U", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("Name said 1080p", result.Reason);
    }

    /// <summary>A cropped widescreen 3840&#215;1600 still counts as 2160p -- judged by width.</summary>
    [Fact]
    public async Task A_cropped_widescreen_frame_still_passes_as_its_claimed_resolution()
    {
        var result = await Run(Probe.Ok(3840, 1600, 120 * 60), "Sintel.2010.2160p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task A_runtime_well_under_the_known_length_fails()
    {
        // 90 of 120 known minutes = 75%, under the 80% default.
        var result = await Run(Probe.Ok(1920, 1080, 90 * 60), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("under 80%", result.Reason);
    }

    [Fact]
    public async Task A_runtime_close_to_the_known_length_passes()
    {
        // 100 of 120 known minutes = 83%.
        var result = await Run(Probe.Ok(1920, 1080, 100 * 60), "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task A_runtime_far_longer_than_known_fails_as_possibly_the_wrong_title()
    {
        // 60 of a 24-minute known episode = 250%.
        var result = await Run(Probe.Ok(1920, 1080, 60 * 60), "Lioness.S02E07.1080p.WEB-DL", 24);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("may not be the right film or episode", result.Reason);
    }

    [Fact]
    public async Task No_ffprobe_available_passes_rather_than_blocking_on_a_check_it_cannot_run()
    {
        var result = await Run(Probe.Unavailable(), "Sintel.2010.2160p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>
    /// "Missing the audio language you want": the file's only audio track is
    /// English, but the library requires Japanese (#917, #769).
    /// </summary>
    [Fact]
    public async Task None_of_the_audio_tracks_in_the_wanted_language_fails()
    {
        var wantsJapanese = new ProfileLanguagePreference(["ja"], RequireLanguage: true);

        var result = await Run(Probe.Ok(1920, 1080, 120 * 60), "Sintel.2010.1080p.WEB-DL", 120, wantsJapanese);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("Japanese", result.Reason);
    }

    [Fact]
    public async Task A_track_in_the_wanted_language_passes()
    {
        var wantsEnglish = new ProfileLanguagePreference(["en"], RequireLanguage: true);

        var result = await Run(Probe.Ok(1920, 1080, 120 * 60), "Sintel.2010.1080p.WEB-DL", 120, wantsEnglish);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>A preference that only ranks, rather than requiring, never refuses a release for its language.</summary>
    [Fact]
    public async Task A_ranking_only_language_preference_never_fails_a_file()
    {
        var prefersJapanese = new ProfileLanguagePreference(["ja"], RequireLanguage: false);

        var result = await Run(Probe.Ok(1920, 1080, 120 * 60), "Sintel.2010.1080p.WEB-DL", 120, prefersJapanese);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task No_language_preference_at_all_is_unaffected()
    {
        var result = await Run(Probe.Ok(1920, 1080, 120 * 60), "Sintel.2010.1080p.WEB-DL", 120, languagePreference: null);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>A film in its own folder -- the common case -- is resolved to its own video file rather than passed over (#917).</summary>
    [Fact]
    public async Task A_movie_in_its_own_folder_is_resolved_to_its_video_file()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-integrity-folder-{Guid.NewGuid():n}")).FullName;
        try
        {
            var video = Path.Combine(folder, "Sintel.2010.1080p.WEB-DL.mkv");
            File.WriteAllText(video, "content");
            File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(Path.Combine(folder, "Sintel.2010.1080p.WEB-DL.nfo"), "nfo");

            var result = await DownloadIntegrityCheck.RunAsync(
                folder, "Sintel.2010.2160p.WEB-DL", 120, 80, Probe.Ok(1920, 1080, 120 * 60), CancellationToken.None);

            // The picture is 1080p, but the release name (used for the
            // resolution rule) says 2160p -- proving the folder's own video
            // file was actually opened and checked, not passed over.
            Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
            Assert.Contains("Name said 2160p", result.Reason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A folder with nothing recognisable yet (still unpacking) is waited for, not failed (#917).</summary>
    [Fact]
    public async Task A_folder_with_no_video_file_yet_is_waited_for()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-integrity-empty-{Guid.NewGuid():n}")).FullName;
        try
        {
            var result = await DownloadIntegrityCheck.RunAsync(
                folder, "Sintel.2010.1080p.WEB-DL", 120, 80, Probe.Ok(1920, 1080, 120 * 60), CancellationToken.None);

            Assert.Equal(DownloadIntegrityOutcome.StillWriting, result.Outcome);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A season pack folder checks each episode file (#917).</summary>
    [Fact]
    public async Task A_season_pack_folder_fails_on_any_episode_that_fails()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-integrity-pack-{Guid.NewGuid():n}")).FullName;
        try
        {
            var episode1 = Path.Combine(folder, "Show.S01E01.1080p.WEB-DL.mkv");
            var episode2 = Path.Combine(folder, "Show.S01E02.1080p.WEB-DL.mkv");
            File.WriteAllText(episode1, "bigger content, checked first");
            File.WriteAllText(episode2, "e2");
            File.SetLastWriteTimeUtc(episode1, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(episode2, DateTime.UtcNow.AddMinutes(-5));

            var okInfo = new MediaProbeInfo(
                "succeeded", "ffprobe", null, 24 * 60, "mkv", null,
                [new MediaVideoStreamInfo(0, "h264", null, 1920, 1080, "yuv420p", 24, null, null)],
                [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
                []);
            var noVideoInfo = new MediaProbeInfo(
                "succeeded", "ffprobe", null, 24 * 60, "mkv", null,
                [],
                [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
                []);
            var probe = new PerPathProbe(path => string.Equals(path, episode2, StringComparison.OrdinalIgnoreCase)
                ? noVideoInfo
                : okInfo);

            var result = await DownloadIntegrityCheck.RunAsync(
                folder, "Show.S01E01.1080p.WEB-DL", null, 80, probe, CancellationToken.None);

            Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
            Assert.Contains("No video stream", result.Reason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static MediaProbeInfo InfoOf(double minutes) => new(
        "succeeded", "ffprobe", null, minutes * 60, "mkv", null,
        [new MediaVideoStreamInfo(0, "h264", null, 1920, 1080, "yuv420p", 24, null, null)],
        [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
        []);

    private static async Task<DownloadIntegrityResult> RunInFolderAsync(
        string releaseName, int? knownRuntime, (string RelativePath, int Bytes, double Minutes)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deluno-integrity-1187-{Guid.NewGuid():n}")).FullName;
        try
        {
            var minutesByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, bytes, minutes) in files)
            {
                var full = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, new byte[bytes]);
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-5));
                minutesByPath[full] = minutes;
            }

            var probe = new PerPathProbe(path => InfoOf(minutesByPath[path]));
            return await DownloadIntegrityCheck.RunAsync(folder, releaseName, knownRuntime, 80, probe, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A sample clip is never judged, whatever its name's shape: it sits beside the film and the film alone is held to its runtime.</summary>
    [Theory]
    [InlineData("Sample/sample.mkv")]
    [InlineData("sample.mkv")]
    [InlineData("sample-Sintel.mkv")]
    [InlineData("Sintel-sample.mkv")]
    [InlineData("Sintel.sample.mkv")]
    public async Task A_sample_clip_in_the_folder_is_never_judged(string sample)
    {
        var result = await RunInFolderAsync("Sintel.2010.1080p.WEB-DL", 90,
        [
            ("Sintel.2010.1080p.WEB-DL.mkv", 5000, 90),
            (sample, 100, 1)
        ]);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task A_folder_holding_only_a_sample_clip_is_waited_for_not_failed()
    {
        var result = await RunInFolderAsync("Sintel.2010.1080p.WEB-DL", 90, [("Sample/sample.mkv", 100, 1)]);

        Assert.Equal(DownloadIntegrityOutcome.StillWriting, result.Outcome);
    }

    [Fact]
    public async Task A_file_the_probe_could_not_reach_is_not_a_verdict_on_the_file_and_passes_like_a_missing_ffprobe()
    {
        var unreachable = new Probe(new MediaProbeInfo("unreadable", "ffprobe", "The share stopped answering.", null, null, null, [], [], []));

        var result = await Run(unreachable, "Sintel.2010.1080p.WEB-DL", 120);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>A film whose own title has the word Sample in it is a film, not a sample clip: it is judged, so a short copy of it fails.</summary>
    [Fact]
    public async Task A_film_whose_title_is_the_word_Sample_is_judged_like_any_other_file()
    {
        var short_ = await RunInFolderAsync("The.Sample.2020.1080p.WEB-DL", 90, [("The.Sample.2020.1080p.WEB-DL.mkv", 5000, 5)]);
        var whole = await RunInFolderAsync("The.Sample.2020.1080p.WEB-DL", 90, [("The.Sample.2020.1080p.WEB-DL.mkv", 5000, 90)]);

        Assert.Equal(DownloadIntegrityOutcome.Failed, short_.Outcome);
        Assert.Equal(DownloadIntegrityOutcome.Pass, whole.Outcome);
    }

    /// <summary>#1187: a short extra next to the film is not held to the film's runtime.</summary>
    [Fact]
    public async Task A_film_with_a_short_extra_passes()
    {
        var result = await RunInFolderAsync("Sintel.2010.1080p.WEB-DL", 90,
        [
            ("Sintel.2010.1080p.WEB-DL.mkv", 5000, 90),
            ("Featurettes/Making.Of.mkv", 100, 6)
        ]);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>#1187: the film's own video still has to be long enough.</summary>
    [Fact]
    public async Task A_film_whose_main_video_is_too_short_still_fails()
    {
        var result = await RunInFolderAsync("Sintel.2010.1080p.WEB-DL", 90,
        [
            ("Sintel.2010.1080p.WEB-DL.mkv", 5000, 20),
            ("Featurettes/Making.Of.mkv", 100, 6)
        ]);

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("Name said 90 minutes", result.Reason);
    }

    /// <summary>#1187: a season pack is judged per episode, and its extras are left alone.</summary>
    [Fact]
    public async Task A_season_pack_judges_each_episode_and_ignores_extras()
    {
        var passing = await RunInFolderAsync("Show.S01.1080p.WEB-DL", 45,
        [
            ("Show.S01E01.1080p.mkv", 5000, 44),
            ("Show.S01E02.1080p.mkv", 4000, 46),
            ("Extras/Behind.The.Scenes.mkv", 100, 5)
        ]);
        Assert.Equal(DownloadIntegrityOutcome.Pass, passing.Outcome);

        var failing = await RunInFolderAsync("Show.S01.1080p.WEB-DL", 45,
        [
            ("Show.S01E01.1080p.mkv", 5000, 44),
            ("Show.S01E02.1080p.mkv", 4000, 10)
        ]);
        Assert.Equal(DownloadIntegrityOutcome.Failed, failing.Outcome);
        Assert.Contains("Name said 45 minutes", failing.Reason);
    }

    /// <summary>"Not the film the title says", on strong evidence: the embedded title tag names a different film (#917).</summary>
    [Fact]
    public async Task An_embedded_title_naming_a_different_film_fails()
    {
        var result = await Run(Probe.WithTitle(1920, 1080, 120 * 60, "Some Completely Different Movie"), "Sintel.2010.1080p.WEB-DL", 120, knownTitle: "Sintel");

        Assert.Equal(DownloadIntegrityOutcome.Failed, result.Outcome);
        Assert.Contains("title says", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Some Completely Different Movie", result.Reason);
    }

    /// <summary>Weak evidence -- no known title to compare against -- never fails a file (#917).</summary>
    [Fact]
    public async Task An_embedded_title_with_no_known_title_to_compare_passes()
    {
        var result = await Run(Probe.WithTitle(1920, 1080, 120 * 60, "Some Completely Different Movie"), "Sintel.2010.1080p.WEB-DL", 120, knownTitle: null);

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    /// <summary>Weak evidence -- an embedded title overlapping the known one -- never fails a file (#917).</summary>
    [Fact]
    public async Task An_embedded_title_overlapping_the_known_title_passes()
    {
        var result = await Run(Probe.WithTitle(1920, 1080, 120 * 60, "Sintel Extended Cut"), "Sintel.2010.1080p.WEB-DL", 120, knownTitle: "Sintel");

        Assert.Equal(DownloadIntegrityOutcome.Pass, result.Outcome);
    }

    private sealed class PerPathProbe(Func<string, MediaProbeInfo> byPath) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(byPath(path));
    }

    private async Task<DownloadIntegrityResult> Run(
        IMediaProbeService probe,
        string releaseName,
        int knownRuntimeMinutes,
        ProfileLanguagePreference? languagePreference = null,
        string? knownTitle = null)
        => await DownloadIntegrityCheck.RunAsync(_path, releaseName, knownRuntimeMinutes, minimumRuntimePercent: 80, probe, CancellationToken.None, languagePreference, knownTitle);

    private sealed class Probe(MediaProbeInfo info) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(info);

        public static Probe Ok(int width, int height, double durationSeconds) => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, durationSeconds, "mkv", null,
            [new MediaVideoStreamInfo(0, "h264", null, width, height, "yuv420p", 24, null, null)],
            [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
            []));

        public static Probe NoVideo(double durationSeconds) => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, durationSeconds, "mkv", null,
            [],
            [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
            []));

        public static Probe NoAudio(int width, int height, double durationSeconds) => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, durationSeconds, "mkv", null,
            [new MediaVideoStreamInfo(0, "h264", null, width, height, "yuv420p", 24, null, null)],
            [],
            []));

        public static Probe Failed(string message) => new(new MediaProbeInfo("failed", "ffprobe", message, null, null, null, [], [], []));

        public static Probe WithTitle(int width, int height, double durationSeconds, string embeddedTitle) => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, durationSeconds, "mkv", null,
            [new MediaVideoStreamInfo(0, "h264", null, width, height, "yuv420p", 24, null, null)],
            [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
            [],
            embeddedTitle));

        public static Probe Unavailable() => new(new MediaProbeInfo("unavailable", "ffprobe", "ffprobe is not installed.", null, null, null, [], [], []));
    }
}
