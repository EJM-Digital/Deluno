using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A retention rule that deletes, against a real library folder and the real recycle bin: nothing leaves the library while a title
/// is only marked, while it waits, or when the rule's action is only to write it down; when a person confirms, exactly the titles
/// named go to the recycle bin (never a title that is not ready) and everything else stays where it was.
/// </summary>
public sealed class RetentionRemovalOnRealFilesTests
{
    [Fact]
    public async Task A_check_marks_the_matching_film_as_leaving_soon_and_moves_no_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "ask", expiryAction: "delete");

        var run = await CheckAsync(app, rule);

        var marked = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon");
        var only = Assert.Single(marked.EnumerateArray());
        Assert.Equal(world.DoomedId, only.GetProperty("entityId").GetString());
        Assert.Equal("leaving", only.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("matchedCount").GetInt32());
        Assert.True(File.Exists(world.DoomedFile));
        Assert.True(File.Exists(world.SafeFile));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task When_the_wait_is_over_an_ask_first_rule_makes_it_ready_but_removes_nothing_until_a_person_confirms()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "ask", expiryAction: "delete");
        await CheckAsync(app, rule);
        PassTheWait(app);

        await CheckAsync(app, rule);

        var marked = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon");
        Assert.Equal("ready", Assert.Single(marked.EnumerateArray()).GetProperty("state").GetString());
        Assert.True(File.Exists(world.DoomedFile), "ready is not removed");
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.True(await HasTitleAsync(app, world.DoomedId));
    }

    [Fact]
    public async Task Confirming_a_ready_title_sends_its_files_to_the_recycle_bin_and_leaves_every_other_film_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "ask", expiryAction: "delete");
        await CheckAsync(app, rule);
        PassTheWait(app);
        await CheckAsync(app, rule);

        using var response = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/remove", new { ruleId = rule, entityIds = new[] { world.DoomedId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, run.GetProperty("removedCount").GetInt32());
        Assert.False(File.Exists(world.DoomedFile), "the film's file left the library");
        var binned = Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.Equal(world.DoomedFolder, binned.OriginalPath);
        Assert.True(Directory.Exists(binned.RecyclePath) || File.Exists(binned.RecyclePath), "it is held in the recycle bin, not deleted");
        Assert.True(File.Exists(world.SafeFile), "another film is untouched");
        Assert.False(await HasTitleAsync(app, world.DoomedId));
        Assert.True(await HasTitleAsync(app, world.SafeId));
        // Nothing may add it back from a list: the removal is written as an exclusion.
        Assert.Contains("Doomed", await app.Client.GetStringAsync("/api/exclusions"));
        Assert.Contains("Doomed", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task Confirming_a_title_that_is_not_ready_removes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "ask", expiryAction: "delete");
        await CheckAsync(app, rule);

        // Marked and still waiting, and a title the rule never marked at all.
        using var response = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/remove", new { ruleId = rule, entityIds = new[] { world.DoomedId, world.SafeId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removedCount").GetInt32());
        Assert.True(File.Exists(world.DoomedFile));
        Assert.True(File.Exists(world.SafeFile));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rule_that_only_writes_it_down_never_removes_a_file_on_the_day()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "automatic", expiryAction: "log");
        await CheckAsync(app, rule);
        PassTheWait(app);

        await CheckAsync(app, rule);

        Assert.True(File.Exists(world.DoomedFile));
        Assert.True(File.Exists(world.SafeFile));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.True(await HasTitleAsync(app, world.DoomedId));
        Assert.Contains("Doomed", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task A_title_the_person_keeps_leaves_the_list_and_is_not_removed_on_the_day()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "automatic", expiryAction: "delete");
        await CheckAsync(app, rule);
        // Its day has come, and only then does the person say to keep it.
        PassTheWait(app);
        using var kept = await app.Client.PostAsJsonAsync("/api/retention/keeps", new { entityId = world.DoomedId, level = "movie", ruleId = (string?)null, note = "family favourite" });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        await CheckAsync(app, rule);

        Assert.True(File.Exists(world.DoomedFile));
        Assert.Empty(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        Assert.True(await HasTitleAsync(app, world.DoomedId));
    }

    [Fact]
    public async Task An_automatic_delete_rule_removes_on_the_day_what_still_matches_into_the_recycle_bin()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var world = await WorldAsync(app);
        var rule = await SaveRuleAsync(app, removalMode: "automatic", expiryAction: "delete");
        await CheckAsync(app, rule);
        Assert.True(File.Exists(world.DoomedFile), "the first check only marks it");
        PassTheWait(app);

        await CheckAsync(app, rule);

        Assert.False(File.Exists(world.DoomedFile));
        Assert.Single(await app.Services.GetRequiredService<IRecycleBinService>().ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.SafeFile));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record World(string DoomedId, string SafeId, string DoomedFile, string SafeFile, string DoomedFolder);

    private static async Task<World> WorldAsync(ApplicationTestHost app)
    {
        var root = Path.Combine(app.DataRoot, "movies");
        Directory.CreateDirectory(root);
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            downloadsPath = (string?)null,
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

        var (doomedId, doomedFile, doomedFolder) = await AddFilmAsync(app, root, libraryId, "Doomed Film", 1931);
        var (safeId, safeFile, _) = await AddFilmAsync(app, root, libraryId, "Safe Film", 1932);
        return new World(doomedId, safeId, doomedFile, safeFile, doomedFolder);
    }

    private static async Task<(string Id, string File, string Folder)> AddFilmAsync(ApplicationTestHost app, string root, string libraryId, string title, int year)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, $"{title} ({year})")).FullName;
        var file = Path.Combine(folder, $"{title.Replace(' ', '.')}.{year}.1080p.BluRay.x264-HD4U.mkv");
        await File.WriteAllTextAsync(file, "not really a film " + title);
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.True((await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None))!.HasFile, "the file in its folder is linked when the title is added");
        return (id, file, folder);
    }

    private static async Task<string> SaveRuleAsync(ApplicationTestHost app, string removalMode, string expiryAction)
    {
        using var response = await app.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old doomed films",
            level = "movie",
            isEnabled = true,
            steps = new[] { new { type = "condition", op = "keep", condition = "title:has:Doomed" } },
            mediaServerIds = Array.Empty<string>(),
            removeFromServers = false,
            waitDays = 1,
            removalMode,
            checkIntervalHours = 8,
            expiryAction
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CheckAsync(ApplicationTestHost app, string ruleId)
    {
        using var response = await app.Client.PostAsync($"/api/retention/rules/{ruleId}/check", content: null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The wait is a day at least; the marks are dated back so the next check finds the day has come.</summary>
    private static void PassTheWait(ApplicationTestHost app)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataRoot, "platform.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_marks SET remove_after_utc = @past, first_matched_utc = @past;";
        command.Parameters.AddWithValue("@past", DateTimeOffset.UtcNow.AddDays(-3).ToString("o"));
        Assert.True(command.ExecuteNonQuery() > 0, "there was a mark to date back");
    }

    private static async Task<bool> HasTitleAsync(ApplicationTestHost app, string id)
        => await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None) is not null;
}
