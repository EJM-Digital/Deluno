using System.Diagnostics;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>A probe that stalls (a share that hangs mid-read) is stopped at its limit and answers unreadable, with nothing left running.</summary>
[Collection("FfprobeEnvironment")]
public sealed class FfprobeStallTests : IDisposable
{
    private const string Variable = "DELUNO_FFPROBE_PATH";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-ffprobe-stall-" + Guid.NewGuid().ToString("N"));
    private readonly string? _before = Environment.GetEnvironmentVariable(Variable);
    private readonly TimeSpan _limitBefore = FfprobeMediaProbeService.ProbeLimit;

    public FfprobeStallTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, _before);
        FfprobeMediaProbeService.ProbeLimit = _limitBefore;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A stand-in ffprobe that records its own process id and then sits for a minute without printing a thing.</summary>
    private void StallingFfprobe()
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_folder, "ffprobe.cmd");
            File.WriteAllText(path, "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");
        }
        else
        {
            path = Path.Combine(_folder, "ffprobe.sh");
            File.WriteAllText(path, $"#!/bin/sh\necho $$ > \"{_folder}/pid.txt\"\nexec sleep 60\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable(Variable, path);
    }

    [Fact]
    public async Task A_probe_that_outlasts_its_limit_is_stopped_and_the_file_is_unreadable_not_bad()
    {
        var film = Path.Combine(_folder, "film.mkv");
        File.WriteAllText(film, "x");
        StallingFfprobe();
        FfprobeMediaProbeService.ProbeLimit = TimeSpan.FromSeconds(1);

        var clock = Stopwatch.StartNew();
        var info = await new FfprobeMediaProbeService().ProbeAsync(film, CancellationToken.None);
        clock.Stop();

        Assert.Equal("unreadable", info.Status);
        Assert.Equal(ProbeFailureWords.UnreadableMessage, info.Message);
        Assert.Contains("did not finish reading this file in time", info.Detail);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the probe waited {clock.Elapsed} for a stand-in that sleeps for a minute");
        Assert.Empty(info.VideoStreams);

        var pidFile = Path.Combine(_folder, "pid.txt");
        if (File.Exists(pidFile))
        {
            var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
            var gone = SpinWait.SpinUntil(() =>
            {
                try { using var left = Process.GetProcessById(pid); return left.HasExited; }
                catch (ArgumentException) { return true; }
            }, TimeSpan.FromSeconds(10));
            Assert.True(gone, "the stalled ffprobe was left running");
        }
    }
}
