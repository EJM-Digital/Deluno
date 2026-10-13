using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

/// <summary>Where a start has got to (#1256): the phase, what in it, and when it last moved.</summary>
public sealed record StartProgressSnapshot(int Pid, int Attempt, StartupPhase Phase, string? Detail, DateTimeOffset AtUtc);

/// <summary>What the supervisor reads to tell a slow start from a hung one.</summary>
public interface IStartProgressSource
{
    StartProgressSnapshot Current { get; }

    /// <summary>A new try begins: back to the first phase, timestamped now.</summary>
    void BeginAttempt(int attempt);

    /// <summary>Raised after every step, on the thread that took it.</summary>
    event Action<StartProgressSnapshot>? Changed;
}

/// <summary>The marker file's content: what a process wrote just before the step it was about to take.</summary>
public sealed record StartMarker(int Pid, int Attempt, string Phase, string? Detail, DateTimeOffset AtUtc);

public enum StartMarkerKind
{
    /// <summary>There is no marker.</summary>
    Missing,

    /// <summary>This process wrote it.</summary>
    Own,

    /// <summary>Another process wrote it: a start that ended (or is still going on) elsewhere. It is never this run's progress.</summary>
    OtherProcess,

    /// <summary>The file could not be opened in time (a replace, a scanner or the disk held it). Not a verdict on its content.</summary>
    Busy,

    /// <summary>It could not be read: half written, damaged, or from a version that wrote something else.</summary>
    Unreadable
}

/// <summary>The file-system calls the marker makes, so a test can hold, deny or slow each one without racing a clock.</summary>
internal interface IMarkerFileSystem
{
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string text);
    void Move(string from, string to);
    void CreateDirectory(string path);
    void Sleep(TimeSpan span);
}

public sealed record StartMarkerRead(StartMarkerKind Kind, StartMarker? Marker);

/// <summary>
/// The start-up marker (<c>start-progress.json</c>, beside the other start-up logs). It is replaced whole (written to a
/// temporary file and renamed over the old one), so a reader sees the old marker or the new one, never half of either.
/// Whoever looks later - the next start, a person with a text editor, the golden-path suite - can read where the
/// last start stopped.
/// </summary>
public static class StartMarkerFile
{
    public const string FileName = "start-progress.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes the marker. Never throws: a marker that cannot be written must not stop a start.</summary>
    public static void Write(string path, StartMarker marker)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // A fixed temporary name: one writer at a time (the progress holds a lock).
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(marker, Json));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    // A reader has the old marker open for a moment; the rename waits its turn.
                    Thread.Sleep(10);
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    // STUB (tests first): replaced by the real seam-based versions.
    internal static string? Write(string path, StartMarker marker, IMarkerFileSystem io, bool quick = false)
    {
        Write(path, marker);
        return null;
    }

    internal static StartMarkerRead Read(string path, int ownPid, IMarkerFileSystem io) => Read(path, ownPid);

    public static StartMarkerRead Read(string path, int ownPid)
    {
        string text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new(StartMarkerKind.Missing, null);
                }

                text = File.ReadAllText(path);
                break;
            }
            catch (FileNotFoundException)
            {
                return new(StartMarkerKind.Missing, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new(StartMarkerKind.Missing, null);
            }
            catch (IOException) when (attempt < 4)
            {
                // The writer is renaming the new marker over this one; look again.
                Thread.Sleep(5);
            }
            catch
            {
                return new(StartMarkerKind.Unreadable, null);
            }
        }

        try
        {
            var marker = JsonSerializer.Deserialize<StartMarker>(text, Json);
            if (marker is null || marker.Pid <= 0 || !StartupPhases.TryParse(marker.Phase, out _) || marker.AtUtc == default)
            {
                return new(StartMarkerKind.Unreadable, null);
            }

            return new(marker.Pid == ownPid ? StartMarkerKind.Own : StartMarkerKind.OtherProcess, marker);
        }
        catch (JsonException)
        {
            return new(StartMarkerKind.Unreadable, null);
        }
    }
}

/// <summary>
/// How long each phase may go without progress before the start counts as hung (#1256). They come from what the code
/// itself can wait:
/// <list type="bullet">
/// <item>Backup: the update gate cancels a backup after 10 minutes and stops waiting 30 seconds later, so 12 minutes.</item>
/// <item>Opening the databases: one locked database is waited for up to 2 minutes (<see cref="MigrationLockRetryOptions"/>),
/// and every database reports itself, so 4 minutes without a new database.</item>
/// <item>Migrations: every migration reports itself; one migration alone can be a long data change or a VACUUM of a large
/// file (it has no time limit of its own), so 15 minutes for the longest single step.</item>
/// <item>Web host: the last services start and Kestrel binds the port; a port in use fails at once, so 3 minutes.</item>
/// <item>Starting: building the host (the dependency container and the configuration), 2 minutes.</item>
/// </list>
/// A start that keeps reporting steps is never cut off, however long it takes.
/// </summary>
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

/// <summary>
/// The start-up progress of this process. The host reports each step through <see cref="IStartupProgress"/> before it
/// takes it; this writes the marker (atomically), keeps the snapshot the supervisor watches, logs the step and tells the
/// tray. A marker another process left behind is read once, at the start, and reported as the previous run; it never
/// counts as progress of this one.
/// </summary>
public sealed class StartProgress : IStartupProgress, IStartProgressSource
{
    private readonly TimeProvider _time;
    private readonly string? _markerPath;
    private readonly int _pid;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private StartProgressSnapshot _current;

    public StartProgress(TimeProvider time, string? markerPath, int pid, Action<string>? log = null)
        : this(time, markerPath, pid, log, null)
    {
    }

    internal StartProgress(TimeProvider time, string? markerPath, int pid, Action<string>? log, IMarkerFileSystem? fileSystem)
    {
        _ = fileSystem; // STUB
        _time = time;
        _markerPath = markerPath;
        _pid = pid;
        _log = log;
        _current = new StartProgressSnapshot(pid, 1, StartupPhase.Starting, null, time.GetUtcNow());

        if (markerPath is not null)
        {
            var previous = StartMarkerFile.Read(markerPath, pid);
            if (previous is { Kind: StartMarkerKind.OtherProcess, Marker: { } marker }
                && StartupPhases.TryParse(marker.Phase, out var phase)
                && phase != StartupPhase.Ready)
            {
                PreviousRun = marker;
                var detail = string.IsNullOrWhiteSpace(marker.Detail) ? string.Empty : $" ({marker.Detail})";
                Say(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The previous start (process {marker.Pid}, try {marker.Attempt}) stopped while {StartupPhases.Words(phase)}{detail}, at {marker.AtUtc:u}. It never got to the point of listening."));
            }
        }
    }

    /// <summary>The marker another process left when it stopped before it was ready, if there was one.</summary>
    public StartMarker? PreviousRun { get; }

    public StartProgressSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after every step, on the thread that took it.</summary>
    public event Action<StartProgressSnapshot>? Changed;

    /// <summary>The progress of this process's start: its marker lives with the other start-up files, and its lines go to tray-startup.log.</summary>
    public static StartProgress ForThisProcess()
        => new(
            TimeProvider.System,
            Path.Combine(BlockedUpdateNote.DefaultFolder(), StartMarkerFile.FileName),
            Environment.ProcessId,
            line => HookLog.Write(line, TrayStartLog.FileName));

    public void BeginAttempt(int attempt)
    {
        StartProgressSnapshot snapshot;
        lock (_gate)
        {
            _current = new StartProgressSnapshot(_pid, attempt, StartupPhase.Starting, null, _time.GetUtcNow());
            snapshot = _current;
            WriteMarker(snapshot);
        }

        Say(string.Create(
            CultureInfo.InvariantCulture,
            $"Deluno {Deluno.Api.DelunoVersion.Running} is starting (process {_pid}, try {attempt})."));
        Changed?.Invoke(snapshot);
    }

    public void Enter(StartupPhase phase, string? detail = null)
    {
        StartProgressSnapshot snapshot;
        lock (_gate)
        {
            _current = _current with { Phase = phase, Detail = detail, AtUtc = _time.GetUtcNow() };
            snapshot = _current;
            // Before the step, not after: a step that never returns leaves this line as the last word.
            WriteMarker(snapshot);
        }

        // Every database and every migration reports itself; only the changes of phase are worth a line of their own.
        if (phase != StartupPhase.Migrations || detail is null || detail.EndsWith("checking the changes to apply", StringComparison.Ordinal))
        {
            var text = string.IsNullOrWhiteSpace(detail) ? StartupPhases.Words(phase) : $"{StartupPhases.Words(phase)}: {detail}";
            Say(char.ToUpperInvariant(text[0]) + text[1..] + ".");
        }

        Changed?.Invoke(snapshot);
    }

    private void WriteMarker(StartProgressSnapshot snapshot)
    {
        if (_markerPath is null)
        {
            return;
        }

        StartMarkerFile.Write(_markerPath, new StartMarker(snapshot.Pid, snapshot.Attempt, StartupPhases.Key(snapshot.Phase), snapshot.Detail, snapshot.AtUtc));
    }

    private void Say(string line)
    {
        try
        {
            _log?.Invoke(line);
        }
        catch
        {
            // A log that cannot be written must not stop a start.
        }
    }
}
