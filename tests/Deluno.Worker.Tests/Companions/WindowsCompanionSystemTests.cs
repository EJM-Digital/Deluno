using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Deluno.Companions;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// C.14.36: the real Windows layer the companions stand on (process start, stop and detection, listening ports, the sign-in start
/// entry, running a program and reading its exit code), run against real processes, a real socket and the real registry. Each test
/// uses names of its own and puts everything back. The elevated run needs a person to answer Windows' prompt, so it is
/// proved by the golden-VM install scenario, not here. Off Windows these tests have nothing to run against.
/// </summary>
public sealed class WindowsCompanionSystemTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-companion-system-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];
    private const string Command = "example-app --minimized";
    private readonly WindowsCompanionSystem _system = new();

    public WindowsCompanionSystemTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }

        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A copy of a harmless program under a name no other process has, so stopping it can never touch anything else.</summary>
    private (string Name, string Path) UniqueSleeper()
    {
        var name = "deluno-test-" + Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(_folder, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), path);
        return (name, path);
    }

    private Process Launch(string path)
    {
        var process = Process.Start(new ProcessStartInfo(path, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        _started.Add(process);
        return process;
    }

    private static async Task<bool> BecomesAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100); // reason: polling a real process table, with a deadline
        }

        return condition();
    }

    [Fact]
    public async Task A_program_running_in_this_session_is_found_and_stopping_it_ends_every_copy_of_that_name_and_no_other()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (name, path) = UniqueSleeper();
        var (otherName, otherPath) = UniqueSleeper();
        var wanted = Launch(path);
        var other = Launch(otherPath);
        Assert.True(await BecomesAsync(() => _system.IsProcessRunning(name)));

        _system.StopProcess(name);

        Assert.True(await BecomesAsync(() => !_system.IsProcessRunning(name)));
        Assert.True(wanted.HasExited);
        Assert.False(other.HasExited, "a program with another name is left alone");
        Assert.True(_system.IsProcessRunning(otherName));
    }

    [Fact]
    public void Stopping_a_program_that_is_not_running_does_nothing_and_does_not_throw()
    {
        if (!OperatingSystem.IsWindows()) return;

        _system.StopProcess("deluno-test-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void A_port_listened_on_is_reported_in_use_and_attributed_to_the_folder_the_program_runs_from()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var current = Process.GetCurrentProcess();
        var runsFrom = Path.GetDirectoryName(current.MainModule!.FileName)!;

        Assert.True(_system.IsPortInUse(port));
        Assert.Contains(port, _system.ListeningPortsOf(runsFrom));
        Assert.DoesNotContain(port, _system.ListeningPortsOf(_folder));
        listener.Stop();
        Assert.False(_system.IsPortInUse(port));
        Assert.DoesNotContain(port, _system.ListeningPortsOf(runsFrom));
    }

    [Fact]
    public async Task Running_a_program_returns_its_exit_code_and_a_missing_one_is_an_error_not_a_decline()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = await _system.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 7", CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Declined);
        await Assert.ThrowsAnyAsync<Exception>(() => _system.RunAsync(Path.Combine(_folder, "missing.exe"), "", CancellationToken.None));
    }

    [Fact]
    public void Starting_at_sign_in_writes_a_Run_entry_for_this_account_that_holds_the_command()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "DelunoTest" + Guid.NewGuid().ToString("N")[..10];
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            _system.StartAtSignIn(name, Command);

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey)!;
            Assert.Equal(Command, key.GetValue(name));
        }
        finally
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void Text_is_written_with_its_folders_made_and_read_back_and_a_missing_file_reads_as_nothing()
    {
        var path = Path.Combine(_folder, "a", "b", "settings.ini");

        Assert.Null(_system.ReadText(path));
        Assert.False(_system.FileExists(path));
        _system.WriteText(path, "x=1");

        Assert.True(_system.FileExists(path));
        Assert.Equal("x=1", _system.ReadText(path));
    }
}
