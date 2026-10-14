using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Restart and apply" over HTTP with the real backup service writing to a real data folder. Only the step that would replace the
/// running program is stood in for (an update cannot be installed from a test); the backup that must come first is the real one.
/// </summary>
public sealed class PreUpdateBackupOnRealFilesTests
{
    [Fact]
    public async Task Restart_and_apply_takes_a_real_pre_update_backup_first_and_leaves_the_data_as_it_was()
    {
        var applied = new AppliedCounter();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Replace(services, applied));
        var notes = Path.Combine(app.DataRoot, "settings", "notes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        await File.WriteAllTextAsync(notes, "kept");

        using var response = await app.Client.PostAsync("/api/updates/restart-now", content: null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, applied.Count);
        var zip = Assert.Single(Directory.GetFiles(Path.Combine(app.DataRoot, "backups"), "*.zip"));
        using (var archive = ZipFile.OpenRead(zip))
        {
            using var manifest = JsonDocument.Parse(archive.GetEntry("deluno-backup.json")!.Open());
            Assert.Equal("pre-update", manifest.RootElement.GetProperty("reason").GetString());
            using var reader = new StreamReader(archive.GetEntry("data/settings/notes.json")!.Open());
            Assert.Equal("kept", reader.ReadToEnd());
        }

        Assert.Equal("kept", await File.ReadAllTextAsync(notes));
    }

    [Fact]
    public async Task Restart_and_apply_is_refused_and_nothing_is_applied_when_the_backup_cannot_be_written()
    {
        var applied = new AppliedCounter();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Replace(services, applied));
        var folder = Path.Combine(app.DataRoot, "chosen-backups");
        using var saved = await app.Client.PutAsJsonAsync("/api/backups/settings", new { enabled = false, frequency = "daily", timeOfDay = "03:00", retentionCount = 7, backupFolder = folder });
        saved.EnsureSuccessStatusCode();
        // The folder turns into a file, which is what a drive that came back as something else looks like.
        Directory.Delete(folder, true);
        await File.WriteAllTextAsync(folder, "not a folder");

        using var response = await app.Client.PostAsync("/api/updates/restart-now", content: null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.StartsWith("Backup failed and restart was blocked", body.GetProperty("message").GetString());
        Assert.Equal(0, applied.Count);
        Assert.Empty(Directory.GetFiles(app.DataRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_pre_update_backup_is_not_pruned_by_retention_while_older_backups_are()
    {
        var applied = new AppliedCounter();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => Replace(services, applied));
        using var saved = await app.Client.PutAsJsonAsync("/api/backups/settings", new { enabled = false, frequency = "daily", timeOfDay = "03:00", retentionCount = 1 });
        saved.EnsureSuccessStatusCode();
        var backups = Path.Combine(app.DataRoot, "backups");
        var older = Path.Combine(backups, "deluno-backup-20200101-000000.zip");
        using (var zip = ZipFile.Open(older, ZipArchiveMode.Create))
        {
            using var manifest = new StreamWriter(zip.CreateEntry("deluno-backup.json").Open());
            manifest.Write("{\"app\":\"Deluno\",\"version\":\"1\",\"createdUtc\":\"2020-01-01T00:00:00Z\",\"reason\":\"manual\",\"files\":[]}");
        }

        using var response = await app.Client.PostAsync("/api/updates/restart-now", content: null);

        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetBoolean());
        var left = Directory.GetFiles(backups, "*.zip");
        var kept = Assert.Single(left);
        Assert.NotEqual(older, kept);
        using var archive = ZipFile.OpenRead(kept);
        using var kind = JsonDocument.Parse(archive.GetEntry("deluno-backup.json")!.Open());
        Assert.Equal("pre-update", kind.RootElement.GetProperty("reason").GetString());
    }

    private static void Replace(IServiceCollection services, AppliedCounter applied)
    {
        services.RemoveAll<IUpdateOrchestrator>();
        services.AddSingleton<IUpdateOrchestrator>(new CountingOrchestrator(applied));
    }

    private sealed class AppliedCounter
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Hit() => Interlocked.Increment(ref count);
    }

    private sealed class CountingOrchestrator(AppliedCounter applied) : IUpdateOrchestrator
    {
        public Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status("status"));
        public Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult(Status("checked"));
        public Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult(Status("downloaded"));

        public Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken)
        {
            applied.Hit();
            return Task.FromResult(Status("restarting"));
        }

        public Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult(new UpdatePreferencesResponse(UpdateModes.NotifyOnly, "stable", false));

        public Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken)
            => GetPreferencesAsync(cancellationToken);

        private static UpdateStatusResponse Status(string message) => new(
            CurrentVersion: "1.0.0", Channel: "stable", InstallKind: UpdateInstallKinds.Manual, BehaviorMode: UpdateModes.NotifyOnly,
            IsInstalled: true, CanCheck: true, CanDownload: true, CanApply: true, UpdateAvailable: true, LatestVersion: "1.1.0",
            State: UpdateStates.NotSupported, ProgressPercent: null, RestartRequired: true, LastCheckedUtc: null, LastDownloadedUtc: null,
            Message: message, LastError: null, Notes: Array.Empty<string>());
    }
}
