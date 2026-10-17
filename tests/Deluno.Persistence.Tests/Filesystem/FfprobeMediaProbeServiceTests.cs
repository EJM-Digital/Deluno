using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The probe reads a file through ffprobe and turns its answer into what Deluno stores. These run the real service
/// against a small stand-in program named by DELUNO_FFPROBE_PATH, which prints an ffprobe-shaped reply (the
/// "-print_format json -show_format -show_streams" document) or an error and exits with a chosen code, so every way
/// ffprobe can end is tried without needing ffprobe on the machine.
/// </summary>
[Collection("FfprobeEnvironment")]
public sealed class FfprobeMediaProbeServiceTests : IDisposable
{
    private const string Variable = "DELUNO_FFPROBE_PATH";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-ffprobe-" + Guid.NewGuid().ToString("N"));
    private readonly string? _before = Environment.GetEnvironmentVariable(Variable);

    public FfprobeMediaProbeServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, _before);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private const string Reply = """
    {
      "streams": [
        {"index":0,"codec_name":"hevc","codec_type":"video","profile":"Main 10","width":3840,"height":2160,"pix_fmt":"yuv420p10le",
         "r_frame_rate":"24000/1001","avg_frame_rate":"24000/1001","bit_rate":"15000000","color_transfer":"smpte2084",
         "side_data_list":[{"side_data_type":"Mastering display metadata"},{"side_data_type":""}],
         "tags":{"language":"eng"},"disposition":{"default":1}},
        {"index":1,"codec_name":"eac3","codec_type":"audio","profile":"Dolby Digital Plus + Dolby Atmos","channels":6,"channel_layout":"5.1(side)",
         "sample_rate":"48000","bit_rate":"768000","tags":{"language":"eng","title":"English Atmos"}},
        {"index":2,"codec_name":"subrip","codec_type":"subtitle","tags":{"language":"spa","title":"Forced"},"disposition":{"forced":1,"hearing_impaired":0}},
        {"index":3,"codec_name":"subrip","codec_type":"subtitle","disposition":{"forced":0,"hearing_impaired":1}}
      ],
      "format": {"format_name":"matroska,webm","duration":"5400.250000","bit_rate":"16000000","tags":{"title":"Sintel"}}
    }
    """;

    private string Film()
    {
        var path = Path.Combine(_folder, "film.mkv");
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>A stand-in ffprobe that prints <paramref name="stdout"/>, writes <paramref name="stderr"/> and exits with <paramref name="exitCode"/>.</summary>
    private void FakeFfprobe(string stdout, string stderr = "", int exitCode = 0)
    {
        File.WriteAllText(Path.Combine(_folder, "out.txt"), stdout);
        File.WriteAllText(Path.Combine(_folder, "err.txt"), stderr);
        string script;
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_folder, "ffprobe.cmd");
            script = $"@echo off\r\ntype \"%~dp0out.txt\"\r\ntype \"%~dp0err.txt\" 1>&2\r\nexit /b {exitCode}\r\n";
        }
        else
        {
            path = Path.Combine(_folder, "ffprobe.sh");
            script = $"#!/bin/sh\ncat \"$(dirname \"$0\")/out.txt\"\ncat \"$(dirname \"$0\")/err.txt\" >&2\nexit {exitCode}\n";
        }

        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable(Variable, path);
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_unavailable_without_running_ffprobe()
    {
        var info = await new FfprobeMediaProbeService().ProbeAsync(Path.Combine(_folder, "missing.mkv"), CancellationToken.None);

        Assert.Equal("unavailable", info.Status);
        Assert.Equal("Source file does not exist.", info.Message);
    }

    [Fact]
    public async Task A_good_reply_is_read_into_video_audio_and_subtitle_streams()
    {
        FakeFfprobe(Reply);

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("succeeded", info.Status);
        Assert.Equal(5400.25, info.DurationSeconds);
        Assert.Equal("matroska,webm", info.Container);
        Assert.Equal(16000000L, info.Bitrate);
        Assert.Equal("Sintel", info.EmbeddedTitle);

        var video = Assert.Single(info.VideoStreams);
        Assert.Equal("hevc", video.Codec);
        Assert.Equal(3840, video.Width);
        Assert.Equal(23.976, video.FrameRate);
        Assert.Equal(15000000L, video.Bitrate);
        Assert.Equal("eng", video.Language);
        Assert.Equal("smpte2084", video.ColorTransfer);
        Assert.Equal(new[] { "Mastering display metadata" }, video.SideDataTypes);

        var audio = Assert.Single(info.AudioStreams);
        Assert.Equal("eac3", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal("eng", audio.Language);

        Assert.Equal(2, info.SubtitleStreams.Count);
        var forced = info.SubtitleStreams[0];
        Assert.True(forced.Forced);
        Assert.False(forced.HearingImpaired);
        Assert.Equal("spa", forced.Language);
        Assert.Equal("Forced", forced.Title);
        var plain = info.SubtitleStreams[1];
        Assert.False(plain.Forced);
        Assert.True(plain.HearingImpaired);
        Assert.Null(plain.Language);
        Assert.Null(plain.Title);
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("")]
    [InlineData("abc/def")]
    [InlineData("30/0")]
    public async Task A_frame_rate_ffprobe_could_not_work_out_is_left_empty(string rate)
    {
        FakeFfprobe("{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"avg_frame_rate\":\"" + rate + "\"}],\"format\":{}}");

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Null(Assert.Single(info.VideoStreams).FrameRate);
    }

    [Fact]
    public async Task A_frame_rate_given_as_one_number_is_read_as_it_is()
    {
        FakeFfprobe("""{"streams":[{"index":0,"codec_type":"video","r_frame_rate":"25"}],"format":{}}""");

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal(25d, Assert.Single(info.VideoStreams).FrameRate);
        Assert.Null(info.DurationSeconds);
    }

    [Fact]
    public async Task A_reply_with_no_streams_succeeds_with_none()
    {
        FakeFfprobe("""{"format":{"format_name":"mp4"}}""");

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("succeeded", info.Status);
        Assert.Empty(info.VideoStreams);
        Assert.Empty(info.AudioStreams);
        Assert.Empty(info.SubtitleStreams);
    }

    [Fact]
    public async Task A_file_ffprobe_rejects_as_broken_fails_with_ffprobes_words_kept_as_the_detail()
    {
        FakeFfprobe("", "moov atom not found\nInvalid data found when processing input", exitCode: 1);

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("failed", info.Status);
        Assert.Equal(ProbeFailureWords.FailedMessage, info.Message);
        Assert.Contains("moov atom not found", info.Detail);
    }

    [Fact]
    public async Task A_failure_with_no_words_names_the_exit_code()
    {
        FakeFfprobe("", "", exitCode: 3);

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("failed", info.Status);
        Assert.Equal("ffprobe exited with code 3.", info.Detail);
    }

    [Theory]
    [InlineData("Input/output error")]
    [InlineData("Permission denied")]
    [InlineData("The process cannot access the file because it is being used by another process.")]
    [InlineData("Stale file handle")]
    public async Task A_file_the_system_would_not_hand_over_is_unreadable_not_broken(string error)
    {
        FakeFfprobe("", error, exitCode: 1);

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("unreadable", info.Status);
        Assert.Equal(ProbeFailureWords.UnreadableMessage, info.Message);
        Assert.Contains(error, info.Detail);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"streams\": [")]
    public async Task A_reply_that_is_not_readable_json_fails(string output)
    {
        FakeFfprobe(output);

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("failed", info.Status);
    }

    [Fact]
    public async Task A_reply_of_null_fails_as_empty()
    {
        FakeFfprobe("null");

        var info = await new FfprobeMediaProbeService().ProbeAsync(Film(), CancellationToken.None);

        Assert.Equal("failed", info.Status);
        Assert.Contains("empty or invalid", info.Detail);
    }

    [Fact]
    public async Task A_cancelled_probe_is_cancelled_not_reported_as_a_bad_file()
    {
        FakeFfprobe(Reply);
        using var cancelled = new CancellationTokenSource();
        var film = Film();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FfprobeMediaProbeService().ProbeAsync(film, cancelled.Token));
    }

    [Theory]
    [InlineData("av_read_frame: Input/output error", true)]
    [InlineData("x: permission denied", true)]
    [InlineData("The network path was not found.", true)]
    [InlineData("x: No such file or directory", true)]
    [InlineData("Invalid data found when processing input", false)]
    [InlineData("moov atom not found", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ffprobes_words_for_a_file_it_could_not_reach_are_told_from_a_file_it_could_not_read(string? error, bool unreachable)
        => Assert.Equal(unreachable, FfprobeMediaProbeService.LooksLikeTheFileCouldNotBeReached(error));
}

[CollectionDefinition("FfprobeEnvironment", DisableParallelization = true)]
public sealed class FfprobeEnvironmentCollection;
