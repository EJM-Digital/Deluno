using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The recurring clean-up passes of the work planner, run against a real host on a real temp folder: each claims its
/// slot in the jobs database, deletes what it should from the real disk, leaves the rest, and records a plain
/// outcome the System screen shows.
/// </summary>
public sealed class ScheduledCleanupPassesOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static WorkPlanner PlannerFor(IServiceScope scope)
        => new(
            NullLogger<WorkPlanner>.Instance,
            scope.ServiceProvider.GetRequiredService<IJobQueueRepository>(),
            new ConfigurationBuilder().Build(),
            TimeProvider.System);

    private static async Task<SystemTaskState> TaskStateAsync(IServiceScope scope, string key)
        => (await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .Single(state => state.ScheduleKey == key);

    private static string LibraryFile(RealDiskLibrary world, string name, int length)
    {
        var folder = Path.Combine(world.Root, Path.GetFileNameWithoutExtension(name));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x11, length).ToArray());
        return path;
    }

    [Fact]
    public async Task The_recycle_bin_pass_deletes_only_expired_items_from_disk_and_records_a_completed_pass()
    {
        await using var world = await StartWorldAsync();
        var oldFile = LibraryFile(world, "Old (2001).mkv", 5000);
        var freshFile = LibraryFile(world, "Fresh (2002).mkv", 3000);
        var old = (await world.Bin.StoreReplacementAsync(world.Library, oldFile, oldFile, CancellationToken.None))!;
        var fresh = (await world.Bin.StoreReplacementAsync(world.Library, freshFile, freshFile, CancellationToken.None))!;
        var statePath = Path.Combine(world.App.DataRoot, "recycle-bin", "state.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        foreach (var item in state["items"]!.AsArray())
        {
            if (item!["id"]!.GetValue<string>() == old.Id) item["expiresUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        }

        File.WriteAllText(statePath, state.ToJsonString());
        using var scope = world.App.Services.CreateScope();

        await PlannerFor(scope).RunRecycleBinCleanupAsync(world.Bin, CancellationToken.None);

        Assert.False(File.Exists(old.RecyclePath), "the expired item is really gone from disk");
        Assert.True(File.Exists(fresh.RecyclePath), "an item still inside its keep period stays");
        Assert.Equal(fresh.Id, Assert.Single(await world.Bin.ListAsync(CancellationToken.None)).Id);
        var recorded = await TaskStateAsync(scope, SystemTasks.RecycleBinCleanup);
        Assert.Equal("completed", recorded.LastResult);
    }

    [Fact]
    public async Task The_artwork_pass_deletes_only_old_unreferenced_artwork_files_and_writes_an_activity_line()
    {
        await using var world = await StartWorldAsync();
        var artworkRoot = Path.Combine(world.App.DataRoot, "artwork-cache");
        Directory.CreateDirectory(artworkRoot);
        var orphanKey = new string('b', 64);
        var youngKey = new string('c', 64);
        var orphan = Path.Combine(artworkRoot, orphanKey + ".jpg");
        var young = Path.Combine(artworkRoot, youngKey + ".jpg");
        File.WriteAllBytes(orphan, [4, 5, 6, 7, 8]);
        File.WriteAllBytes(young, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(young, DateTime.UtcNow.AddHours(-1));
        async Task InsertAsync(string key, string path, DateTimeOffset fetched)
            => await world.SqlAsync("cache", $"INSERT INTO artwork_cache (cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc) VALUES ('{key}', 'movies', 'https://images.deluno.test/art.jpg', '{path.Replace("'", "''")}', '{fetched:O}', NULL);");
        await InsertAsync(orphanKey, orphan, DateTimeOffset.UtcNow.AddDays(-3));
        await InsertAsync(youngKey, young, DateTimeOffset.UtcNow.AddHours(-1));
        using var scope = world.App.Services.CreateScope();
        var services = scope.ServiceProvider;

        await PlannerFor(scope).RunArtworkCacheCleanupAsync(
            services.GetRequiredService<TmdbMetadataProvider>(),
            services.GetRequiredService<IMovieCatalogRepository>(),
            services.GetRequiredService<ISeriesCatalogRepository>(),
            TimeProvider.System,
            services.GetRequiredService<IActivityFeedRepository>(),
            CancellationToken.None);

        Assert.False(File.Exists(orphan), "old artwork nothing references is deleted");
        Assert.True(File.Exists(young), "artwork inside the 24 hour grace period stays");
        var activity = await services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "metadata.artwork.cleanup" && entry.Message.Contains("reclaimed 5 bytes"));
        Assert.Equal("completed", (await TaskStateAsync(scope, SystemTasks.ArtworkCacheCleanup)).LastResult);
    }

    [Fact]
    public async Task The_unmatched_give_up_pass_deletes_a_download_area_file_and_records_a_completed_pass_only_when_the_policy_says_so()
    {
        await using var world = await StartWorldAsync();
        var leftover = Path.Combine(world.Downloads, "Sintel.2010.mkv");
        File.WriteAllText(leftover, "an unmatched file");
        var movie = await world.AddMovieAsync("Sintel", 2010);
        var dispatch = await world.RecordDispatchAsync(movie.Id, "Sintel 2010 WEB 1080p");
        var created = await world.Movies.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest("Sintel", ImportFailurePolicy.Unmatched, "Deluno could not match this file.", "Choose the film.",
                JsonSerializer.Serialize(new { DispatchId = dispatch, SourcePath = leftover })),
            CancellationToken.None);
        using var scope = world.App.Services.CreateScope();
        var giveUp = scope.ServiceProvider.GetRequiredService<IUnmatchedImportGiveUpService>();

        // The default policy holds the case: the pass runs and touches nothing.
        await PlannerFor(scope).RunUnmatchedImportGiveUpAsync(giveUp, CancellationToken.None);
        Assert.True(File.Exists(leftover));
        Assert.Equal("completed", (await TaskStateAsync(scope, SystemTasks.UnmatchedImportGiveUp)).LastResult);

        // Give up after one day, with the case a day and a bit old: the next pass removes the file.
        var settings = (await world.App.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        settings["unmatchedImportPolicy"] = UnmatchedImportPolicies.GiveUpAfterDays;
        settings["unmatchedImportGiveUpDays"] = 1;
        Assert.True((await world.App.Client.PutAsJsonAsync("/api/settings", settings)).IsSuccessStatusCode);
        await world.SqlAsync("movies", $"UPDATE movie_import_recovery_cases SET detected_utc = '{DateTimeOffset.UtcNow.AddDays(-2):O}' WHERE id = '{created.Id}';");
        await world.SqlAsync("jobs", $"DELETE FROM worker_schedule_state WHERE schedule_key = '{SystemTasks.UnmatchedImportGiveUp}';");

        await PlannerFor(scope).RunUnmatchedImportGiveUpAsync(giveUp, CancellationToken.None);

        Assert.False(File.Exists(leftover), "the overdue unmatched file in a download area was deleted");
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<IBlockedReleaseRepository>().ListAsync(CancellationToken.None), blocked => blocked.ReleaseName == "Sintel 2010 WEB 1080p");
        Assert.DoesNotContain((await world.Movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases, item => item.Id == created.Id && item.Status == "open");
        Assert.Equal("completed", (await TaskStateAsync(scope, SystemTasks.UnmatchedImportGiveUp)).LastResult);
    }
}
