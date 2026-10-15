using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Api.Backup;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Backups on a real data folder with real SQLite databases (platform BKP-02, BKP-05, BKP-09): the backup list is pushed live when it
/// changes, a scheduled pass is taken by one runner only and a pass stopped part-way is recorded as cancelled, and an upload bigger
/// than ASP.NET's 30,000,000-byte default reaches the restore preview on a real Kestrel listener.
/// </summary>
public sealed class BackupLiveListClaimAndBigUploadOnRealFilesTests
{
    private static DelunoBackupService ServiceFor(ApplicationTestHost app, IServiceScope scope, IRealtimeEventPublisher? realtime = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = app.DataRoot }),
            TimeProvider.System,
            NullLogger<DelunoBackupService>.Instance,
            scope.ServiceProvider.GetRequiredService<IJobQueueRepository>(),
            realtime: realtime);

    private static string SettingsFile(ApplicationTestHost app) => Path.Combine(app.DataRoot, "backups", "backup-settings.json");

    private static void MakeDue(ApplicationTestHost app)
    {
        var settings = JsonNode.Parse(File.ReadAllText(SettingsFile(app))) as JsonObject ?? new JsonObject();
        settings["enabled"] = true;
        settings["nextRunUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        File.WriteAllText(SettingsFile(app), settings.ToJsonString());
    }

    private static async Task<SystemTaskState?> TaskStateAsync(IServiceScope scope)
        => (await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .FirstOrDefault(state => state.ScheduleKey == SystemTasks.Backup);

    private static string[] Zips(ApplicationTestHost app)
        => Directory.Exists(Path.Combine(app.DataRoot, "backups")) ? Directory.GetFiles(Path.Combine(app.DataRoot, "backups"), "*.zip") : [];

    [Fact]
    public async Task Taking_a_backup_and_deleting_it_each_push_the_backup_list_live_and_the_list_then_matches_the_folder()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync();
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope, publisher);

        var made = await service.CreateBackupAsync("manual", CancellationToken.None);

        Assert.Contains(publisher.Published, item => item is { Type: "Backup", Id: "backups" });
        Assert.Equal(made.Id, Assert.Single(await service.ListBackupsAsync(CancellationToken.None)).Id);
        var pushedByTaking = publisher.Published.Count(item => item.Type == "Backup");

        Assert.True(await service.DeleteBackupAsync(made.Id, CancellationToken.None));

        Assert.True(publisher.Published.Count(item => item.Type == "Backup") > pushedByTaking, "deleting a backup pushes the list again");
        Assert.Empty(await service.ListBackupsAsync(CancellationToken.None));
        Assert.Empty(Zips(app));
        // A delete that finds nothing changes nothing and pushes nothing.
        var before = publisher.Published.Count(item => item.Type == "Backup");
        Assert.False(await service.DeleteBackupAsync(made.Id, CancellationToken.None));
        Assert.Equal(before, publisher.Published.Count(item => item.Type == "Backup"));
    }

    [Fact]
    public async Task A_scheduled_pass_another_runner_already_holds_is_not_taken_a_second_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "backups"));
        await app.Client.GetAsync("/api/backups/settings");
        MakeDue(app);
        using var scope = app.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueueRepository>();
        // Another runner (a second copy of Deluno on the same data) claimed this pass a moment ago.
        Assert.True(await jobs.TryClaimScheduledPassAsync(SystemTasks.Backup, SystemTasks.IntervalFor(SystemTasks.Backup), CancellationToken.None));
        var service = ServiceFor(app, scope);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3)); // reason: a backup that must NOT be taken can only be shown absent once the runner's first pass has had time to look
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(Zips(app));
        Assert.NotEqual("completed", (await TaskStateAsync(scope))?.LastResult);
    }

    [Fact]
    public async Task A_scheduled_pass_stopped_while_it_waits_for_the_backup_is_recorded_as_cancelled_and_leaves_no_archive()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "backups"));
        await app.Client.GetAsync("/api/backups/settings");
        MakeDue(app);
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope);
        // A restore (or another backup) holds the backup gate, so the scheduled pass is part-way: claimed, and waiting its turn.
        var gate = (SemaphoreSlim)typeof(DelunoBackupService).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        await gate.WaitAsync();
        try
        {
            await service.StartAsync(CancellationToken.None);
            await Wait.UntilAsync(async () => (await TaskStateAsync(scope)) is not null, "the scheduled pass to be claimed");
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }

        await Wait.UntilAsync(async () => (await TaskStateAsync(scope))?.LastResult == "cancelled", "the stopped pass to be recorded as cancelled");
        Assert.Empty(Zips(app));
    }

    [Fact]
    public async Task A_backup_bigger_than_the_default_upload_limit_reaches_the_restore_preview_on_a_real_listener()
    {
        await using var host = await SharedPipelineHost.StartAsync(root: Path.Combine(Path.GetTempPath(), "deluno-big-upload", Guid.NewGuid().ToString("N")));
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        created.EnsureSuccessStatusCode();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);

        // 40 MB of incompressible database content: over ASP.NET's 30,000,000-byte default for a request body.
        var archive = Path.Combine(host.DataRoot, "..", "big-backup.zip");
        using (var file = File.Create(archive))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await using (var manifest = zip.CreateEntry("deluno-backup.json").Open())
            {
                await manifest.WriteAsync("{\"app\":\"Deluno\",\"files\":[\"platform.db\"]}"u8.ToArray());
            }

            var big = zip.CreateEntry("data/platform.db", CompressionLevel.NoCompression);
            await using var stream = big.Open();
            var chunk = new byte[1024 * 1024];
            var random = new Random(7);
            for (var i = 0; i < 40; i++)
            {
                random.NextBytes(chunk);
                await stream.WriteAsync(chunk);
            }
        }

        Assert.True(new FileInfo(archive).Length > 30_000_000);
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(File.OpenRead(archive));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(content, "file", "big-backup.zip");

        using var response = await host.Client.PostAsync("/api/backups/restore/preview", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("valid").GetBoolean(), preview.ToString());
        Assert.Equal("Backup can be restored.", preview.GetProperty("message").GetString());
    }
}
