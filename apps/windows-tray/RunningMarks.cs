using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Deluno.Tray;

/// <summary>Which Deluno was running: the tray (with its server inside it), or the Windows service.</summary>
internal enum RunningKind { Tray, Service }

/// <summary>A Deluno process: which one, when it started, and the Deluno.exe it runs from.</summary>
internal readonly record struct RunningMark(int ProcessId, long StartedAtTicks, string Executable)
{
    /// <summary>This process.</summary>
    internal static RunningMark ThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        return new RunningMark(self.Id, self.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath ?? string.Empty);
    }

    /// <summary>
    /// Whether this very process, not a later one that was given its id, is still running. A mark with no start time
    /// (one read from Velopack's log, which names only the id) is asked by id alone.
    /// </summary>
    internal bool IsRunning()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return !process.HasExited && (StartedAtTicks == 0 || process.StartTime.ToUniversalTime().Ticks == StartedAtTicks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal interface IRunningMarkStore
{
    RunningMark? Read(RunningKind kind);
    void Write(RunningKind kind, RunningMark mark);
    void Remove(RunningKind kind);
}

/// <summary>
/// Whether Deluno was running when Setup stopped it (#1256). Setup stops the running Deluno before the new version's
/// install hook runs, so by then there is no process left to ask. A running Deluno sets its mark when it starts and clears
/// it when it exits in order (Quit, an update, the end of the Windows session). A mark that is still there was left by
/// something that ended Deluno without its knowing: Setup, or a crash.
///
/// The mark names the Deluno.exe it was set by, and only this install's own Deluno.exe counts for this install: a copy run
/// from a portable folder or a development build never makes a Setup start anything. The mark is volatile (see
/// <see cref="RegistryRunningMarkStore"/>), so a restart or a sign-out never leaves one for a Deluno that is not running.
/// </summary>
internal sealed class RunningMarks
{
    private readonly IRunningMarkStore _store;

    internal RunningMarks(IRunningMarkStore store) => _store = store;

    /// <summary>The marks of the real machine.</summary>
    internal static RunningMarks ForThisMachine() => new(new RegistryRunningMarkStore(RegistryRunningMarkStore.TrayKeyPath, RegistryRunningMarkStore.ServiceKeyPath));

    // The hooks call these inside a time budget. A store error is logged, never thrown: a missing mark costs Deluno a restart
    // after a silent install, and failing the install over it would cost far more.

    internal void Set(RunningKind kind, RunningMark mark)
    {
        try
        {
            _store.Write(kind, mark);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            HookLog.Write($"Could not record that Deluno is running: {ex.Message}", TrayStartLog.FileName);
        }
    }

    /// <summary>Removes the mark when it is this process's own: another that has started since has set its own.</summary>
    internal void Clear(RunningKind kind, int processId)
    {
        try
        {
            if (_store.Read(kind)?.ProcessId == processId)
            {
                _store.Remove(kind);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            HookLog.Write($"Could not clear the record that Deluno is running: {ex.Message}", TrayStartLog.FileName);
        }
    }

    /// <summary>
    /// Whether the Deluno of the install whose Deluno.exe is <paramref name="executable"/> was running and has been ended
    /// without an orderly exit. A mark set by any other Deluno.exe, or by a process that is still running, is not that and
    /// is left alone. Reading it removes it, so one mark answers one question.
    /// </summary>
    internal RunningMark? TakeLeftBy(RunningKind kind, string executable, Func<RunningMark, bool> isRunning)
    {
        try
        {
            if (_store.Read(kind) is not { } recorded || !SameInstall(recorded.Executable, executable) || isRunning(recorded))
            {
                return null;
            }

            _store.Remove(kind);
            return recorded;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            HookLog.Write($"Could not read whether Deluno was running: {ex.Message}", TrayStartLog.FileName);
            return null;
        }
    }

    /// <summary>
    /// The same Deluno.exe, or the same install: Velopack runs a hook from the new version's own folder (app-x.y.z), while
    /// a person starts Deluno from the "current" folder beside it. Both sit directly under the install folder.
    /// </summary>
    internal static bool SameInstall(string recorded, string executable)
    {
        if (recorded.Length == 0 || executable.Length == 0)
        {
            return false;
        }

        try
        {
            var a = Path.GetFullPath(recorded);
            var b = Path.GetFullPath(executable);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase)
                && IsVersionFolder(Path.GetFileName(Path.GetDirectoryName(a)))
                && IsVersionFolder(Path.GetFileName(Path.GetDirectoryName(b)))
                && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(a)), Path.GetDirectoryName(Path.GetDirectoryName(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsVersionFolder(string? name) =>
        name is not null && (name.Equals("current", StringComparison.OrdinalIgnoreCase) || name.StartsWith("app-", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The marks in the registry. Volatile keys, so they vanish with the session or the machine and never claim a Deluno that
/// is not running. They sit directly under Software because Windows makes every key created above a volatile one volatile
/// too. The tray mark is the user's; the service mark is the machine's, written by the service itself (a service run as a
/// user without the right to write there leaves no mark, and Windows' own restart-on-failure then brings it back).
/// </summary>
internal sealed class RegistryRunningMarkStore : IRunningMarkStore
{
    internal const string TrayKeyPath = @"SOFTWARE\DelunoTrayRunning";
    internal const string ServiceKeyPath = @"SOFTWARE\DelunoServiceRunning";
    private const string ProcessIdValueName = "ProcessId";
    private const string StartedAtValueName = "StartedAt";
    private const string ExecutableValueName = "Executable";

    private readonly string _trayKeyPath;
    private readonly string _serviceKeyPath;
    private readonly bool _machineForService;

    internal RegistryRunningMarkStore(string trayKeyPath, string serviceKeyPath, bool machineForService = true)
    {
        _trayKeyPath = trayKeyPath;
        _serviceKeyPath = serviceKeyPath;
        _machineForService = machineForService;
    }

    private (RegistryKey Root, string Path) Where(RunningKind kind) => kind == RunningKind.Tray
        ? (Registry.CurrentUser, _trayKeyPath)
        : (_machineForService ? Registry.LocalMachine : Registry.CurrentUser, _serviceKeyPath);

    public RunningMark? Read(RunningKind kind)
    {
        var (root, path) = Where(kind);
        using var key = root.OpenSubKey(path);
        if (key?.GetValue(ProcessIdValueName) is int processId
            && key.GetValue(StartedAtValueName) is long startedAt
            && key.GetValue(ExecutableValueName) is string executable)
        {
            return new RunningMark(processId, startedAt, executable);
        }

        return null;
    }

    public void Write(RunningKind kind, RunningMark mark)
    {
        var (root, path) = Where(kind);
        using var key = root.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
        key.SetValue(ProcessIdValueName, mark.ProcessId, RegistryValueKind.DWord);
        key.SetValue(StartedAtValueName, mark.StartedAtTicks, RegistryValueKind.QWord);
        key.SetValue(ExecutableValueName, mark.Executable, RegistryValueKind.String);
    }

    public void Remove(RunningKind kind)
    {
        var (root, path) = Where(kind);
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
