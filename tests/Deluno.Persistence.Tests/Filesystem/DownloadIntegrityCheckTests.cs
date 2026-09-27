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
        Assert.Contains("1920 pixels wide", result.Reason);
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

    private async Task<DownloadIntegrityResult> Run(
        IMediaProbeService probe,
        string releaseName,
        int knownRuntimeMinutes,
        ProfileLanguagePreference? languagePreference = null)
        => await DownloadIntegrityCheck.RunAsync(_path, releaseName, knownRuntimeMinutes, minimumRuntimePercent: 80, probe, CancellationToken.None, languagePreference);

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

        public static Probe Unavailable() => new(new MediaProbeInfo("unavailable", "ffprobe", "ffprobe is not installed.", null, null, null, [], [], []));
    }
}
