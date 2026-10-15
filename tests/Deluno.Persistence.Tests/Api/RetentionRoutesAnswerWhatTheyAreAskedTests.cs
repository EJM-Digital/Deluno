using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The retention routes in the cases RetentionRoutesDoWhatTheySayTests does not walk (coverage register, area engines): the fields a rule may ask at each
/// level, every way a rule is refused when it is saved, the builder's trial that saves nothing, Run now, and the lists a person reads afterwards.
/// </summary>
public sealed class RetentionRoutesAnswerWhatTheyAreAskedTests
{
    private static readonly RetentionStep[] EveryFilm = [RetentionStep.KeepOnly("year:max:2100")];

    private static object Rule(string name, object? steps = null, string removalMode = "ask", int waitDays = 7, string[]? servers = null, bool removeFromServers = false, string? expiryAction = null, string? expiryTag = null, int checkIntervalHours = 8)
        => new
        {
            name, level = "movie", isEnabled = true, steps = steps ?? EveryFilm, mediaServerIds = servers ?? [], removeFromServers,
            waitDays, removalMode, checkIntervalHours, expiryAction, expiryTag
        };

    private static async Task<JsonElement> CreateAsync(ApplicationTestHost app, object rule)
    {
        var response = await app.Client.PostAsJsonAsync("/api/retention/rules", rule);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string[]> ProblemsOfAsync(ApplicationTestHost app, object rule)
    {
        var response = await app.Client.PostAsJsonAsync("/api/retention/rules", rule);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("rule").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task The_fields_a_rule_may_ask_follow_its_level_merge_Delunos_own_filter_fields_with_the_server_and_disk_ones_and_an_unknown_level_follows_the_media_type()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        async Task<JsonElement> FieldsAsync(string query) => await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/fields{query}");
        string[] Ids(JsonElement fields) => fields.EnumerateArray().Select(field => field.GetProperty("id").GetString()!).ToArray();
        string[] Sources(JsonElement fields) => fields.EnumerateArray().Select(field => field.GetProperty("source").GetString()!).Distinct().Order().ToArray();

        var movie = await FieldsAsync("?level=movie");
        var show = await FieldsAsync("?level=show");
        var episode = await FieldsAsync("?level=episode");
        var guessedTv = await FieldsAsync("?level=nonsense&mediaType=tv");
        var guessedFilm = await FieldsAsync("?level=nonsense");

        Assert.Contains("deluno", Sources(movie));
        Assert.Contains("server", Sources(movie));
        Assert.Contains("disk", Sources(movie));
        Assert.Contains("server.timesViewed", Ids(movie));
        Assert.NotEqual(Ids(movie), Ids(show));
        Assert.NotEqual(Ids(show), Ids(episode));
        Assert.Contains("server.watchedEpisodes", Ids(show));
        Assert.DoesNotContain("server.watchedEpisodes", Ids(movie));
        Assert.Equal(Ids(show), Ids(guessedTv));
        Assert.Equal(Ids(movie), Ids(guessedFilm));
        Assert.True(Ids(movie).Length > movie.EnumerateArray().Count(field => field.GetProperty("source").GetString() != "deluno"), "Delunos own filter fields are merged in");
    }

    [Fact]
    public async Task A_rule_is_refused_in_plain_words_for_each_thing_wrong_with_it_and_a_good_one_is_created_with_201()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Contains("The rule has no steps. A rule with none would select everything.", await ProblemsOfAsync(app, Rule("No steps", steps: Array.Empty<RetentionStep>())));
        Assert.Contains("Step 1 of the rule has no condition.", await ProblemsOfAsync(app, Rule("Empty step", steps: new[] { new RetentionStep("condition", "keep", " ") })));
        var server = await ProblemsOfAsync(app, Rule("Server field", steps: new[] { RetentionStep.KeepOnly("server.ratingImdb:min:7") }));
        Assert.Contains("“IMDb rating” comes from media servers. Choose the servers this rule reads.", server);
        Assert.Contains("“IMDb rating” cannot be compared with “bogus”.", await ProblemsOfAsync(app, Rule("Operator", steps: new[] { RetentionStep.KeepOnly("server.ratingImdb:bogus:7") })));
        Assert.Contains("“IMDb rating” needs a value to compare with.", await ProblemsOfAsync(app, Rule("Value", steps: new[] { RetentionStep.KeepOnly("server.ratingImdb:min:") })));
        Assert.Contains("“Times requested” comes from Seerr. Connect a Seerr server under Media Servers first.", await ProblemsOfAsync(app, Rule("Seerr", steps: new[] { RetentionStep.KeepOnly("seerr.timesRequested:min:1") })));
        Assert.Contains("There is no media server with id “nope”.", await ProblemsOfAsync(app, Rule("Server id", servers: ["nope"])));
        Assert.Contains("Choose the media servers to tell when a title is removed, or turn that off.", await ProblemsOfAsync(app, Rule("Tell servers", removeFromServers: true)));
        Assert.Contains("Choose what happens on the day: remove it, stop monitoring it, only write it down, or tag it.", await ProblemsOfAsync(app, Rule("Action", expiryAction: "explode")));
        Assert.Contains("Name the tag to give a title on its day.", await ProblemsOfAsync(app, Rule("Tag", expiryAction: "tag", expiryTag: " ")));
        Assert.Contains($"A title waits {RetentionLimits.MinWaitDays} to {RetentionLimits.MaxWaitDays} days before it goes. There is always a warning first.", await ProblemsOfAsync(app, Rule("Wait", waitDays: RetentionLimits.MaxWaitDays + 1)));
        Assert.Contains($"A rule is checked every {RetentionLimits.MinCheckIntervalHours} to {RetentionLimits.MaxCheckIntervalHours} hours.", await ProblemsOfAsync(app, Rule("Interval", checkIntervalHours: RetentionLimits.MaxCheckIntervalHours + 1)));
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/rules")).Replace(" ", ""));

        var created = await CreateAsync(app, Rule("Good rule", expiryAction: "tag", expiryTag: "leaving"));
        Assert.Equal("Good rule", created.GetProperty("name").GetString());
        Assert.Equal("tag", created.GetProperty("expiryAction").GetString());
        Assert.Equal("leaving", created.GetProperty("expiryTag").GetString());
    }

    [Fact]
    public async Task The_trial_counts_what_a_rule_as_drawn_would_select_now_with_its_size_and_titles_and_saves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, film.LibraryId);

        var all = await app.Client.PostAsJsonAsync("/api/retention/rules/trial", Rule("Anything"));
        var oldOnly = await app.Client.PostAsJsonAsync("/api/retention/rules/trial", Rule("Old films", steps: new[] { RetentionStep.KeepOnly("year:max:2018") }));

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var everything = await all.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, everything.GetProperty("considered").GetInt32());
        Assert.Equal(2, everything.GetProperty("matchedCount").GetInt32());
        Assert.Equal(8, everything.GetProperty("matchedBytes").GetInt64());
        Assert.Equal(["Arrival", "Dune"], everything.GetProperty("sample").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).ToArray());
        Assert.Equal(1, everything.GetProperty("stepCounts").GetArrayLength());
        Assert.Equal(0, everything.GetProperty("undecidedCount").GetInt32());
        Assert.Equal(0, everything.GetProperty("unreachableServers").GetArrayLength());
        var old = await oldOnly.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, old.GetProperty("matchedCount").GetInt32());
        Assert.Equal("Arrival", Assert.Single(old.GetProperty("sample").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/rules")).Replace(" ", ""));
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/retention/leaving-soon")).Replace(" ", ""));
    }

    [Fact]
    public async Task Run_now_runs_the_same_check_as_the_schedule_for_the_person_and_an_unknown_rule_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var rule = await CreateAsync(app, Rule("Old films"));
        var id = rule.GetProperty("id").GetString()!;

        var byHand = await app.Client.PostAsync($"/api/retention/rules/{id}/check", content: null);
        var missing = await app.Client.PostAsync("/api/retention/rules/no-such-rule/check", content: null);

        Assert.Equal(HttpStatusCode.OK, byHand.StatusCode);
        var ran = await byHand.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("person", ran.GetProperty("trigger").GetString());
        Assert.Equal(1, ran.GetProperty("matchedCount").GetInt32());
        var scheduled = await app.Services.GetRequiredService<RetentionService>().CheckAsync((await app.Services.GetRequiredService<IRetentionRepository>().GetRuleAsync(id, CancellationToken.None))!, "schedule", CancellationToken.None);
        Assert.Equal("schedule", scheduled.Trigger);
        Assert.Equal(ran.GetProperty("matchedCount").GetInt32(), scheduled.MatchedCount);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task What_a_rule_ran_what_is_leaving_soon_and_a_titles_own_retention_status_can_each_be_read_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var rule = await CreateAsync(app, Rule("Old films"));
        var id = rule.GetProperty("id").GetString()!;
        var run = await (await app.Client.PostAsync($"/api/retention/rules/{id}/check", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetString()!;

        var runs = await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/rules/{id}/runs?take=5");
        var one = await app.Client.GetAsync($"/api/retention/runs/{runId}");
        var unknownRun = await app.Client.GetAsync("/api/retention/runs/no-such-run");
        var leaving = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon");
        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/titles/{film.MovieId}");
        var quiet = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/titles/some-other-title");

        Assert.Equal(runId, Assert.Single(runs.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal("Dune", Assert.Single((await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
        Assert.Equal(film.MovieId, Assert.Single(leaving.EnumerateArray()).GetProperty("entityId").GetString());
        var mark = Assert.Single(status.GetProperty("marks").EnumerateArray());
        Assert.Equal(id, mark.GetProperty("ruleId").GetString());
        Assert.Equal("leaving", mark.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("keeps").GetArrayLength());
        Assert.Equal(0, quiet.GetProperty("marks").GetArrayLength());
    }
}
