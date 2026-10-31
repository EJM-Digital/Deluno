using Deluno.Filesystem.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

[CollectionDefinition("ffmpeg-environment", DisableParallelization = true)]
public sealed class FfmpegEnvironmentCollection;

/// <summary>
/// The speech detector run against a stand-in for ffmpeg that prints what the real ffmpeg 9.0.2
/// printed for a six-second tone that speaks from second 1 to second 3 (a sine wave gated with
/// <c>between(t,1,3)</c> through the detector's own filter). The stand-in is a script found through
/// <c>DELUNO_FFMPEG_PATH</c>, the way an install names its own ffmpeg. #1268.
/// </summary>
[Collection("ffmpeg-environment")]
public sealed class FfmpegSpeechDetectorTests : IDisposable
{
    private const string Variable = "DELUNO_FFMPEG_PATH";

    private const string Recorded =
        "[Parsed_silencedetect_2 @ 00000136405349c0] silence_start: 0\n" +
        "[Parsed_silencedetect_2 @ 00000136405349c0] silence_end: 1.000091 | silence_duration: 1.000091\n" +
        "[Parsed_silencedetect_2 @ 00000136405349c0] silence_start: 3.003447\n" +
        "[Parsed_silencedetect_2 @ 00000136405349c0] silence_end: 6 | silence_duration: 2.996553\n";

    private readonly string folder = Directory.CreateTempSubdirectory("deluno-ffmpeg-").FullName;
    private readonly string? savedVariable = Environment.GetEnvironmentVariable(Variable);
    private readonly string? savedPath = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, savedVariable);
        Environment.SetEnvironmentVariable("PATH", savedPath);
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A stand-in that writes <paramref name="stderr"/> to its error stream and exits with <paramref name="exitCode"/>.</summary>
    private string StandIn(string stderr, int exitCode = 0, bool hang = false)
    {
        var text = Path.Combine(folder, "stderr.txt");
        File.WriteAllText(text, stderr);
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(folder, "ffmpeg-standin.cmd");
            var wait = hang ? "ping -n 30 127.0.0.1 >nul\r\n" : string.Empty;
            File.WriteAllText(path, $"@echo off\r\ntype \"{text}\" 1>&2\r\n{wait}exit /b {exitCode}\r\n");
        }
        else
        {
            path = Path.Combine(folder, "ffmpeg-standin.sh");
            var wait = hang ? "sleep 30\n" : string.Empty;
            File.WriteAllText(path, $"#!/bin/sh\ncat \"{text}\" >&2\n{wait}exit {exitCode}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable(Variable, path);
        return path;
    }

    private static FfmpegSpeechDetector Detector(TimeSpan? limit = null) => new(NullLogger<FfmpegSpeechDetector>.Instance, limit);

    private static readonly TimeSpan Six = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task The_silences_ffmpeg_reports_are_cut_out_of_the_speech()
    {
        StandIn(Recorded);

        var mask = await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None);

        Assert.NotNull(mask);
        Assert.False(mask![50]);
        Assert.True(mask[150]);
        Assert.True(mask[299]);
        Assert.False(mask[400]);
        Assert.InRange(mask.Population, 195, 205);
    }

    [Fact]
    public async Task A_silence_still_open_when_the_file_ends_runs_to_the_end()
    {
        StandIn("[Parsed_silencedetect_2 @ 0000] silence_start: 3.0\n");

        var mask = await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None);

        Assert.NotNull(mask);
        Assert.True(mask![250]);
        Assert.False(mask[400]);
    }

    [Fact]
    public async Task Lines_that_are_not_silences_or_cannot_be_read_are_passed_over()
    {
        StandIn(
            "size=N/A time=00:00:06.00 bitrate=N/A\n" +
            "[x @ 0] silence_end: 2 | silence_duration: 2\n" +
            "[x @ 0] silence_start: abc\n" +
            "[x @ 0] silence_start: -0.5\n" +
            "[x @ 0] silence_end: 1.5 | silence_duration: 2\n");

        var mask = await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None);

        Assert.NotNull(mask);
        Assert.False(mask![100]);
        Assert.True(mask[200]);
    }

    [Fact]
    public async Task Audio_with_no_silence_in_it_gives_nothing_to_align_against()
    {
        StandIn("size=N/A time=00:00:06.00 bitrate=N/A\n");

        Assert.Null(await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None));
    }

    [Fact]
    public async Task An_ffmpeg_that_fails_gives_nothing()
    {
        StandIn(Recorded, exitCode: 1);

        Assert.Null(await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None));
    }

    [Fact]
    public async Task A_video_of_no_length_is_not_listened_to()
    {
        StandIn(Recorded);

        Assert.Null(await Detector().DetectAsync("film.mkv", 1, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_ffmpeg_gives_nothing()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        Environment.SetEnvironmentVariable("DELUNO_FFMPEG_DIR", null);
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.Null(await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None));
    }

    [Fact]
    public async Task An_ffmpeg_that_cannot_be_started_gives_nothing()
    {
        var broken = Path.Combine(folder, OperatingSystem.IsWindows() ? "broken.exe" : "broken");
        File.WriteAllText(broken, "this is not a program");
        Environment.SetEnvironmentVariable(Variable, broken);

        Assert.Null(await Detector().DetectAsync("film.mkv", 1, Six, CancellationToken.None));
    }

    [Fact]
    public async Task A_listen_that_runs_past_its_limit_is_stopped_and_gives_nothing()
    {
        StandIn(Recorded, hang: true);

        var mask = await Detector(TimeSpan.FromMilliseconds(500)).DetectAsync("film.mkv", 1, Six, CancellationToken.None);

        Assert.Null(mask);
    }

    [Fact]
    public async Task A_cancelled_listen_stops_ffmpeg_and_says_so()
    {
        StandIn(Recorded, hang: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Detector().DetectAsync("film.mkv", 1, Six, cancel.Token));
    }

    [Fact]
    public void Stopping_a_process_that_is_not_there_is_not_an_error()
    {
        using var never = new System.Diagnostics.Process();

        FfmpegSpeechDetector.TryKill(never);
    }
}
