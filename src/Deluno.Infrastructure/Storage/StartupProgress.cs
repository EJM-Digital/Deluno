namespace Deluno.Infrastructure.Storage;

/// <summary>
/// The steps of starting Deluno, in the order they happen (#1256). The tray (and the Windows service) write the one
/// they are in before each step that can block, so a start that stops leaves behind how far it got, and a start that
/// has made no progress for long enough is told apart from one that is only slow.
/// </summary>
public enum StartupPhase
{
    /// <summary>The process is up and the host is being built.</summary>
    Starting = 0,

    /// <summary>The backup taken before an update is installed at start.</summary>
    PreUpdateBackup = 1,

    /// <summary>Each database file is opened and prepared.</summary>
    OpeningDatabases = 2,

    /// <summary>Each database's pending schema changes are applied.</summary>
    Migrations = 3,

    /// <summary>The databases are ready; the remaining services start and the web server begins listening.</summary>
    WebHost = 4,

    /// <summary>The web server is listening.</summary>
    Ready = 5
}

/// <summary>Where a start reports the step it is about to take. Hosts that do not supervise their start register nothing.</summary>
public interface IStartupProgress
{
    /// <summary>
    /// Called before the step that can block. <paramref name="detail"/> says which one (a database, a migration), so
    /// that moving on to the next one counts as progress.
    /// </summary>
    void Enter(StartupPhase phase, string? detail = null);
}

/// <summary>The words for each phase: a short key for logs and the marker file, and plain words for the tray.</summary>
public static class StartupPhases
{
    public static string Key(StartupPhase phase) => phase switch
    {
        StartupPhase.Starting => "starting",
        StartupPhase.PreUpdateBackup => "backup",
        StartupPhase.OpeningDatabases => "databases",
        StartupPhase.Migrations => "migrations",
        StartupPhase.WebHost => "web-host",
        StartupPhase.Ready => "ready",
        _ => phase.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string? key, out StartupPhase phase)
    {
        foreach (var candidate in Enum.GetValues<StartupPhase>())
        {
            if (string.Equals(Key(candidate), key, StringComparison.Ordinal))
            {
                phase = candidate;
                return true;
            }
        }

        phase = default;
        return false;
    }

    /// <summary>Everyday words for the tray's status line and the logs.</summary>
    public static string Words(StartupPhase phase) => phase switch
    {
        StartupPhase.Starting => "starting",
        StartupPhase.PreUpdateBackup => "backing up your data before the update",
        StartupPhase.OpeningDatabases => "opening the databases",
        StartupPhase.Migrations => "updating the databases",
        StartupPhase.WebHost => "starting the web server",
        StartupPhase.Ready => "ready",
        _ => phase.ToString()
    };
}
