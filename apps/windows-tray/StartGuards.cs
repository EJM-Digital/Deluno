#pragma warning disable CS9113 // stub: removed with the implementation
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

public static class StartHangWatch
{
    public static Task<StartProgressSnapshot?> WaitForHangAsync(IStartProgressSource source, StartWindows windows, TimeProvider time, CancellationToken stop)
        => Task.FromResult<StartProgressSnapshot?>(null);
}

public interface IProcessLauncher
{
    void Launch(string exePath, string arguments);
}

public sealed class ProcessRelaunch(string guardFolder, TimeProvider time, IProcessLauncher launcher, int pid, string? exePath, ISupervisorLog log)
    : IProcessRestarter
{
    public const string AfterRestartArgument = "--after-restart";
    public const string GuardFileName = "restart-guard.json";
    public static readonly TimeSpan GuardWindow = TimeSpan.FromMinutes(30);

    public bool TryRestart(string reason) => false;

    public static int? ParentToWaitFor(string[] args) => null;
}

public static class ServiceStartGuard
{
    public const int FailedExitCode = 1;
    public const int HungExitCode = 3;

    public static Task WatchAsync(IStartProgressSource progress, StartWindows windows, TimeProvider time, ISupervisorLog log, Func<string> diagnostics, Action<int> exit, CancellationToken stop)
        => Task.CompletedTask;

    public static void OnStartFailed(Exception failure, ISupervisorLog log, Action<int> exit)
    {
    }
}
