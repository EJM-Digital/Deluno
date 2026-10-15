using System.IO.Compression;
using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The backup refusals, on real temp folders: a file in the backup folder that Deluno did not make is never listed as
/// a backup nor removed to tidy old ones, a backup id that names nothing is refused, damaged or busy settings never
/// stop a backup or get overwritten, and a file date a zip cannot hold does not lose the file.
/// </summary>
public sealed class ABackupRefusesWhatItCannotTrustTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deluno-backup-refuse", Guid.NewGuid().ToString("N"));

    public ABackupRefusesWhatItCannotTrustTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(root, "backups", "backup-settings.json");

    private DelunoBackupService NewService(string utcNow = "2026-10-14T01:00:00Z")
        => new(
            Options.Create(new StoragePathOptions { DataRoot = root }),
            new FixedTime(DateTimeOffset.Parse(utcNow)),
            NullLogger<DelunoBackupService>.Instance);

    private static UpdateBackupSettingsRequest Settings(bool enabled, int retention = 7)
        => new(Enabled: enabled, Frequency: "daily", TimeOfDay: "02:00", RetentionCount: retention, BackupFolder: null);

    [Fact]
    public async Task A_backup_id_that_names_nothing_is_refused_and_nothing_is_removed()
    {
        var service = NewService();
        await service.GetSettingsAsync(CancellationToken.None);
        var keep = Path.Combine(root, "backups", "keep.zip");
        File.WriteAllText(keep, "not ours");

        Assert.Null(await service.OpenBackupAsync(".zip", CancellationToken.None));
        Assert.Null(await service.OpenBackupAsync("", CancellationToken.None));
        Assert.False(await service.DeleteBackupAsync(".zip", CancellationToken.None));
        Assert.False(await service.DeleteBackupAsync("missing", CancellationToken.None));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task A_zip_that_is_not_a_backup_is_not_listed_and_is_never_removed_to_tidy_old_backups()
    {
        var service = NewService();
        await service.SaveSettingsAsync(Settings(enabled: false, retention: 1), CancellationToken.None);
        var folder = Path.Combine(root, "backups");
        var damaged = Path.Combine(folder, "damaged.zip");
        File.WriteAllText(damaged, "this is not a zip file");
        var foreign = Path.Combine(folder, "foreign.zip");
        using (var archive = ZipFile.Open(foreign, ZipArchiveMode.Create))
        {
            archive.CreateEntry("notes.txt");
        }

        File.WriteAllText(Path.Combine(root, "platform.db"), "x");
        var first = await service.CreateBackupAsync("manual", CancellationToken.None);
        var second = await NewService("2026-10-14T02:00:00Z").CreateBackupAsync("manual", CancellationToken.None);

        var listed = await service.ListBackupsAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, item => item.FileName is "damaged.zip");
        Assert.Contains(listed, item => item.FileName == "foreign.zip");
        Assert.True(File.Exists(damaged));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(first.FullPath), "retention keeps one of Deluno's own backups");
        Assert.True(File.Exists(second.FullPath));
        var settings = await service.GetSettingsAsync(CancellationToken.None);
        Assert.Contains("did not make", settings.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damaged_settings_never_stop_a_backup_and_are_never_written_over_by_it()
    {
        var service = NewService();
        await service.GetSettingsAsync(CancellationToken.None);
        File.WriteAllText(SettingsFile, "{ this is not json");
        File.WriteAllText(Path.Combine(root, "platform.db"), "x");

        var backup = await service.CreateBackupAsync("manual", CancellationToken.None);

        Assert.True(File.Exists(backup.FullPath));
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsFile));
        var settings = await service.GetSettingsAsync(CancellationToken.None);
        Assert.False(settings.Enabled);
        Assert.Contains("damaged", settings.Problem ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_that_cannot_be_recorded_do_not_undo_a_backup_that_was_taken()
    {
        var service = NewService();
        await service.SaveSettingsAsync(Settings(enabled: true), CancellationToken.None);
        File.WriteAllText(Path.Combine(root, "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "b.json"), "{}");

        // Between the start of the backup and its end the settings file is damaged by something else.
        var backup = await service.CreateBackupAsync(
            "manual",
            CancellationToken.None,
            _ => File.WriteAllText(SettingsFile, "{ damaged while the backup ran"));

        Assert.True(File.Exists(backup.FullPath));
    }

    [Fact]
    public async Task A_time_of_day_that_cannot_be_read_falls_back_to_three_in_the_morning()
    {
        var service = NewService("2026-10-14T01:00:00Z");
        await service.SaveSettingsAsync(Settings(enabled: true), CancellationToken.None);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
        node["timeOfDay"] = "soon";
        node["nextRunUtc"] = null;
        File.WriteAllText(SettingsFile, node.ToJsonString());

        // Reading normalises the time, so the next run is worked out for 03:00.
        var settings = await NewService().GetSettingsAsync(CancellationToken.None);

        Assert.Equal("03:00", settings.TimeOfDay);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T03:00:00Z"), settings.NextRunUtc);
    }

    [Fact]
    public async Task A_file_date_after_the_last_one_a_zip_holds_does_not_lose_the_file()
    {
        var file = Path.Combine(root, "platform.db");
        File.WriteAllText(file, "platform");
        File.SetLastWriteTime(file, new DateTime(2300, 1, 1, 12, 0, 0, DateTimeKind.Local));

        var backup = await NewService().CreateBackupAsync("manual", CancellationToken.None);

        using var archive = ZipFile.OpenRead(backup.FullPath);
        var entry = archive.GetEntry("data/platform.db");
        Assert.NotNull(entry);
        Assert.True(entry!.LastWriteTime.Year <= 2107, "a zip can only hold up to 2107");
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("platform", await reader.ReadToEndAsync());
    }

    private sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
