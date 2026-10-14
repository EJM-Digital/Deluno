using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The backup and restore routes over HTTP, against the real application and a real data folder on disk. These are the paths a
/// person falls back on when something has gone wrong, so each is checked by what ends up in the folder, not by the status alone.
/// </summary>
public sealed class BackupOverHttpOnRealFilesTests
{
    [Fact]
    public async Task A_backup_holds_every_data_file_except_the_excluded_folders_and_the_manifest_lists_exactly_what_went_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Seed(app.DataRoot, "settings/notes.json", "{}");
        Seed(app.DataRoot, "logs/deluno-20260101.jsonl", "log");
        Seed(app.DataRoot, "restore-staging/old/platform.db", "staged");
        Seed(app.DataRoot, "backup-snapshots/x/y.db", "snapshot");
        Seed(app.DataRoot, "platform.db.pre-restore", "old");

        var id = await CreateAsync(app);

        Assert.True(File.Exists(Path.Combine(app.DataRoot, "backups", id + ".zip")));
        using var archive = ZipFile.OpenRead(Path.Combine(app.DataRoot, "backups", id + ".zip"));
        var entries = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("data/settings/notes.json", entries);
        Assert.Contains("data/platform.db", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("data/backups", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.StartsWith("data/logs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.StartsWith("data/restore-staging", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.StartsWith("data/backup-snapshots", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.EndsWith(".pre-restore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || e.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));

        using var manifestStream = archive.GetEntry("deluno-backup.json")!.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var dataEntries = entries.Where(e => e.StartsWith("data/", StringComparison.Ordinal)).Select(e => e["data/".Length..].Replace('/', Path.DirectorySeparatorChar)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(dataEntries, manifest!.Files.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public async Task Downloading_a_backup_streams_the_zip_under_its_file_name_and_an_unknown_id_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateAsync(app);

        using var response = await app.Client.GetAsync($"/api/backups/{id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(id + ".zip", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(app.DataRoot, "backups", id + ".zip")), downloaded);

        using var unknown = await app.Client.GetAsync("/api/backups/deluno-backup-19990101-000000/download");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_backup_id_with_path_parts_cannot_reach_a_file_outside_the_backup_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        // A zip that sits beside the backup folder, one level up.
        var outside = Path.Combine(app.DataRoot, "outside.zip");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);

        foreach (var id in new[] { "..%2Foutside", "..%5Coutside", "%2E%2E%2Foutside.zip" })
        {
            using var download = await app.Client.GetAsync($"/api/backups/{id}/download");
            Assert.NotEqual(HttpStatusCode.OK, download.StatusCode);
            using var delete = await app.Client.DeleteAsync($"/api/backups/{id}");
            Assert.True(delete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent, $"{id} answered {(int)delete.StatusCode}");
        }

        // The sibling zip is untouched whatever the ids resolved to.
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task Deleting_a_backup_removes_the_zip_from_the_backup_folder_and_the_second_delete_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateAsync(app);
        var zip = Path.Combine(app.DataRoot, "backups", id + ".zip");
        Assert.True(File.Exists(zip));

        using var first = await app.Client.DeleteAsync($"/api/backups/{id}");
        using var second = await app.Client.DeleteAsync($"/api/backups/{id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.False(File.Exists(zip));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Saving_the_schedule_normalises_frequency_time_and_folder_clamps_retention_and_sets_the_next_run()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var chosen = Path.Combine(app.DataRoot, "my-backups", "..", "elsewhere");

        using var response = await app.Client.PutAsJsonAsync("/api/backups/settings", new
        {
            enabled = true,
            frequency = " WEEKLY ",
            timeOfDay = "25:99",
            retentionCount = 0,
            backupFolder = chosen
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("weekly", saved.GetProperty("frequency").GetString());
        Assert.Equal("03:00", saved.GetProperty("timeOfDay").GetString());
        Assert.Equal(1, saved.GetProperty("retentionCount").GetInt32());
        var folder = saved.GetProperty("backupFolder").GetString()!;
        Assert.Equal(Path.GetFullPath(chosen), folder);
        Assert.True(Directory.Exists(folder));
        Assert.DoesNotContain("..", folder);
        Assert.True(saved.GetProperty("nextRunUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        // The file on disk is what the next start reads.
        var read = await app.Client.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        Assert.Equal(Path.GetFullPath(chosen), read.GetProperty("backupFolder").GetString());

        using var tooMany = await app.Client.PutAsJsonAsync("/api/backups/settings", new { enabled = false, frequency = "daily", timeOfDay = "02:00", retentionCount = 999999 });
        var clamped = await tooMany.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10000, clamped.GetProperty("retentionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, clamped.GetProperty("nextRunUtc").ValueKind);
    }

    [Theory]
    [InlineData("daily", "2026-05-14T01:00:00Z", "2026-05-14T02:30:00Z")]
    [InlineData("daily", "2026-05-14T03:00:00Z", "2026-05-15T02:30:00Z")]
    [InlineData("weekly", "2026-05-14T03:00:00Z", "2026-05-21T02:30:00Z")]
    [InlineData("monthly", "2026-05-14T03:00:00Z", "2026-06-14T02:30:00Z")]
    public async Task The_next_run_is_the_next_daily_weekly_or_monthly_time_after_now(string frequency, string now, string expected)
    {
        using var storage = new TempFolder();
        var service = Service(storage, now);

        var saved = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, frequency, "02:30", 7, null), CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse(expected), saved.NextRunUtc);
        Assert.Contains(frequency, File.ReadAllText(Path.Combine(storage, "backups", "backup-settings.json")));
    }

    [Fact]
    public async Task A_disabled_schedule_has_no_next_run()
    {
        using var storage = new TempFolder();
        var service = Service(storage, "2026-05-14T01:00:00Z");

        var saved = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "02:30", 7, null), CancellationToken.None);

        Assert.Null(saved.NextRunUtc);
        Assert.Contains("\"nextRunUtc\": null", await File.ReadAllTextAsync(Path.Combine(storage, "backups", "backup-settings.json")));
    }

    [Fact]
    public async Task Backups_beyond_the_retention_count_are_deleted_oldest_first_and_the_newest_pre_update_backup_stays()
    {
        using var storage = new TempFolder();
        File.WriteAllText(Path.Combine(storage, "platform.db"), "x");
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        var service = new DelunoBackupService(Options.Create(new StoragePathOptions { DataRoot = storage }), clock, NullLogger<DelunoBackupService>.Instance);
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 3, null), CancellationToken.None);

        var ids = new List<string>();
        foreach (var reason in new[] { "manual", "manual", "manual", "manual", "pre-update" })
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            ids.Add((await service.CreateBackupAsync(reason, CancellationToken.None)).Id);
        }

        var left = Directory.GetFiles(Path.Combine(storage, "backups"), "*.zip").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(ids.Skip(2).Order().ToArray(), left);
        var list = await service.ListBackupsAsync(CancellationToken.None);
        Assert.Equal("pre-update", list[0].Reason);
        Assert.Equal(ids[4], list[0].Id);
    }

    [Fact]
    public async Task Two_backups_at_once_are_taken_one_after_the_other_and_both_are_whole_zips()
    {
        using var storage = new TempFolder();
        File.WriteAllText(Path.Combine(storage, "platform.db"), "x");
        var big = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(big);
        File.WriteAllBytes(Path.Combine(storage, "big.bin"), big);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        var service = new DelunoBackupService(Options.Create(new StoragePathOptions { DataRoot = storage }), clock, NullLogger<DelunoBackupService>.Instance);
        var overlapped = 0;
        var inside = 0;
        void Enter(string _)
        {
            if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlapped);
            Thread.Sleep(20); // reason: holds the first backup inside its file loop so a second one would visibly overlap it
            Interlocked.Decrement(ref inside);
        }

        var first = Task.Run(() => service.CreateBackupAsync("manual", CancellationToken.None, Enter));
        var second = Task.Run(() => service.CreateBackupAsync("manual", CancellationToken.None, Enter));
        var done = await Task.WhenAll(first, second);

        Assert.Equal(0, overlapped);
        foreach (var item in done)
        {
            using var archive = ZipFile.OpenRead(item.FullPath);
            Assert.NotNull(archive.GetEntry("deluno-backup.json"));
            Assert.NotNull(archive.GetEntry("data/big.bin"));
        }
    }

    [Fact]
    public async Task A_backup_into_a_folder_that_cannot_be_used_fails_and_leaves_no_half_zip_behind()
    {
        using var storage = new TempFolder();
        File.WriteAllText(Path.Combine(storage, "platform.db"), "x");
        var service = Service(storage, "2026-05-14T01:00:00Z");
        var folder = Path.Combine(storage, "chosen");
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 7, folder), CancellationToken.None);
        // The folder is replaced by a file, which is what a drive that came back as something else looks like.
        Directory.Delete(folder, true);
        File.WriteAllText(folder, "not a folder");

        await Assert.ThrowsAnyAsync<IOException>(() => service.CreateBackupAsync("manual", CancellationToken.None));

        Assert.DoesNotContain(Directory.EnumerateFiles(storage, "*.zip", SearchOption.AllDirectories), f => true);
        Assert.False(Directory.Exists(Path.Combine(storage, "backup-snapshots")));
    }

    [Fact]
    public async Task The_scheduled_backup_runs_once_the_next_run_time_has_passed_and_moves_the_next_run_on()
    {
        using var storage = new TempFolder();
        File.WriteAllText(Path.Combine(storage, "platform.db"), "x");
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        var service = new DelunoBackupService(Options.Create(new StoragePathOptions { DataRoot = storage }), clock, NullLogger<DelunoBackupService>.Instance);
        var saved = await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "daily", "02:00", 7, null), CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T02:00:00Z"), saved.NextRunUtc);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => Directory.GetFiles(Path.Combine(storage, "backups"), "*.zip").Length == 1, "the scheduled backup to be written");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var after = await service.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T02:01:00Z"), after.LastRunUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-05-15T02:00:00Z"), after.NextRunUtc);
        var item = (await service.ListBackupsAsync(CancellationToken.None)).Single();
        Assert.Equal("scheduled", item.Reason);
    }

    [Fact]
    public async Task The_backup_routes_need_sign_in_and_the_system_scope()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        var created = await CreateAsync(app);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/backups"), (HttpMethod.Get, "/api/backups/settings"), (HttpMethod.Put, "/api/backups/settings"),
                     (HttpMethod.Post, "/api/backups"), (HttpMethod.Get, $"/api/backups/{created}/download"), (HttpMethod.Delete, $"/api/backups/{created}"),
                     (HttpMethod.Post, "/api/backups/restore"), (HttpMethod.Post, "/api/backups/restore/preview")
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var refused = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // A key with only the read scope signs in, but every change to a backup asks for the system scope.
        var made = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "read only", scopes = "read" });
        var key = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var readOnly = app.CreateAnonymousClient();
        readOnly.DefaultRequestHeaders.Add("X-Api-Key", key);
        using var delete = await readOnly.DeleteAsync($"/api/backups/{created}");
        using var create = await readOnly.PostAsJsonAsync("/api/backups", new { reason = "manual" });
        using var save = await readOnly.PutAsJsonAsync("/api/backups/settings", new { enabled = true, frequency = "daily", timeOfDay = "02:00", retentionCount = 1 });
        using var restoreForm = new MultipartFormDataContent { { new ByteArrayContent(ZipOf(("deluno-backup.json", "{\"app\":\"Deluno\"}"), ("data/platform.db", "x"))), "file", "b.zip" } };
        using var restore = await readOnly.PostAsync("/api/backups/restore", restoreForm);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
        Assert.True(File.Exists(Path.Combine(app.DataRoot, "backups", created + ".zip")));
        Assert.False(StagedRestore.IsPending(app.DataRoot));
        Assert.Single(Directory.GetFiles(Path.Combine(app.DataRoot, "backups"), "*.zip"));
    }

    [Fact]
    public async Task Previewing_a_valid_backup_over_multipart_says_it_can_be_restored_with_its_manifest()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await CreateAsync(app);

        var preview = await PostFileAsync(app, "/api/backups/restore/preview", await File.ReadAllBytesAsync(Path.Combine(app.DataRoot, "backups", id + ".zip")));

        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.Equal("Backup can be restored.", preview.GetProperty("message").GetString());
        Assert.Equal("Deluno", preview.GetProperty("manifest").GetProperty("app").GetString());
        Assert.True(preview.GetProperty("manifest").GetProperty("files").GetArrayLength() > 0);
        Assert.NotEqual(default, preview.GetProperty("manifest").GetProperty("createdUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Preview_refuses_no_manifest_another_apps_manifest_and_a_file_that_is_not_a_zip_and_warns_about_no_data()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noManifest = await PostFileAsync(app, "/api/backups/restore/preview", ZipOf(("data/platform.db", "x")));
        Assert.False(noManifest.GetProperty("valid").GetBoolean());
        Assert.Equal("This file is not a Deluno backup.", noManifest.GetProperty("message").GetString());

        var other = await PostFileAsync(app, "/api/backups/restore/preview", ZipOf(("deluno-backup.json", "{\"app\":\"Sonarr\",\"files\":[]}"), ("data/platform.db", "x")));
        Assert.False(other.GetProperty("valid").GetBoolean());
        Assert.Equal("The backup manifest is invalid.", other.GetProperty("message").GetString());

        var notZip = await PostFileAsync(app, "/api/backups/restore/preview", Encoding.UTF8.GetBytes("this is plain text, not an archive"));
        Assert.False(notZip.GetProperty("valid").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(notZip.GetProperty("message").GetString()));

        var noData = await PostFileAsync(app, "/api/backups/restore/preview", ZipOf(("deluno-backup.json", "{\"app\":\"Deluno\",\"files\":[]}")));
        Assert.True(noData.GetProperty("valid").GetBoolean());
        Assert.Contains("The archive does not contain any data files.", noData.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        // A preview only looks: nothing is staged and no restart is armed.
        Assert.False(Directory.Exists(Path.Combine(app.DataRoot, "restore-staging")));
        Assert.False(StagedRestore.IsPending(app.DataRoot));
    }

    [Fact]
    public async Task Restore_checks_the_archive_itself_and_refuses_an_invalid_or_empty_one_without_staging_anything()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var staging = Path.Combine(app.DataRoot, "restore-staging");

        var invalid = await PostFileAsync(app, "/api/backups/restore", ZipOf(("data/platform.db", "x")));
        Assert.False(invalid.GetProperty("restored").GetBoolean());
        Assert.Equal("This file is not a Deluno backup.", invalid.GetProperty("message").GetString());

        var empty = await PostFileAsync(app, "/api/backups/restore", ZipOf(("deluno-backup.json", "{\"app\":\"Deluno\",\"files\":[]}")));
        Assert.False(empty.GetProperty("restored").GetBoolean());
        Assert.Equal("No restorable data files were found.", empty.GetProperty("message").GetString());

        Assert.False(StagedRestore.IsPending(app.DataRoot));
        Assert.True(!Directory.Exists(staging) || Directory.GetDirectories(staging).Length == 0, "an empty restore must not leave a staging folder");
    }

    [Fact]
    public async Task Restore_stages_the_files_arms_the_marker_and_changes_nothing_live_until_the_restart_applies_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Seed(app.DataRoot, "settings/notes.json", "before");
        var id = await CreateAsync(app);
        // Changed after the backup, which the restore will put back.
        File.WriteAllText(Path.Combine(app.DataRoot, "settings", "notes.json"), "after");
        var liveBefore = new FileInfo(Path.Combine(app.DataRoot, "platform.db")).LastWriteTimeUtc;

        var restored = await PostFileAsync(app, "/api/backups/restore", await File.ReadAllBytesAsync(Path.Combine(app.DataRoot, "backups", id + ".zip")));

        Assert.True(restored.GetProperty("restored").GetBoolean());
        Assert.Contains("Backup staged. Restart Deluno to apply it", restored.GetProperty("message").GetString());
        var files = restored.GetProperty("restoredFiles").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Contains("platform.db", files);
        Assert.Contains(Path.Combine("settings", "notes.json"), files);
        var folder = restored.GetProperty("restoreFolder").GetString()!;
        Assert.StartsWith(Path.Combine(app.DataRoot, "restore-staging"), folder);
        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(folder, "settings", "notes.json")));
        Assert.True(StagedRestore.IsPending(app.DataRoot));
        // Nothing live moved: the restart is what applies it.
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(app.DataRoot, "settings", "notes.json")));
        Assert.False(File.Exists(Path.Combine(app.DataRoot, "settings", "notes.json.pre-restore")));
        Assert.Equal(liveBefore, new FileInfo(Path.Combine(app.DataRoot, "platform.db")).LastWriteTimeUtc);
        Assert.False(File.Exists(Path.Combine(app.DataRoot, "platform.db.pre-restore")));
    }

    [Fact]
    public async Task A_restore_and_a_backup_never_overlap()
    {
        using var storage = new TempFolder();
        File.WriteAllText(Path.Combine(storage, "platform.db"), "x");
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        var service = new DelunoBackupService(Options.Create(new StoragePathOptions { DataRoot = storage }), clock, NullLogger<DelunoBackupService>.Instance);
        var seed = await service.CreateBackupAsync("manual", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));

        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var running = Task.Run(() => service.CreateBackupAsync("manual", CancellationToken.None, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the backup should be mid-way");

        await using var stream = File.OpenRead(seed.FullPath);
        var restoring = service.RestoreAsync(stream, CancellationToken.None);
        await Task.Delay(300); // reason: gives a restore that ignored the gate time to run ahead of the backup
        Assert.False(restoring.IsCompleted, "a restore must wait while a backup is being taken");
        Assert.False(StagedRestore.IsPending(storage));

        release.Set();
        await running;
        var result = await restoring;
        Assert.True(result.Restored);
        Assert.True(StagedRestore.IsPending(storage));
    }

    private static async Task<string> CreateAsync(ApplicationTestHost app)
    {
        using var created = await app.Client.PostAsJsonAsync("/api/backups", new { reason = "manual" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("backup").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> PostFileAsync(ApplicationTestHost app, string path, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        using var response = await app.Client.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static byte[] ZipOf(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        return memory.ToArray();
    }

    private static void Seed(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static DelunoBackupService Service(string dataRoot, string now)
        => new(Options.Create(new StoragePathOptions { DataRoot = dataRoot }), new FixedTimeProvider(DateTimeOffset.Parse(now)), NullLogger<DelunoBackupService>.Instance);

    

    private sealed class TempFolder : IDisposable
    {
        private readonly string path;

        public TempFolder() : this(Path.Combine(Path.GetTempPath(), "deluno-backup-http-tests", Guid.NewGuid().ToString("N")))
        {
        }

        private TempFolder(string path)
        {
            this.path = path;
            Directory.CreateDirectory(path);
        }

        public static implicit operator string(TempFolder scope) => scope.path;

        public void Dispose()
        {
            try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
