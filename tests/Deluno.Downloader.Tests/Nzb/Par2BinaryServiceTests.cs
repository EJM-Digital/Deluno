using Deluno.Downloader.Nzb.Par2;

namespace Deluno.Downloader.Tests.Nzb;

public class Par2BinaryServiceTests
{
    [Fact]
    public async Task CheckBinary_returns_not_found_when_binary_missing()
    {
        var svc = new Par2BinaryService(par2BinaryPath:
            Path.Combine(Path.GetTempPath(), $"definitely-not-a-binary-{Guid.NewGuid():N}"));

        var status = await svc.CheckBinaryAsync(CancellationToken.None);

        Assert.False(status.Found);
        Assert.NotNull(status.ErrorMessage);
        Assert.Contains("par2", status.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_with_missing_binary_returns_Failed_with_message()
    {
        var svc = new Par2BinaryService(par2BinaryPath:
            Path.Combine(Path.GetTempPath(), $"missing-par2-{Guid.NewGuid():N}"));

        var result = await svc.VerifyAsync(
            par2File: Path.Combine(Path.GetTempPath(), "movie.par2"),
            progress: null,
            ct: CancellationToken.None);

        Assert.Equal(Par2Outcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Repair_with_missing_binary_returns_failure_with_message()
    {
        var svc = new Par2BinaryService(par2BinaryPath:
            Path.Combine(Path.GetTempPath(), $"missing-par2-{Guid.NewGuid():N}"));

        var result = await svc.RepairAsync(
            par2File: Path.Combine(Path.GetTempPath(), "movie.par2"),
            progress: null,
            ct: CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.NotNull(result.Message);
    }

    // Exit-code → Par2Outcome mapping is internal; exercise it via the
    // public surface using a stub binary that returns a controlled exit.
    // We use cmd.exe / sh -c to return a specific exit code without
    // depending on a real par2 install.
    [Theory]
    [InlineData(0, Par2Outcome.Ok)]
    [InlineData(1, Par2Outcome.NeedsRepair)]
    [InlineData(2, Par2Outcome.UnrecoverableDamage)]
    [InlineData(3, Par2Outcome.MissingFiles)]
    [InlineData(99, Par2Outcome.Failed)]
    public async Task Verify_maps_exit_codes_correctly(int exitCode, Par2Outcome expected)
    {
        var (binary, scriptArgs) = MakeExitCodeStub(exitCode);
        // Build a service whose binary is the stub. The stub ignores args.
        var svc = new Par2BinaryService(binary);

        // VerifyAsync passes `verify -q <file>` to the binary — for our
        // stub that's just noise; it returns the exit code regardless.
        var result = await svc.VerifyAsync(
            par2File: "ignored.par2", progress: null, ct: CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    /// <summary>
    /// Returns a binary path + arg suffix that, when invoked,
    /// terminates with the given exit code. Used to test exit-code
    /// mapping without a real par2 install. Works on both Windows
    /// (uses cmd.exe) and Unix (uses /bin/sh).
    /// </summary>
    private static (string Binary, string Args) MakeExitCodeStub(int exit)
    {
        if (OperatingSystem.IsWindows())
        {
            // cmd.exe /c "exit N" — but Par2BinaryService uses its own
            // arg list. So we wrap by writing a tiny .bat file.
            var batPath = Path.Combine(Path.GetTempPath(), $"par2stub-{Guid.NewGuid():N}.bat");
            File.WriteAllText(batPath, $"@echo off\r\nexit /b {exit}\r\n");
            return (batPath, string.Empty);
        }
        else
        {
            var shPath = Path.Combine(Path.GetTempPath(), $"par2stub-{Guid.NewGuid():N}.sh");
            File.WriteAllText(shPath, $"#!/bin/sh\nexit {exit}\n");
            File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return (shPath, string.Empty);
        }
    }
}
