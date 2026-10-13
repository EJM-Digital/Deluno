using System.Text.Json;
using Deluno.Api.Backup;
using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.StateFiles;

/// <summary>
/// #1260, file by file: for each of Deluno's own state files that another process also touches, a busy file (another handle
/// open, or a delete pending), a cut-short one and a missing one, and two writers at once. A change either lands on the
/// file's true contents or does not happen and says so; the file is never replaced from a guess.
/// </summary>
public sealed class DelunoStateFilesNeverTurnBusyIntoEmptyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-state-files-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedStateFileSystem _io = new();

    public DelunoStateFilesNeverTurnBusyIntoEmptyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Exception? Busy(string path, int attempt) => ScriptedStateFileSystem.SharingViolation();

    private static Exception? DeletePending(string path, int attempt) => ScriptedStateFileSystem.AccessDenied();

    // ---------------------------------------------------------------- secrets/component-credentials.json

    private FileBundledComponentCredentialStore Credentials(IStateFileSystem? io = null)
        => new(FileBundledComponentCredentialStore.PathFor(_root), new ReversingProtector(), io);

    [Fact]
    public void Credentials_a_busy_file_never_loses_the_other_apps_sign_ins_when_one_is_saved()
    {
        var store = Credentials();
        store.Save("nzbget", new BundledComponentCredentials("deluno", "one"));
        var before = File.ReadAllText(FileBundledComponentCredentialStore.PathFor(_root));
        _io.ReadFault = Busy;
        var busy = Credentials(_io);

        var failure = Assert.Throws<StateFileException>(() => busy.Save("qbittorrent", new BundledComponentCredentials("deluno", "two")));

        Assert.Contains("in use", failure.Message);
        Assert.Equal(before, File.ReadAllText(FileBundledComponentCredentialStore.PathFor(_root)));
        Assert.Equal(StateFileKind.Busy, busy.Availability());
        _io.ReadFault = null;
        Assert.Equal("one", Credentials().TryGet("nzbget")!.Password);
    }

    [Fact]
    public void Credentials_a_file_with_a_delete_pending_is_busy_not_empty_on_remove_too()
    {
        var store = Credentials();
        store.Save("nzbget", new BundledComponentCredentials("deluno", "one"));
        _io.ReadFault = DeletePending;

        Assert.Throws<StateFileException>(() => Credentials(_io).Remove("nzbget"));

        _io.ReadFault = null;
        Assert.True(Credentials().Has("nzbget"));
    }

    [Fact]
    public void Credentials_a_file_held_by_another_handle_with_no_sharing_is_busy_and_unchanged()
    {
        Credentials().Save("nzbget", new BundledComponentCredentials("deluno", "one"));
        var path = FileBundledComponentCredentialStore.PathFor(_root);
        var before = File.ReadAllText(path);
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<StateFileException>(() => Credentials(_io).Save("qbittorrent", new BundledComponentCredentials("deluno", "two")));
        Assert.Null(Credentials(_io).TryGet("nzbget"));
        Assert.Equal(StateFileKind.Busy, Credentials(_io).Availability());

        other.Dispose();
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Credentials_a_cut_short_file_is_kept_and_not_written_over()
    {
        var path = FileBundledComponentCredentialStore.PathFor(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"nzbget\":{\"Username\":\"deluno\",\"Pass");
        _io.Now = new DateTimeOffset(2026, 10, 14, 11, 0, 0, TimeSpan.Zero);

        var failure = Assert.Throws<StateFileException>(() => Credentials(_io).Save("qbittorrent", new BundledComponentCredentials("deluno", "two")));

        Assert.Contains("damaged", failure.Message);
        Assert.Equal("{\"nzbget\":{\"Username\":\"deluno\",\"Pass", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "component-credentials.json.unreadable-*"));
        Assert.Equal(StateFileKind.Unreadable, Credentials(_io).Availability());
    }

    [Fact]
    public void Credentials_a_missing_file_is_a_first_save()
    {
        var store = Credentials(_io);
        Assert.Equal(StateFileKind.Missing, store.Availability());

        store.Save("nzbget", new BundledComponentCredentials("deluno", "one"));

        Assert.Equal("one", Credentials().TryGet("nzbget")!.Password);
    }

    [Fact]
    public async Task Credentials_the_tray_and_the_server_saving_at_once_keep_both_sign_ins()
    {
        // Two stores on the same file with nothing shared in process: what the tray and the service are.
        var tray = Task.Run(() => { for (var i = 0; i < 15; i++) { Credentials().Save($"tray{i}", new BundledComponentCredentials("u", "p" + i)); } });
        var server = Task.Run(() => { for (var i = 0; i < 15; i++) { Credentials().Save($"server{i}", new BundledComponentCredentials("u", "p" + i)); } });
        await Task.WhenAll(tray, server);

        Assert.Equal(30, Credentials().Kinds().Count);
    }

    // ---------------------------------------------------------------- config/installed-components.json

    private FileInstalledComponentRecord Record(IStateFileSystem? io = null)
        => new(FileInstalledComponentRecord.PathFor(_root), io: io);

    [Fact]
    public void Installed_apps_a_busy_file_does_not_forget_the_others_when_one_is_marked()
    {
        Record().MarkInstalled("nzbget", "localhost", 6789);
        var path = FileInstalledComponentRecord.PathFor(_root);
        var before = File.ReadAllText(path);
        _io.ReadFault = Busy;

        Assert.Throws<StateFileException>(() => Record(_io).MarkInstalled("qbittorrent", "localhost", 8081));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(StateFileKind.Busy, Record(_io).ReadChecked().Kind);
    }

    [Fact]
    public void Installed_apps_a_delete_pending_file_is_busy_and_marking_wired_changes_nothing()
    {
        Record().MarkInstalled("nzbget", "localhost", 6789);
        _io.ReadFault = DeletePending;

        Assert.Throws<StateFileException>(() => Record(_io).MarkWired("nzbget"));

        _io.ReadFault = null;
        Assert.Null(Record().Read().Single().WiredUtc);
    }

    [Fact]
    public void Installed_apps_a_cut_short_file_is_kept_unchanged_and_reads_as_unreadable()
    {
        var path = FileInstalledComponentRecord.PathFor(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[{\"Kind\":\"nzbget\",\"Hos");

        Assert.Throws<StateFileException>(() => Record(_io).MarkInstalled("qbittorrent", "localhost", 8081));

        Assert.Equal("[{\"Kind\":\"nzbget\",\"Hos", File.ReadAllText(path));
        Assert.Equal(StateFileKind.Unreadable, Record(_io).ReadChecked().Kind);
    }

    [Fact]
    public void Installed_apps_a_missing_file_reads_as_none_and_the_first_mark_creates_it()
    {
        var record = Record(_io);
        Assert.Equal(StateFileKind.Missing, record.ReadChecked().Kind);
        Assert.Empty(record.Read());

        record.MarkInstalled("nzbget", "localhost", 6789);

        Assert.Equal("nzbget", record.Read().Single().Kind);
    }

    [Fact]
    public async Task Installed_apps_the_tray_marking_while_the_server_marks_wired_loses_neither()
    {
        Record().MarkInstalled("nzbget", "localhost", 6789);
        var tray = Task.Run(() => { for (var i = 0; i < 15; i++) { Record().MarkInstalled($"app{i}", "localhost", 1000 + i); } });
        var server = Task.Run(() => { for (var i = 0; i < 15; i++) { Record().MarkWired("nzbget"); } });
        await Task.WhenAll(tray, server);

        var entries = Record().Read();
        Assert.Equal(16, entries.Count);
        Assert.NotNull(entries.Single(entry => entry.Kind == "nzbget").WiredUtc);
    }

    // ---------------------------------------------------------------- config/pending-component-installs.json

    [Fact]
    public void Pending_installs_a_busy_file_drops_nothing_and_the_ask_is_not_accepted()
    {
        PendingComponentInstalls.Add(_root, ["nzbget"], null, askedByPerson: false, reason: "limit");
        var path = PendingComponentInstalls.PathFor(_root);
        var before = File.ReadAllText(path);
        _io.ReadFault = Busy;

        var failure = Assert.Throws<StateFileException>(() => PendingComponentInstalls.Add(_root, ["weir"], null, askedByPerson: true, reason: "asked", io: _io));

        Assert.Contains("in use", failure.Message);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(StateFileKind.Busy, PendingComponentInstalls.ReadChecked(_root, _io).Kind);
        Assert.Null(PendingComponentInstalls.Read(_root, _io));
    }

    [Fact]
    public void Pending_installs_a_cut_short_file_is_not_written_over_by_an_add_a_remove_or_a_mark()
    {
        var path = PendingComponentInstalls.PathFor(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Kinds\":[\"nzbget\",\"we");

        Assert.Throws<StateFileException>(() => PendingComponentInstalls.Add(_root, ["weir"], null, true, "asked", _io));
        Assert.Throws<StateFileException>(() => PendingComponentInstalls.Remove(_root, ["nzbget"], _io));
        Assert.Throws<StateFileException>(() => PendingComponentInstalls.MarkAsked(_root, _io));

        Assert.Equal("{\"Kinds\":[\"nzbget\",\"we", File.ReadAllText(path));
        Assert.Equal(StateFileKind.Unreadable, PendingComponentInstalls.ReadChecked(_root, _io).Kind);
    }

    [Fact]
    public void Pending_installs_a_missing_file_is_an_empty_list_and_the_last_removal_deletes_the_file()
    {
        Assert.Equal(StateFileKind.Missing, PendingComponentInstalls.ReadChecked(_root, _io).Kind);
        PendingComponentInstalls.Add(_root, ["nzbget"], null, false, "limit", _io);
        Assert.Equal(["nzbget"], PendingComponentInstalls.Read(_root, _io)!.Kinds);

        PendingComponentInstalls.Remove(_root, ["nzbget"], _io);

        Assert.False(File.Exists(PendingComponentInstalls.PathFor(_root)));
    }

    [Fact]
    public async Task Pending_installs_the_server_asking_while_the_tray_removes_loses_neither()
    {
        PendingComponentInstalls.Add(_root, ["nzbget", "qbittorrent"], null, false, "limit");
        var server = Task.Run(() => { for (var i = 0; i < 10; i++) { PendingComponentInstalls.Add(_root, [$"asked{i}"], null, askedByPerson: true, reason: "asked"); } });
        var tray = Task.Run(() => PendingComponentInstalls.Remove(_root, ["nzbget"]));
        await Task.WhenAll(server, tray);

        var kinds = PendingComponentInstalls.Read(_root)!.Kinds;
        Assert.DoesNotContain("nzbget", kinds);
        Assert.Contains("qbittorrent", kinds);
        Assert.Equal(10, kinds.Count(kind => kind.StartsWith("asked", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- database-maintenance.json

    [Fact]
    public void Maintenance_a_busy_note_keeps_the_fields_that_were_not_changed()
    {
        DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), true, null);
        var path = Path.Combine(_root, DatabaseMaintenanceLog.FileName);
        var before = File.ReadAllText(path);
        _io.ReadFault = Busy;

        var problem = DatabaseMaintenanceLog.RecordCompaction(_root, DateTimeOffset.Parse("2026-10-02T00:00:00Z"), _io);

        Assert.NotNull(problem);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Maintenance_a_cut_short_note_has_a_copy_kept_and_starts_again_with_the_new_field()
    {
        var path = Path.Combine(_root, DatabaseMaintenanceLog.FileName);
        File.WriteAllText(path, "{\"integrityCheckedUtc\":\"2026-10-01T00:00:00+00:00\",\"integ");

        var problem = DatabaseMaintenanceLog.RecordCompaction(_root, DateTimeOffset.Parse("2026-10-02T00:00:00Z"), _io);

        Assert.Null(problem);
        Assert.Single(Directory.GetFiles(_root, DatabaseMaintenanceLog.FileName + ".unreadable-*"));
        Assert.NotNull(DatabaseMaintenanceLog.Read(_root, _io).CompactedUtc);
    }

    [Fact]
    public async Task Maintenance_an_integrity_note_and_a_compaction_note_at_once_keep_both()
    {
        var a = Task.Run(() => DatabaseMaintenanceLog.RecordIntegrity(_root, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), true, null));
        var b = Task.Run(() => DatabaseMaintenanceLog.RecordCompaction(_root, DateTimeOffset.Parse("2026-10-02T00:00:00Z")));
        await Task.WhenAll(a, b);

        var note = DatabaseMaintenanceLog.Read(_root);
        Assert.NotNull(note.IntegrityCheckedUtc);
        Assert.NotNull(note.CompactedUtc);
    }

    // ---------------------------------------------------------------- backups/backup-settings.json

    private string BackupSettingsPath => Path.Combine(_root, "backups", "backup-settings.json");

    private DelunoBackupService Backups(IStateFileSystem? io = null)
    {
        File.WriteAllText(Path.Combine(_root, "platform.json"), "{}");
        return new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = _root }),
            new FixedClock(DateTimeOffset.Parse("2026-10-14T09:30:00Z")),
            NullLogger<DelunoBackupService>.Instance,
            stateFiles: io);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Backup_settings_a_cut_short_file_still_lets_a_backup_run_says_so_and_is_not_written_over()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupSettingsPath)!);
        File.WriteAllText(BackupSettingsPath, "{\"enabled\":tr");
        var service = Backups(_io);

        var backup = await service.CreateBackupAsync("pre-update", CancellationToken.None);
        var settings = await service.GetSettingsAsync(CancellationToken.None);

        Assert.True(File.Exists(backup.FullPath));
        Assert.Equal("{\"enabled\":tr", File.ReadAllText(BackupSettingsPath));
        Assert.NotNull(settings.Problem);
        Assert.Contains("damaged", settings.Problem);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(BackupSettingsPath)!, "backup-settings.json.unreadable-*"));
    }

    [Fact]
    public async Task Backup_settings_a_busy_file_still_lets_a_backup_run_and_is_neither_changed_nor_defaulted()
    {
        var service = Backups();
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "weekly", "04:00", 12, null), CancellationToken.None);
        var before = File.ReadAllText(BackupSettingsPath);
        _io.ReadFault = (path, _) => path.EndsWith("backup-settings.json", StringComparison.Ordinal) ? ScriptedStateFileSystem.SharingViolation() : null;
        var busy = Backups(_io);

        var backup = await busy.CreateBackupAsync("pre-update", CancellationToken.None);
        var settings = await busy.GetSettingsAsync(CancellationToken.None);

        Assert.True(File.Exists(backup.FullPath));
        Assert.Equal(before, File.ReadAllText(BackupSettingsPath));
        Assert.Contains("in use", settings.Problem);
        await Assert.ThrowsAsync<PlainFailureException>(() => busy.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, null), CancellationToken.None));
        Assert.Equal(before, File.ReadAllText(BackupSettingsPath));
    }

    [Fact]
    public async Task Backup_settings_saving_again_after_damage_repairs_the_file_and_clears_the_problem()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupSettingsPath)!);
        File.WriteAllText(BackupSettingsPath, "{\"enabled\":tr");
        var service = Backups();

        var saved = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "weekly", "04:00", 12, null), CancellationToken.None);

        Assert.Null(saved.Problem);
        Assert.Equal(12, (await service.GetSettingsAsync(CancellationToken.None)).RetentionCount);
        Assert.Null((await service.GetSettingsAsync(CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task Backup_settings_a_change_made_while_a_backup_runs_waits_for_it_and_is_not_reverted_by_it()
    {
        var service = Backups();
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "daily", "03:00", 7, null), CancellationToken.None);
        Task<BackupSettingsSnapshot>? change = null;
        var startedDuringBackup = false;

        await service.CreateBackupAsync("manual", CancellationToken.None, onFile: _ =>
        {
            if (change is not null)
            {
                return;
            }

            // The backup holds its gate from here until it ends. A settings change asked for now must wait behind it.
            change = service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "weekly", "05:00", 21, null), CancellationToken.None);
            startedDuringBackup = !change.IsCompleted;
        });
        var saved = await change!;
        var after = await service.GetSettingsAsync(CancellationToken.None);

        Assert.True(startedDuringBackup, "the change must wait for the running backup");
        Assert.Equal("weekly", saved.Frequency);
        Assert.Equal(21, after.RetentionCount);
        Assert.Equal("weekly", after.Frequency);
        Assert.Equal("05:00", after.TimeOfDay);
    }

    [Fact]
    public async Task Backup_settings_a_change_made_by_another_instance_during_a_backup_survives_the_run_times_being_recorded()
    {
        var running = Backups();
        await running.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "daily", "03:00", 7, null), CancellationToken.None);
        var other = Backups();

        await running.CreateBackupAsync("manual", CancellationToken.None, onFile: _ =>
        {
            // A second instance (the tray's own pre-update service) changes the file while this backup is partway through.
            other.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "monthly", "06:00", 30, null), CancellationToken.None).GetAwaiter().GetResult();
        });

        var after = await running.GetSettingsAsync(CancellationToken.None);
        Assert.Equal("monthly", after.Frequency);
        Assert.Equal(30, after.RetentionCount);
        Assert.NotNull(after.LastRunUtc);
    }

    [Fact]
    public async Task Backup_settings_a_missing_file_is_a_first_run_with_the_standard_settings()
    {
        var settings = await Backups(_io).GetSettingsAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
        Assert.Equal(7, settings.RetentionCount);
        Assert.Null(settings.Problem);
        Assert.True(File.Exists(BackupSettingsPath));
    }

    private sealed class ReversingProtector : Deluno.Security.ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => "rev:" + purpose + ":" + new string(plaintext.Reverse().ToArray());

        public string? Unprotect(string purpose, string? protectedValue)
            => protectedValue is not null && protectedValue.StartsWith("rev:" + purpose + ":", StringComparison.Ordinal)
                ? new string(protectedValue[("rev:" + purpose + ":").Length..].Reverse().ToArray())
                : null;

        public bool IsProtected(string? value) => value?.StartsWith("rev:", StringComparison.Ordinal) == true;
    }
}
