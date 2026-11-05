using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The scheduled blocked-release cleanup (<see cref="SystemTasks.BlockedReleaseCleanup"/>, planning.maintenance lane) run by the real
/// <see cref="WorkPlanner"/> with the real refused-download cleanup service against real files in a temp folder: the leftover payload
/// of a refused release is gone from the disk after one pass, the pass is recorded completed, and a file Deluno did not send is
/// still there.
/// </summary>
public sealed class TheBlockedReleaseCleanupPassClearsLeftoversOnDiskTests
{
    private const string Release = "Severance S02E10 1080p WEBRip H264-WREN";

    private static BlockedRelease Refusal(string name)
        => new(Guid.NewGuid().ToString("N"), $"key-{name}", name, "Fixture Indexer", "tv", "episode-1", "Severance",
            "noVideoStream", "Refused.", "hash-1", "qb-1", "qBittorrent", DateTimeOffset.UtcNow,
            BlockedReleaseStates.Refused, BlockedReleaseOrigins.Automatic);

    private static string Write(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[256]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-5));
        return path;
    }

    private static async Task<string> DownloadsFolderAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Incomplete")).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Shows", mediaType = "tv", rootPath = root, downloadsPath = downloads });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return downloads;
    }

    private static WorkPlanner PlannerFor(ApplicationTestHost app)
        => new(NullLogger<WorkPlanner>.Instance, app.Services.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), TimeProvider.System);

    [Fact]
    public async Task One_pass_removes_a_refused_releases_leftover_file_from_the_disk_and_is_recorded_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = await DownloadsFolderAsync(app);
        var payload = Write(Path.Combine(downloads, "qBittorrent", "deluno-tv"), Release + ".exe");
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(Refusal(Release), CancellationToken.None);
        Assert.True(File.Exists(payload));

        using var scope = app.Services.CreateScope();
        await PlannerFor(app).RunBlockedReleaseCleanupAsync(
            scope.ServiceProvider.GetRequiredService<IRefusedDownloadCleanupService>(), CancellationToken.None);

        Assert.False(File.Exists(payload));
        var state = (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .Single(s => s.ScheduleKey == SystemTasks.BlockedReleaseCleanup);
        Assert.Equal("completed", state.LastResult);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task One_pass_leaves_a_file_of_a_release_deluno_did_not_refuse_where_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var downloads = await DownloadsFolderAsync(app);
        var refused = Write(Path.Combine(downloads, "qBittorrent", "deluno-tv"), Release + ".exe");
        var other = Write(Path.Combine(downloads, "qBittorrent", "deluno-tv"), "Some Other Show S01E01 1080p WEB-DL.mkv");
        await app.Services.GetRequiredService<IBlockedReleaseRepository>().BlockAsync(Refusal(Release), CancellationToken.None);

        using var scope = app.Services.CreateScope();
        await PlannerFor(app).RunBlockedReleaseCleanupAsync(
            scope.ServiceProvider.GetRequiredService<IRefusedDownloadCleanupService>(), CancellationToken.None);

        Assert.False(File.Exists(refused));
        Assert.True(File.Exists(other));
    }
}
