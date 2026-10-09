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
        Action applyAndRestart)
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
        if (!await BackUpAsync(version, noteFolder, backup))
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

    private static async Task<bool> BackUpAsync(string version, string noteFolder, Func<CancellationToken, Task> backup)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BackupTimeout);
            await backup(timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? "the backup took too long." : ex.Message;
            BlockedUpdateNote.Write(noteFolder, new BlockedUpdateNote(version, reason, DateTimeOffset.UtcNow));
            HookLog.Write($"update {version} not installed, backup failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The same "pre-update" backup the web page takes. With the host running it uses the host's own backup service;
    /// before the host runs (at start) it builds that one service on its own, over the same data folder.
    /// </summary>
    public static Task TakePreUpdateBackupAsync(IServiceProvider? hostServices, CancellationToken cancellationToken)
    {
        var service = hostServices?.GetService(typeof(IDelunoBackupService)) as IDelunoBackupService
            ?? new DelunoBackupService(
                Options.Create(new StoragePathOptions { DataRoot = AppSettings.Load().DataRoot }),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DelunoBackupService>.Instance);
        return service.CreateBackupAsync("pre-update", cancellationToken);
    }
}
