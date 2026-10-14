using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A retention rule's day, run through the real routes on a real temp folder with the real recycle bin and real
/// catalogue. The rule only uses a library condition (how long ago the title was added), so no media server is needed.
/// On its day the rule does exactly what it was told: the title and its files to the bin where Restore brings them back,
/// or only a note, or only unmonitoring, and in the second two nothing leaves the disk.
/// </summary>
public sealed class RetentionRulesOnDiskTests
{
    private static async Task<RealDiskLibrary> StartWorldAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return await RealDiskLibrary.OpenAsync(app);
    }

    private static async Task<(string Id, string File)> AnOldFilmAsync(RealDiskLibrary world)
    {
        var folder = Path.Combine(world.Root, "Nosferatu (1922)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Nosferatu.1922.1080p.BluRay.x264-HD4U.mkv");
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var response = await world.App.Client.PostAsJsonAsync("/api/movies/", new { title = "Nosferatu", releaseYear = 1922, monitored = true, libraryId = world.Library.Id, searchForMissingNow = false });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.True((await world.Movies.GetByIdAsync(id, CancellationToken.None))!.HasFile);
        // Added a year ago, so "added more than 30 days ago" holds.
        await world.SqlAsync("movies", $"UPDATE movie_entries SET created_utc = '{DateTimeOffset.UtcNow.AddDays(-365):O}' WHERE id = '{id}';");
        return (id, file);
    }

    private static async Task<string> RuleAsync(RealDiskLibrary world, string expiryAction)
    {
        var response = await world.App.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films",
            level = "movie",
            isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "added:beyond:30" } },
            mediaServerIds = Array.Empty<string>(),
            removeFromServers = false,
            waitDays = 1,
            removalMode = "automatic",
            checkIntervalHours = 24,
            expiryAction,
            expiryTag = expiryAction == "tag" ? "leaving" : null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CheckAsync(RealDiskLibrary world, string ruleId)
    {
        var response = await world.App.Client.PostAsync($"/api/retention/rules/{ruleId}/check", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The day arrives: the title's mark is due in the past.</summary>
    private static Task TheDayArrivesAsync(RealDiskLibrary world)
        => world.SqlAsync("platform", $"UPDATE retention_marks SET remove_after_utc = '{DateTimeOffset.UtcNow.AddHours(-1):O}';");

    [Fact]
    public async Task On_its_day_a_delete_rule_moves_the_titles_folder_to_the_recycle_bin_removes_the_title_and_restore_brings_both_back()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "delete");

        var first = await CheckAsync(world, rule);
        Assert.True(File.Exists(file), "the first check only marks it: nothing is removed before its day. " + first);
        await TheDayArrivesAsync(world);
        var second = await CheckAsync(world, rule);

        Assert.False(File.Exists(file), "the file left the library folder: " + second);
        Assert.Equal(HttpStatusCode.NotFound, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);
        var item = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.NotNull(item.TitleSnapshotId);
        var activity = await world.App.Client.GetStringAsync("/api/activity?take=50");
        Assert.Matches("Nosferatu was removed from Deluno: [^\"]*retention rule", activity);

        var restore = await world.App.Client.PostAsync($"/api/recycle-bin/{item.Id}/restore", null);
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        Assert.True(File.Exists(file));
        Assert.Equal(HttpStatusCode.OK, (await world.App.Client.GetAsync($"/api/movies/{id}")).StatusCode);
    }

    [Fact]
    public async Task On_its_day_a_just_log_rule_changes_nothing_on_disk_or_in_the_catalogue_and_says_so_once()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "log");

        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);
        await CheckAsync(world, rule);
        await CheckAsync(world, rule);

        Assert.True(File.Exists(file));
        Assert.All(File.ReadAllBytes(file), value => Assert.Equal(0x42, value));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.True(movie.Monitored);
        Assert.True(movie.HasFile);
        var activity = await world.App.Client.GetStringAsync("/api/activity?take=100");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(activity, "reached its day and was only recorded"));
    }

    [Fact]
    public async Task On_its_day_a_stop_monitoring_rule_keeps_the_files_and_the_title_and_only_stops_wanting_it()
    {
        await using var world = await StartWorldAsync();
        var (id, file) = await AnOldFilmAsync(world);
        var rule = await RuleAsync(world, "unmonitor");

        await CheckAsync(world, rule);
        await TheDayArrivesAsync(world);
        await CheckAsync(world, rule);

        Assert.True(File.Exists(file));
        Assert.Empty(await world.Bin.ListAsync(CancellationToken.None));
        var movie = (await world.Movies.GetByIdAsync(id, CancellationToken.None))!;
        Assert.False(movie.Monitored);
        Assert.True(movie.HasFile);
    }
}
