using System.Diagnostics;

namespace Deluno.Tray;

internal enum RelaunchKind { None, Tray, Service }

internal static class RelaunchAfterSetup
{
    internal const string Argument = "--after-setup";
    internal static readonly TimeSpan HookBudget = TimeSpan.FromSeconds(10);

    internal static RelaunchKind Schedule(
        RunningMarks marks, string executable, Func<RunningMark, bool> isRunning, Func<bool> serviceInstalled,
        Func<int?> setupProcessId, string logPath, Action<ProcessStartInfo> start, Action<string> log)
        => throw new NotImplementedException();

    internal static bool RunWithinBudget(Action work, TimeSpan budget, TimeProvider time, Action<string> log)
        => throw new NotImplementedException();

    internal static string HelperScript(RelaunchKind kind, int? setupProcessId, string executable, string logPath)
        => throw new NotImplementedException();

    internal static bool ShouldStepAside(IEnumerable<string> args, Func<bool> anotherRuns)
        => throw new NotImplementedException();
}

internal static class ExistingInstall
{
    internal static bool Detect(AppSettingsPathState state, Func<string, bool> fileExists, Func<string, bool> folderHoldsData)
        => throw new NotImplementedException();

    /// <summary>Runs the first-run steps only when this is a first install; logs the decision either way.</summary>
    internal static bool RunFirstRunSteps(bool existingInstall, Action steps, Action<string> log)
        => throw new NotImplementedException();
}
