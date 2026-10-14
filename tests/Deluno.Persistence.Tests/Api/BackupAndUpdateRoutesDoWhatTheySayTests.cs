using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The backup and update routes when the request is right (coverage register, area api): a real archive
/// is written to the real backup folder, downloaded, previewed, staged for restore and deleted.
/// </summary>
public sealed class BackupAndUpdateRoutesDoWhatTheySayTests
{
    private static string BackupFolder(ApplicationTestHost app) => Path.Combine(app.DataRoot, "backups");

    private static async Task<JsonElement> CreateBackupAsync(ApplicationTestHost app, string reason = "manual")
    {
        var response = await app.Client.PostAsJsonAsync("/api/backups", new { reason });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backup");
    }

    private static MultipartFormDataContent Upload(byte[] archive)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        return form;
    }

    [Fact]
    public async Task Creating_a_backup_writes_a_zip_with_a_manifest_and_the_databases_into_the_backup_folder_and_lists_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var backup = await CreateBackupAsync(app, "manual");

        var fullPath = backup.GetProperty("fullPath").GetString()!;
        Assert.True(File.Exists(fullPath), "the archive is on disk");
        Assert.StartsWith(BackupFolder(app), fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("manual", backup.GetProperty("reason").GetString());
        Assert.Equal(new FileInfo(fullPath).Length, backup.GetProperty("sizeBytes").GetInt64());
        using (var archive = ZipFile.OpenRead(fullPath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "deluno-backup.json");
            Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("data/", StringComparison.Ordinal) && entry.FullName.EndsWith(".db", StringComparison.Ordinal));
        }

        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/backups");
        Assert.Equal(backup.GetProperty("id").GetString(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Downloading_a_backup_returns_the_same_bytes_that_are_on_disk_and_an_unknown_id_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var backup = await CreateBackupAsync(app);
        var id = backup.GetProperty("id").GetString()!;

        var download = await app.Client.GetAsync($"/api/backups/{id}/download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(backup.GetProperty("fullPath").GetString()!), await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/backups/no-such-backup/download")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_backup_removes_its_archive_from_the_disk_and_a_second_delete_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var backup = await CreateBackupAsync(app);
        var id = backup.GetProperty("id").GetString()!;
        var fullPath = backup.GetProperty("fullPath").GetString()!;
        Assert.True(File.Exists(fullPath));

        var delete = await app.Client.DeleteAsync($"/api/backups/{id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(File.Exists(fullPath), "the archive is gone from the backup folder");
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/backups")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/backups/{id}")).StatusCode);
    }

    [Fact]
    public async Task Previewing_a_restore_names_the_backup_it_would_use_and_touches_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var backup = await CreateBackupAsync(app, "pre-update");
        var bytes = await File.ReadAllBytesAsync(backup.GetProperty("fullPath").GetString()!);

        var response = await app.Client.PostAsync("/api/backups/restore/preview", Upload(bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("valid").GetBoolean(), preview.ToString());
        Assert.Equal("Deluno", preview.GetProperty("manifest").GetProperty("app").GetString());
        Assert.Equal("pre-update", preview.GetProperty("manifest").GetProperty("reason").GetString());
        Assert.False(Directory.Exists(Path.Combine(app.DataRoot, "restore-staging")), "a preview stages nothing");
    }

    [Fact]
    public async Task Previewing_a_file_that_is_not_a_backup_says_it_is_not_valid()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/backups/restore/preview", Upload("this is not a zip"u8.ToArray()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Restoring_a_backup_stages_every_database_in_it_for_the_next_start_without_overwriting_the_live_ones()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var backup = await CreateBackupAsync(app);
        var archivePath = backup.GetProperty("fullPath").GetString()!;
        var bytes = await File.ReadAllBytesAsync(archivePath);
        var expected = new List<string>();
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            expected.AddRange(archive.Entries.Where(e => e.FullName.StartsWith("data/", StringComparison.Ordinal) && e.Name.Length > 0).Select(e => e.FullName["data/".Length..]));
        }
        Assert.NotEmpty(expected);
        var liveBefore = new FileInfo(Directory.EnumerateFiles(app.DataRoot, "*.db", SearchOption.TopDirectoryOnly).First()).LastWriteTimeUtc;

        var response = await app.Client.PostAsync("/api/backups/restore", Upload(bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("restored").GetBoolean(), result.ToString());
        var restoreFolder = result.GetProperty("restoreFolder").GetString()!;
        Assert.True(Directory.Exists(restoreFolder), "the archive was extracted into a staging folder");
        var restoredNames = result.GetProperty("restoredFiles").EnumerateArray().Select(e => e.GetString()!.Replace('\\', '/')).ToArray();
        Assert.Equal(expected.Select(e => e.Replace('\\', '/')).Order(StringComparer.Ordinal), restoredNames.Order(StringComparer.Ordinal));
        foreach (var name in restoredNames)
        {
            Assert.True(File.Exists(Path.Combine(restoreFolder, name)), $"{name} is staged");
        }

        Assert.Equal(liveBefore, new FileInfo(Directory.EnumerateFiles(app.DataRoot, "*.db", SearchOption.TopDirectoryOnly).First()).LastWriteTimeUtc);
    }

    [Fact]
    public async Task Restoring_a_file_that_is_not_a_backup_restores_nothing_and_says_why()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/backups/restore", Upload("not a backup"u8.ToArray()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("restored").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
        Assert.Empty(result.GetProperty("restoredFiles").EnumerateArray());
    }

    [Fact]
    public async Task Restarting_for_an_update_takes_a_pre_update_backup_on_disk_first_and_reports_it_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Empty(Directory.Exists(BackupFolder(app)) ? Directory.GetFiles(BackupFolder(app), "*.zip") : []);

        var response = await app.Client.PostAsync("/api/updates/restart-now", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean(), body.ToString());
        Assert.Equal("Backup completed. Restarting to apply update.", body.GetProperty("message").GetString());
        var archive = Assert.Single(Directory.GetFiles(BackupFolder(app), "*.zip"));
        using var zip = ZipFile.OpenRead(archive);
        using var manifest = JsonDocument.Parse(new StreamReader(zip.GetEntry("deluno-backup.json")!.Open()).ReadToEnd());
        Assert.Equal("pre-update", manifest.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Downloading_an_update_answers_the_update_status_and_says_the_request_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/updates/download", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal("Download request completed.", body.GetProperty("message").GetString());
        var status = body.GetProperty("status");
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("currentVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("state").GetString()));
    }
}
