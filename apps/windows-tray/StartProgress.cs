#pragma warning disable CS9113 // stub: removed with the implementation
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

/// <summary>Where a start has got to (#1256).</summary>
public sealed record StartProgressSnapshot(int Pid, int Attempt, StartupPhase Phase, string? Detail, DateTimeOffset AtUtc);

/// <summary>What the supervisor reads to tell a slow start from a hung one.</summary>
public interface IStartProgressSource
{
    StartProgressSnapshot Current { get; }

    /// <summary>A new try begins: back to the first phase, timestamped now.</summary>
    void BeginAttempt(int attempt);

    event Action<StartProgressSnapshot>? Changed;
}

/// <summary>The marker file's content.</summary>
public sealed record StartMarker(int Pid, int Attempt, string Phase, string? Detail, DateTimeOffset AtUtc);

public enum StartMarkerKind { Missing, Own, OtherProcess, Unreadable }

public sealed record StartMarkerRead(StartMarkerKind Kind, StartMarker? Marker);

public static class StartMarkerFile
{
    public const string FileName = "start-progress.json";

    public static void Write(string path, StartMarker marker)
    {
    }

    public static StartMarkerRead Read(string path, int ownPid) => new(StartMarkerKind.Missing, null);
}

/// <summary>How long each phase may go without progress before the start counts as hung.</summary>
public sealed class StartWindows
{
    public TimeSpan Starting { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PreUpdateBackup { get; init; } = TimeSpan.FromMinutes(12);
    public TimeSpan OpeningDatabases { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan Migrations { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan WebHost { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan For(StartupPhase phase) => phase switch
    {
        StartupPhase.PreUpdateBackup => PreUpdateBackup,
        StartupPhase.OpeningDatabases => OpeningDatabases,
        StartupPhase.Migrations => Migrations,
        StartupPhase.WebHost => WebHost,
        StartupPhase.Ready => Timeout.InfiniteTimeSpan,
        _ => Starting
    };
}

public sealed class StartProgress(TimeProvider time, string? markerPath, int pid, Action<string>? log = null)
    : IStartupProgress, IStartProgressSource
{
    private StartProgressSnapshot _current = new(pid, 1, StartupPhase.Starting, null, time.GetUtcNow());

    public StartProgressSnapshot Current => _current;

    public StartMarker? PreviousRun => null;

    public event Action<StartProgressSnapshot>? Changed;

    public static StartProgress ForThisProcess()
        => new(
            TimeProvider.System,
            Path.Combine(BlockedUpdateNote.DefaultFolder(), StartMarkerFile.FileName),
            Environment.ProcessId,
            line => HookLog.Write(line, TrayStartLog.FileName));

    public void BeginAttempt(int attempt) => _current = new(pid, attempt, StartupPhase.Starting, null, time.GetUtcNow());

    public void Enter(StartupPhase phase, string? detail = null)
    {
        _current = _current with { Phase = phase, Detail = detail, AtUtc = time.GetUtcNow() };
        Changed?.Invoke(_current);
    }
}
