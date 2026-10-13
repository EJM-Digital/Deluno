using System.Text.Json;
using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Tray;

/// <summary>
/// What an automatic install (on Quit, or at the next start) left behind when a failed backup stopped it, so the next
/// start can say so in Activity and as a tray notice (#1197).
/// </summary>
public sealed record BlockedUpdateNote(string Version, string Reason, DateTimeOffset AtUtc)
{
    public const string FileName = "update-blocked-note.json";

    /// <summary>Where the note lives: the same per-user folder as the other start-up markers.</summary>
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deluno", "logs");

    /// <summary>The plain words for Activity and the tray notice.</summary>
    public string Message =>
        $"The update to {Version} wasn't installed because Deluno couldn't back up its data first: {Reason}";

    public static void Write(string folder, BlockedUpdateNote note)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(note));
        }
        catch
        {
            // Best effort: the update simply stays pending and is tried again.
        }
    }

    /// <summary>Reads the note and removes it, so it is said once. Null when there is none.</summary>
    public static BlockedUpdateNote? TryTake(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var note = JsonSerializer.Deserialize<BlockedUpdateNote>(File.ReadAllText(path));
            File.Delete(path);
            return note;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The rule for installing a downloaded update without anyone pressing a button: take the same "pre-update" backup
/// Restart and apply takes first, and when it fails do not install (#1197).
/// </summary>
public static class PreUpdateBackupGate
{
    public static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long after the time limit a backup that ignores it is still waited for, before the update is stopped (#1256).</summary>
    public static readonly TimeSpan HardLimitGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// On Quit: backs up while the host is still running, then queues the install. Returns true when the install was
    /// queued; false when the backup failed (a note is left for the next start) and Deluno should just quit.
    /// </summary>
    public static async Task<bool> InstallOnQuitAsync(
        string version,
        string noteFolder,
        Func<CancellationToken, Task> backup,
        Action queueInstall)
    {
        if (!await BackUpAsync(version, noteFolder, backup))
        {
            return false;
        }

        queueInstall();
        return true;
    }

    /// <summary>
    /// At start, before the host runs: tried once per version so an update that cannot apply never loops; backs up,
    /// then installs. Returns true when the app is restarting into the new version; false means start normally.
    /// </summary>
    public static async Task<bool> InstallAtStartAsync(
        string version,
        string attemptFolder,
        string noteFolder,
        Func<CancellationToken, Task> backup,
        Action applyAndRestart,
        Deluno.Infrastructure.Storage.IStartupProgress? progress = null,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        var marker = Path.Combine(attemptFolder, "update-start-attempt.txt");
        try
        {
            if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), version, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        // A blocked install is not an attempt: the next start tries again.
        if (!await BackUpAsync(version, noteFolder, backup, progress, time, log))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(attemptFolder);
            File.WriteAllText(marker, version);
        }
        catch
        {
            return false;
        }

        applyAndRestart();
        return true;
    }

    private static async Task<bool> BackUpAsync(
        string version,
        string noteFolder,
        Func<CancellationToken, Task> backup,
        Deluno.Infrastructure.Storage.IStartupProgress? progress = null,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        var clock = time ?? TimeProvider.System;
        var began = clock.GetUtcNow();
        try
        {
            // Before the step that can block (#1256): the log, the marker and the tray all say what is going on.
            progress?.Enter(Deluno.Infrastructure.Storage.StartupPhase.PreUpdateBackup, $"backing up before installing {version}");
            log?.Invoke($"Backing up Deluno's data before installing version {version} (it is cancelled after {BackupTimeout.TotalMinutes:0} minutes).");
            using var timeout = new CancellationTokenSource(BackupTimeout, clock);
            // A backup that is stuck in a read does not look at its token; the wait ends anyway, a little after the limit.
            await Task.Run(() => backup(timeout.Token)).WaitAsync(BackupTimeout + HardLimitGrace, clock).ConfigureAwait(false);
            log?.Invoke($"The backup finished in {(clock.GetUtcNow() - began).TotalSeconds:0} seconds.");
            return true;
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException or TimeoutException ? "the backup took too long." : ex.Message;
            BlockedUpdateNote.Write(noteFolder, new BlockedUpdateNote(version, reason, clock.GetUtcNow()));
            HookLog.Write($"update {version} not installed, backup failed: {ex.Message}");
            log?.Invoke($"The update to {version} was not installed because the backup failed: {reason}");
            return false;
        }
    }

    /// <summary>
    /// The same "pre-update" backup the web page takes. With the host running it uses the host's own backup service;
    /// before the host runs (at start) it builds that one service on its own, over the same data folder.
    /// </summary>
    public static Task TakePreUpdateBackupAsync(IServiceProvider? hostServices, CancellationToken cancellationToken, Action<string>? onFile = null)
    {
        if (hostServices?.GetService(typeof(IDelunoBackupService)) is IDelunoBackupService running)
        {
            return running.CreateBackupAsync("pre-update", cancellationToken);
        }

        var service = new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = AppSettings.Load().DataRoot }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DelunoBackupService>.Instance);
        return service.CreateBackupAsync("pre-update", cancellationToken, onFile);
    }
}
