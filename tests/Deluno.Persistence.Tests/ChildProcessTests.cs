using System.Diagnostics;
using Deluno.Infrastructure.Processes;

namespace Deluno.Persistence.Tests;

/// <summary>
/// #1258: a program Deluno starts must never be left with a full pipe nobody reads, and Deluno must never wait on it
/// without a limit. These run real programs and every wait has a limit.
/// </summary>
public sealed class ChildProcessTests
{
    /// <summary>A small script run by the platform's own shell; written to a file so no quoting rule gets between the test and the program.</summary>
    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var folder = Path.Combine(Path.GetTempPath(), "deluno-1258-scripts");
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path, "@echo off\r\n" + windows + "\r\n");
            return new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", path } };
        }

        var script = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script, unix + "\n");
        return new ProcessStartInfo("sh") { ArgumentList = { script } };
    }

    /// <summary>A 3 MB text file of 60,000 lines of one letter, made once per letter.</summary>
    private static string BigFile(char letter)
    {
        var folder = Path.Combine(Path.GetTempPath(), "deluno-1258-scripts");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"big-{letter}.txt");
        var line = new string(letter, 49);
        if (!File.Exists(path) || new FileInfo(path).Length < 60000L * 50)
        {
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(line + "\n", 60000)));
        }

        return path;
    }

    [Fact]
    public async Task Output_far_larger_than_a_pipe_holds_is_drained_and_the_program_finishes()
    {
        // About 3 MB on standard output and 3 MB on standard error: a pipe holds a few KB (Windows) or 64 KB (Linux).
        // The output is written from files in one go (type, cat), so the program's own speed never decides the test: a
        // cmd echo loop of 120,000 lines took more than the 60 s limit under the gate's parallel load (rc.30.32).
        var x = BigFile('x');
        var y = BigFile('y');
        var info = Shell($"type \"{x}\"& type \"{y}\" 1>&2", $"cat '{x}'; cat '{y}' 1>&2");

        var result = await ChildProcess.RunAsync(info, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("xxxxxxxx", result.Output);
        Assert.Contains("yyyyyyyy", result.Error);
    }

    [Fact]
    public async Task What_is_kept_is_limited_but_all_of_it_is_still_read()
    {
        var x = BigFile('x');
        var info = Shell($"type \"{x}\"", $"cat '{x}'");

        var result = await ChildProcess.RunAsync(info, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.Output.Length, 1, ChildProcess.MaxKeptCharacters + 200);
    }

    [Fact]
    public async Task A_program_that_does_not_finish_in_time_is_stopped_and_reported_as_timed_out()
    {
        var info = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var clock = Stopwatch.StartNew();

        var result = await ChildProcess.RunAsync(info, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_program_that_waits_for_input_is_not_held_up_by_it()
    {
        // Standard input is closed, so a program that reads it sees the end at once.
        var info = Shell("sort", "cat");

        var result = await ChildProcess.RunAsync(info, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_program_that_is_not_there_is_reported_as_not_started_not_thrown()
    {
        var result = await ChildProcess.RunAsync(new ProcessStartInfo("deluno-no-such-program-1258"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Cancelling_stops_the_program()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var info = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChildProcess.RunAsync(info, TimeSpan.FromSeconds(60), cancel.Token));
    }

    [Fact]
    public void The_blocking_form_has_the_same_limits()
    {
        var hang = Shell("ping -n 60 127.0.0.1 >nul", "sleep 60");
        var clock = Stopwatch.StartNew();

        var timedOut = ChildProcess.Run(hang, TimeSpan.FromSeconds(2));
        var quick = ChildProcess.Run(Shell("echo hello", "echo hello"), TimeSpan.FromSeconds(30));

        Assert.True(timedOut.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(0, quick.ExitCode);
        Assert.Contains("hello", quick.Output);
    }
}
