using System.Diagnostics;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>A fact that is skipped, with its reason, on a machine that has no ffmpeg and ffprobe (CI's Linux leg may not).</summary>
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (TheRealFfprobeOnARealVideoTests.FindToolsFolder() is null)
        {
            Skip = "ffmpeg and ffprobe are not installed on this machine (set DELUNO_FFMPEG_DIR to a folder holding both to run it).";
        }
    }
}

/// <summary>
/// Makes a real short Matroska file with the real ffmpeg (the way the golden-path suite makes its test video: lavfi test picture and
/// tone), then runs Deluno's real probe, with the real ffprobe, over it and checks what was parsed (#1296).
/// </summary>
[Collection("FfprobeEnvironment")]
public sealed class TheRealFfprobeOnARealVideoTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-real-ffprobe-" + Guid.NewGuid().ToString("N"));

    public TheRealFfprobeOnARealVideoTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    internal static string? FindToolsFolder()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        bool Has(string? folder) => !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "ffmpeg" + exe)) && File.Exists(Path.Combine(folder, "ffprobe" + exe));
        var configured = Environment.GetEnvironmentVariable(FfmpegTools.DirectoryVariable);
        if (Has(configured)) return configured;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Has(directory)) return directory;
        }

        if (OperatingSystem.IsWindows())
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var bin in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*").SelectMany(p => Directory.EnumerateDirectories(p, "bin", SearchOption.AllDirectories)))
                {
                    if (Has(bin)) return bin;
                }
            }
        }

        return null;
    }

    [RequiresFfmpegFact]
    public async Task A_real_mkv_is_probed_for_its_container_duration_and_every_video_audio_and_subtitle_stream()
    {
        var tools = FindToolsFolder()!;
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var subtitles = Path.Combine(_folder, "spa.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nHola\n");
        var film = Path.Combine(_folder, "Real Film (2026).mkv");
        var make = new ProcessStartInfo(Path.Combine(tools, "ffmpeg" + exe)) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=2",
            "-i", subtitles,
            "-map", "0:v", "-map", "1:a", "-map", "2:s",
            "-c:v", "mpeg4", "-b:v", "300k", "-c:a", "aac", "-b:a", "64k", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:s:0", "language=spa", "-metadata:s:s:0", "title=Forced",
            "-disposition:s:0", "forced", "-metadata", "title=Real Film", film
        })
        {
            make.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(make)!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, "ffmpeg could not make the test video: " + error);
        }

        var before = Environment.GetEnvironmentVariable(FfmpegTools.DirectoryVariable);
        Environment.SetEnvironmentVariable(FfmpegTools.DirectoryVariable, tools);
        try
        {
            var info = await new FfprobeMediaProbeService().ProbeAsync(film, CancellationToken.None);

            Assert.Equal("succeeded", info.Status);
            Assert.Contains("matroska", info.Container);
            Assert.InRange(info.DurationSeconds!.Value, 1.5, 2.6);
            Assert.True(info.Bitrate > 0);
            Assert.Equal("Real Film", info.EmbeddedTitle);
            var video = Assert.Single(info.VideoStreams);
            Assert.Equal("mpeg4", video.Codec);
            Assert.Equal(320, video.Width);
            Assert.Equal(180, video.Height);
            Assert.Equal(24, video.FrameRate);
            Assert.Equal("yuv420p", video.PixelFormat);
            var audio = Assert.Single(info.AudioStreams);
            Assert.Equal("aac", audio.Codec);
            Assert.Equal(48000, audio.SampleRate);
            Assert.Equal("eng", audio.Language);
            Assert.True(audio.Channels >= 1);
            var subtitle = Assert.Single(info.SubtitleStreams);
            Assert.Equal("spa", subtitle.Language);
            Assert.True(subtitle.Forced);
            Assert.False(subtitle.HearingImpaired);
            Assert.Equal("Forced", subtitle.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FfmpegTools.DirectoryVariable, before);
        }
    }

    [RequiresFfmpegFact]
    public async Task A_real_file_that_is_not_video_is_a_failed_probe_in_plain_words_and_not_a_crash()
    {
        var tools = FindToolsFolder()!;
        var junk = Path.Combine(_folder, "junk.mkv");
        await File.WriteAllBytesAsync(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray());
        var before = Environment.GetEnvironmentVariable(FfmpegTools.DirectoryVariable);
        Environment.SetEnvironmentVariable(FfmpegTools.DirectoryVariable, tools);
        try
        {
            var info = await new FfprobeMediaProbeService().ProbeAsync(junk, CancellationToken.None);

            Assert.Equal("failed", info.Status);
            Assert.Equal(ProbeFailureWords.FailedMessage, info.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FfmpegTools.DirectoryVariable, before);
        }
    }
}
