using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The retention routes when the request is right (coverage register, area api): each rule, keep and removal
/// answer what it did, and the change shows in the real database and on the real disk.
/// </summary>
public sealed class RetentionRoutesDoWhatTheySayTests
{
    private static readonly RetentionStep[] EveryFilm = [RetentionStep.KeepOnly("year:max:2100")];

    /// <summary>A number straight out of the host's real SQLite file, so a test sees what is stored and not only what is answered.</summary>
    private static async Task<long> ScalarAsync(ApplicationTestHost app, string database, string sql)
    {
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using var connection = (SqliteConnection)await factory.OpenConnectionAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static object RuleBody(string name, bool enabled = true, string removalMode = "automatic", int waitDays = 7)
        => new
        {
            name,
            level = "movie",
            isEnabled = enabled,
            steps = EveryFilm,
            mediaServerIds = Array.Empty<string>(),
            removeFromServers = false,
            waitDays,
            removalMode,
            checkIntervalHours = 8
        };

    private static async Task<JsonElement> CreateRuleAsync(ApplicationTestHost app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/api/retention/rules", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(string LibraryId, string MovieId, string FilePath)> FilmOnDiskAsync(ApplicationTestHost app, string title = "Dune", int year = 2021)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Films",
            mediaType = "movies",
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var filePath = Path.Combine(root, $"{title} ({year})", $"{title} ({year}) [Bluray-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, title, year,
            wantedStatus: "covered",
            wantedReason: "Imported from your existing library.",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            filePath: filePath,
            fileSizeBytes: 4,
            cancellationToken: CancellationToken.None);
        var movieId = (await movies.ListAsync(CancellationToken.None)).Single(m => m.Title == title).Id;
        return (libraryId, movieId, filePath);
    }

    [Fact]
    public async Task A_rule_is_created_listed_edited_and_deleted_and_each_step_shows_in_the_database()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/rules")).Replace(" ", ""));

        var created = await CreateRuleAsync(app, RuleBody("Old films"));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("Old films", created.GetProperty("name").GetString());
        Assert.Equal("movie", created.GetProperty("level").GetString());
        Assert.Equal(7, created.GetProperty("waitDays").GetInt32());
        Assert.Equal(1, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM retention_rules;"));

        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/rules");
        Assert.Equal(id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());

        var edit = await app.Client.PutAsJsonAsync($"/api/retention/rules/{id}", RuleBody("Older films", enabled: false, waitDays: 30));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await edit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Older films", edited.GetProperty("name").GetString());
        Assert.Equal(30, edited.GetProperty("waitDays").GetInt32());
        Assert.False(edited.GetProperty("isEnabled").GetBoolean());
        var reread = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/rules")).EnumerateArray());
        Assert.Equal("Older films", reread.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/retention/rules/{id}")).StatusCode);
        Assert.Equal(0, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM retention_rules;"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/retention/rules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/retention/rules/{id}", RuleBody("Gone"))).StatusCode);
    }

    [Fact]
    public async Task A_rule_with_no_name_or_an_unknown_server_is_refused_in_plain_words_and_nothing_is_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noName = await app.Client.PostAsJsonAsync("/api/retention/rules", RuleBody(""));
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("Give the rule a name.", await noName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var unknownServer = await app.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Needs a server",
            level = "movie",
            isEnabled = true,
            steps = EveryFilm,
            mediaServerIds = new[] { "no-such-server" },
            removeFromServers = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownServer.StatusCode);
        Assert.Contains("There is no media server with id", await unknownServer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync(app, DelunoDatabaseNames.Platform, "SELECT COUNT(*) FROM retention_rules;"));
    }

    [Fact]
    public async Task Keeping_a_title_lists_it_stops_the_rule_marking_it_and_undoing_the_keep_removes_it_from_the_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, movieId, _) = await FilmOnDiskAsync(app);
        var rule = await CreateRuleAsync(app, RuleBody("Old films"));
        var ruleId = rule.GetProperty("id").GetString()!;

        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/keeps")).Replace(" ", ""));

        var keep = await app.Client.PostAsJsonAsync("/api/retention/keeps", new { entityId = movieId, level = "movie", title = "Dune", ruleId, note = "Family favourite" });
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var kept = (await keep.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keep");
        Assert.Equal("Dune", kept.GetProperty("title").GetString());
        Assert.Equal("Family favourite", kept.GetProperty("note").GetString());

        var keeps = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/keeps");
        Assert.Equal(movieId, Assert.Single(keeps.EnumerateArray()).GetProperty("entityId").GetString());

        var check = await app.Client.PostAsync($"/api/retention/rules/{ruleId}/check", content: null);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/leaving-soon")).Replace(" ", ""));

        var keepId = kept.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/retention/keeps/{keepId}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/keeps")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/retention/keeps/{keepId}")).StatusCode);
    }

    [Fact]
    public async Task Keeping_a_title_with_no_id_is_refused_with_the_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/retention/keeps", new { entityId = "", level = "movie" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose a title to keep.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_added_by_hand_to_a_rule_is_marked_leaving_soon_with_the_rules_wait()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, movieId, _) = await FilmOnDiskAsync(app);
        var rule = await CreateRuleAsync(app, RuleBody("Old films", waitDays: 10));
        var ruleId = rule.GetProperty("id").GetString()!;

        var add = await app.Client.PostAsJsonAsync($"/api/retention/rules/{ruleId}/titles", new { entityId = movieId });

        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var mark = await add.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Dune", mark.GetProperty("title").GetString());
        Assert.True(mark.GetProperty("manual").GetBoolean());
        var leavingSoon = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        Assert.Equal(movieId, leavingSoon.GetProperty("entityId").GetString());
        var due = leavingSoon.GetProperty("removeAfterUtc").GetDateTimeOffset();
        Assert.InRange((due - DateTimeOffset.UtcNow).TotalDays, 9, 10.01);
    }

    [Fact]
    public async Task Adding_a_title_by_hand_that_does_not_exist_or_to_a_rule_that_is_off_is_refused_with_the_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, movieId, _) = await FilmOnDiskAsync(app);
        var on = (await CreateRuleAsync(app, RuleBody("On"))).GetProperty("id").GetString()!;
        var off = (await CreateRuleAsync(app, RuleBody("Off", enabled: false))).GetProperty("id").GetString()!;

        var missing = await app.Client.PostAsJsonAsync($"/api/retention/rules/{on}/titles", new { entityId = "not-a-film" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("Deluno has no title like that", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var wrongRule = await app.Client.PostAsJsonAsync($"/api/retention/rules/{off}/titles", new { entityId = movieId });
        Assert.Equal(HttpStatusCode.BadRequest, wrongRule.StatusCode);
        Assert.Contains("Turn the rule on first", await wrongRule.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/leaving-soon")).Replace(" ", ""));
    }

    [Fact]
    public async Task Confirming_a_ready_title_removes_its_file_from_the_disk_into_the_recycle_bin_and_asks_the_rule_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, movieId, filePath) = await FilmOnDiskAsync(app);
        var rule = await CreateRuleAsync(app, RuleBody("Old films", removalMode: "ask", waitDays: 1));
        var ruleId = rule.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync($"/api/retention/rules/{ruleId}/check", content: null)).StatusCode);

        // Its day arrives.
        var repository = app.Services.GetRequiredService<IRetentionRepository>();
        var mark = Assert.Single(await repository.ListMarksAsync(ruleId, CancellationToken.None));
        Assert.Equal(movieId, mark.EntityId);
        await repository.UpsertMarkAsync(mark with { RemoveAfterUtc = DateTimeOffset.UtcNow.AddHours(-1) }, CancellationToken.None);
        var second = await app.Client.PostAsync($"/api/retention/rules/{ruleId}/check", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("ready", (await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray().Single().GetProperty("state").GetString());
        Assert.True(File.Exists(filePath), "asking first means nothing is removed before a person confirms");

        var confirm = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/remove", new { ruleId, entityIds = new[] { movieId } });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var run = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, run.GetProperty("removedCount").GetInt32());
        Assert.False(File.Exists(filePath), "the confirmed title's file leaves the library folder");
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/leaving-soon")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/movies/{movieId}")).StatusCode);
    }

    [Fact]
    public async Task Confirming_for_a_rule_that_does_not_exist_answers_not_found_and_removes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (_, movieId, filePath) = await FilmOnDiskAsync(app);

        var response = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/remove", new { ruleId = "no-such-rule", entityIds = new[] { movieId } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task Undoing_an_episode_removal_forgets_the_record_wants_the_episode_again_and_says_so_in_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Shows", mediaType = "tv", purpose = "collection", rootPath = root, importWorkflow = "copy",
            autoSearchEnabled = false, missingSearchEnabled = false, upgradeSearchEnabled = false
        });
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var episodePath = Path.Combine(root, "The Wire", "Season 01", "The Wire S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(episodePath)!);
        await File.WriteAllBytesAsync(episodePath, [1, 2, 3, 4]);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId, "The Wire", 2002, "covered", "Imported.", "Bluray-1080p", "Bluray-1080p", true, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: episodePath, FileSizeBytes: 4)],
            CancellationToken.None);
        var seriesId = Assert.Single(await series.ListAsync(CancellationToken.None)).Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;
        await series.UpdateEpisodeMonitoredAsync([episodeId], false, CancellationToken.None);
        var repository = app.Services.GetRequiredService<IRetentionRepository>();
        await repository.SaveEpisodeRemovalAsync(
            new RetentionEpisodeRemoval(episodeId, seriesId, 1, 1, "rule-1", "Watched episodes", "Watched by everyone", DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Single(await repository.ListEpisodeRemovalsAsync(seriesId, CancellationToken.None));

        var undo = await app.Client.DeleteAsync($"/api/retention/episode-removals/{episodeId}");

        Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        Assert.Empty(await repository.ListEpisodeRemovalsAsync(seriesId, CancellationToken.None));
        Assert.True((await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().Monitored, "the episode is wanted again");
        var activity = await app.Client.GetStringAsync("/api/activity");
        Assert.Contains("is wanted again: season 1, episode 1", activity, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/retention/episode-removals/{episodeId}")).StatusCode);
        Assert.True(File.Exists(episodePath), "undoing a removal record never touches files");
    }
}
