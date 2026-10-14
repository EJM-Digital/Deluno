using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Leftover (orphan) download folders against real folders on disk: what Deluno itself downloaded and nothing uses can be
/// removed; anything else stays. Keep, Remove and the "Run now" of the leftover-downloads task all end in what is left in the
/// download folder.
/// </summary>
public sealed class LeftoverDownloadFoldersOnRealFilesTests
{
    private const string OursName = "Some.Movie.2020.1080p.WEB";
    private const string ForeignName = "Not.Ours.2021.1080p.WEB";

    [Fact]
    public async Task Only_a_folder_Deluno_downloaded_is_listed_and_one_someone_else_put_there_never_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (downloads, ours, foreign) = await SeedAsync(app, oursAgeHours: 5);

        var listed = await ListAsync(app);

        Assert.Equal(ours, Assert.Single(listed));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(downloads));
    }

    [Fact]
    public async Task Remove_deletes_the_folder_Deluno_downloaded_and_leaves_everything_else_in_the_download_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, ours, foreign) = await SeedAsync(app, oursAgeHours: 5);

        using var response = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path = ours });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Removed {OursName}.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.False(Directory.Exists(ours));
        Assert.True(File.Exists(Path.Combine(foreign, "keep-me.mkv")));
        Assert.Contains(OursName, await app.Client.GetStringAsync("/api/activity"));
    }

    [Fact]
    public async Task Remove_refuses_a_folder_Deluno_did_not_download_and_deletes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, ours, foreign) = await SeedAsync(app, oursAgeHours: 5);

        using var response = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path = foreign });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("That is not an orphaned download any more.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.True(File.Exists(Path.Combine(foreign, "keep-me.mkv")));
        Assert.True(File.Exists(Path.Combine(ours, "movie.mkv")));
    }

    [Fact]
    public async Task Remove_refuses_a_path_that_climbs_out_of_the_download_folder()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (downloads, ours, _) = await SeedAsync(app, oursAgeHours: 5);
        var outside = Path.Combine(app.DataRoot, "precious");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "film.mkv"), "precious");

        foreach (var path in new[] { Path.Combine(downloads, "..", "precious"), outside, app.DataRoot })
        {
            using var response = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        Assert.Equal("precious", await File.ReadAllTextAsync(Path.Combine(outside, "film.mkv")));
        Assert.True(Directory.Exists(ours));
    }

    [Fact]
    public async Task Keep_leaves_the_folder_on_disk_and_stops_it_being_listed_or_removable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, ours, _) = await SeedAsync(app, oursAgeHours: 5);

        using var kept = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/keep", new { path = ours });
        using var removeAfter = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path = ours });

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal($"Kept {OursName}.", (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Empty(await ListAsync(app));
        Assert.Equal(HttpStatusCode.Conflict, removeAfter.StatusCode);
        Assert.True(File.Exists(Path.Combine(ours, "movie.mkv")));
    }

    [Fact]
    public async Task A_folder_younger_than_the_minimum_age_is_not_an_orphan_yet()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, ours, _) = await SeedAsync(app, oursAgeHours: 0);

        Assert.Empty(await ListAsync(app));
        using var response = await app.Client.PostAsJsonAsync("/api/download-clients/orphans/remove", new { path = ours });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(ours, "movie.mkv")));
    }

    [Fact]
    public async Task Run_now_on_the_leftover_task_removes_nothing_while_the_switch_is_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, ours, _) = await SeedAsync(app, oursAgeHours: 48);

        using var response = await app.Client.PostAsync("/api/download-clients/leftovers/cleanup", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("removed").GetInt32());
        Assert.Equal(0, body.GetProperty("held").GetInt32());
        Assert.True(File.Exists(Path.Combine(ours, "movie.mkv")));
    }

    [Fact]
    public async Task Run_now_with_the_switch_on_removes_the_old_leftover_and_holds_the_young_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (downloads, oldOurs, foreign) = await SeedAsync(app, oursAgeHours: 48);
        // A second download of ours, but only five hours old: older than the minimum age, younger than the cleanup age.
        var young = await AddOursAsync(app, downloads, "Young.Movie.2024.1080p.WEB", ageHours: 5);
        (await app.Client.PatchAsJsonAsync("/api/settings", new { leftoverDownloadCleanupEnabled = true, leftoverDownloadCleanupAgeHours = 24 })).EnsureSuccessStatusCode();

        using var response = await app.Client.PostAsync("/api/download-clients/leftovers/cleanup", content: null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("removed").GetInt32());
        Assert.Equal(1, body.GetProperty("held").GetInt32());
        Assert.False(Directory.Exists(oldOurs));
        Assert.True(File.Exists(Path.Combine(young, "movie.mkv")));
        Assert.True(File.Exists(Path.Combine(foreign, "keep-me.mkv")));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<IReadOnlyList<string>> ListAsync(ApplicationTestHost app)
    {
        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-clients/orphans");
        return listed.EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToArray();
    }

    private static async Task<(string Downloads, string Ours, string Foreign)> SeedAsync(ApplicationTestHost app, int oursAgeHours)
    {
        var library = Path.Combine(app.DataRoot, "movies");
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(downloads);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies",
            mediaType = "movies",
            purpose = "collection",
            rootPath = library,
            downloadsPath = downloads,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });
        created.EnsureSuccessStatusCode();
        var libraryId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var ours = await AddOursAsync(app, downloads, OursName, oursAgeHours, libraryId);
        var foreign = Path.Combine(downloads, ForeignName);
        Directory.CreateDirectory(foreign);
        var foreignFile = Path.Combine(foreign, "keep-me.mkv");
        await File.WriteAllTextAsync(foreignFile, "not ours");
        Age(foreign, foreignFile, 72);
        return (downloads, ours, foreign);
    }

    private static async Task<string> AddOursAsync(ApplicationTestHost app, string downloads, string name, int ageHours, string? libraryId = null)
    {
        var folder = Path.Combine(downloads, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "movie.mkv");
        await File.WriteAllTextAsync(file, "the film");
        Age(folder, file, ageHours);
        // Deluno sent this release to a client, which is what lets it say the folder is its own.
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId ?? "lib-1", "movies", "movie", Guid.NewGuid().ToString("N"), name, "Indexer", "sabnzbd", "SABnzbd", "queued", null,
            cancellationToken: CancellationToken.None);
        return folder;
    }

    private static void Age(string folder, string file, int hours)
    {
        var then = DateTime.UtcNow.AddHours(-hours);
        File.SetLastWriteTimeUtc(file, then);
        Directory.SetLastWriteTimeUtc(folder, then);
    }
}
