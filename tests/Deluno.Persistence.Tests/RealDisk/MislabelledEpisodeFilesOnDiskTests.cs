using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The repair for an episode file whose name carries another episode's title, run on real files with the real recycle
/// bin and the real catalogue. The wrong file goes to the bin (Restore works and links it back), the episode is Missing
/// and searched, and a file that only looks different, or none that is wrong, is never touched.
/// </summary>
public sealed class MislabelledEpisodeFilesOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    /// <summary>A show with two episode files; the first file's own name carries the second episode's title.</summary>
    private static async Task<(string ShowId, string WrongFile, string RightFile)> AShowWithAMislabelledFileAsync(RealDiskLibrary world)
    {
        var root = Path.Combine(world.App.DataRoot, "tv");
        Directory.CreateDirectory(root);
        var created = await world.App.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "tv", mediaType = "tv", purpose = "collection", rootPath = root, downloadsPath = (string?)null, qualityProfileId = (string?)null,
            importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null, processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        using var libraryDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var libraryId = libraryDocument.RootElement.GetProperty("id").GetString()!;
        var season = Path.Combine(root, "Nosferatu (1922)", "Season 01");
        Directory.CreateDirectory(season);
        var wrong = Path.Combine(season, "Nosferatu.S01E01.The.Second.Title.1080p.WEB-DL.x264-HD4U.mkv");
        var right = Path.Combine(season, "Nosferatu.S01E02.The.Second.Title.1080p.WEB-DL.x264-HD4U.mkv");
        File.WriteAllBytes(wrong, Enumerable.Repeat((byte)0x31, 2048).ToArray());
        File.WriteAllBytes(right, Enumerable.Repeat((byte)0x32, 2048).ToArray());
        var added = await world.App.Client.PostAsJsonAsync("/api/series/", new { title = "Nosferatu", startYear = 1922, monitored = true, libraryId, searchForMissingNow = false });
        using var showDocument = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var showId = showDocument.RootElement.GetProperty("id").GetString()!;
        await world.SqlAsync("series", "UPDATE episode_entries SET title = 'The First Title' WHERE episode_number = 1; UPDATE episode_entries SET title = 'The Second Title' WHERE episode_number = 2;");
        return (showId, wrong, right);
    }

    private static Task<int> RunRepairAsync(RealDiskLibrary world)
    {
        using var scope = world.App.Services.CreateScope();
        var services = scope.ServiceProvider;
        return MislabelledEpisodeFileRepairService.RunAsync(
            services.GetRequiredService<ISeriesCatalogRepository>(),
            services.GetRequiredService<ILibrariesRepository>(),
            world.Bin,
            services.GetRequiredService<IJobQueueRepository>(),
            services.GetRequiredService<IJobScheduler>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_file_whose_name_carries_another_episodes_title_goes_to_the_recycle_bin_the_episode_is_searched_and_restore_links_it_back()
    {
        await using var world = await StartWorldAsync();
        var (showId, wrong, right) = await AShowWithAMislabelledFileAsync(world);
        var shows = world.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        Assert.Equal(2, (await shows.ListEpisodeFileLinksAsync(CancellationToken.None)).Count);

        var repaired = await RunRepairAsync(world);

        Assert.Equal(1, repaired);
        Assert.False(File.Exists(wrong), "the wrong file left the library folder");
        Assert.True(File.Exists(right), "the right episode's file is untouched");
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(wrong, item.OriginalPath);
        Assert.Equal(2048, new FileInfo(item.RecyclePath).Length);
        var remaining = Assert.Single(await shows.ListEpisodeFileLinksAsync(CancellationToken.None));
        Assert.Equal(right, remaining.FilePath);
        var searches = (await world.App.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None)).Where(job => job.Source == "mislabelled-file").ToArray();
        Assert.Single(searches);
        var activity = await world.App.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("held a copy of S01E02", activity);
        Assert.True((await shows.GetByIdAsync(showId, CancellationToken.None))!.Monitored);

        // A second run finds nothing more to do.
        Assert.Equal(0, await RunRepairAsync(world));

        // Restore puts the file back and links the episode to it again.
        var restore = await world.App.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.True(File.Exists(wrong));
        Assert.Equal(2, (await shows.ListEpisodeFileLinksAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_file_whose_name_carries_its_own_episodes_title_is_never_touched()
    {
        await using var world = await StartWorldAsync();
        var (_, wrong, right) = await AShowWithAMislabelledFileAsync(world);
        // The same two files, with the right title in the first one's name.
        var corrected = wrong.Replace("The.Second.Title", "The.First.Title");
        File.Move(wrong, corrected);
        await world.SqlAsync("series", $"UPDATE episode_entries SET file_path = '{corrected.Replace("'", "''")}' WHERE file_path = '{wrong.Replace("'", "''")}';");

        var repaired = await RunRepairAsync(world);

        Assert.Equal(0, repaired);
        Assert.True(File.Exists(corrected));
        Assert.True(File.Exists(right));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }

    // DEFECT (the register row says so): the repair is a plain background service that runs at start-up and every six hours
    // whatever the automatic-jobs switch says, so on a fresh install (switch off) it still moves a person's file to the bin.
    [Fact]
    public async Task A_mislabelled_file_is_left_alone_while_automatic_jobs_are_off()
    {
        await using var world = await StartWorldAsync();
        var (_, wrong, _) = await AShowWithAMislabelledFileAsync(world);
        var service = new MislabelledEpisodeFileRepairService(world.App.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MislabelledEpisodeFileRepairService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => !File.Exists(wrong), "the repair's first pass to run", TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(wrong), "with automatic jobs off nothing may move a person's file");
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
    }
}
