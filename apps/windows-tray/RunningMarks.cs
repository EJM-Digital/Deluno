namespace Deluno.Tray;

/// <summary>Which Deluno was running: the tray (with its server inside it), or the Windows service.</summary>
internal enum RunningKind { Tray, Service }

/// <summary>A Deluno process: which one, when it started, and the Deluno.exe it runs from.</summary>
internal readonly record struct RunningMark(int ProcessId, long StartedAtTicks, string Executable);

internal interface IRunningMarkStore
{
    RunningMark? Read(RunningKind kind);
    void Write(RunningKind kind, RunningMark mark);
    void Remove(RunningKind kind);
}

internal sealed class RunningMarks
{
    private readonly IRunningMarkStore _store;
    internal RunningMarks(IRunningMarkStore store) => _store = store;
    internal void Set(RunningKind kind, RunningMark mark) => _ = _store;
    internal void Clear(RunningKind kind, int processId) => _ = _store;
    internal bool TakeLeftBy(RunningKind kind, string executable, Func<RunningMark, bool> isRunning) => false;
}

internal sealed class RegistryRunningMarkStore : IRunningMarkStore
{
    internal RegistryRunningMarkStore(string trayKeyPath, string serviceKeyPath, bool machineForService = true) { }
    public RunningMark? Read(RunningKind kind) => null;
    public void Write(RunningKind kind, RunningMark mark) { }
    public void Remove(RunningKind kind) { }
}
