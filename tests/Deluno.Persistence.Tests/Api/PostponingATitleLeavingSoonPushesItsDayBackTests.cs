using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Retention;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Postpone" on a title that is leaving soon (#1010): its removal day moves back by the days chosen, the title is not kept, Activity says
/// so, and a postponement that asks for nonsense or names a rule that is not there changes nothing.
/// </summary>
public sealed class PostponingATitleLeavingSoonPushesItsDayBackTests
{
    private static readonly RetentionStep[] EveryFilm = [RetentionStep.KeepOnly("year:max:2100")];

    private static async Task<(string RuleId, string MovieId, DateTimeOffset Day)> LeavingSoonAsync(ApplicationTestHost app)
    {
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var created = await app.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = EveryFilm, mediaServerIds = Array.Empty<string>(), removeFromServers = false,
            waitDays = 7, removalMode = "ask", checkIntervalHours = 8
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ruleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsync($"/api/retention/rules/{ruleId}/check", content: null)).StatusCode);
        var mark = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        return (ruleId, film.MovieId, mark.GetProperty("removeAfterUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Postponing_a_title_moves_its_removal_day_back_by_the_days_chosen_says_so_in_Activity_and_does_not_keep_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (ruleId, movieId, day) = await LeavingSoonAsync(app);

        var response = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/postpone", new { ruleId, entityIds = new[] { movieId }, days = 10 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(movieId, moved.GetProperty("entityId").GetString());
        Assert.Equal(day.AddDays(10), moved.GetProperty("removeAfterUtc").GetDateTimeOffset(), TimeSpan.FromMinutes(1));
        var listed = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        Assert.Equal(day.AddDays(10), listed.GetProperty("removeAfterUtc").GetDateTimeOffset(), TimeSpan.FromMinutes(1));
        Assert.Equal("leaving", listed.GetProperty("state").GetString());
        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/retention/titles/{movieId}");
        Assert.Equal(0, status.GetProperty("keeps").GetArrayLength());
        Assert.Contains("Dune was postponed by 10 days on retention rule", await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    [Fact]
    public async Task A_postponement_of_zero_days_or_for_a_rule_that_is_not_there_or_a_title_not_leaving_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (ruleId, movieId, day) = await LeavingSoonAsync(app);

        var zero = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/postpone", new { ruleId, entityIds = new[] { movieId }, days = 0 });
        var noRule = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/postpone", new { ruleId = "no-such-rule", entityIds = new[] { movieId }, days = 5 });
        var elsewhere = await app.Client.PostAsJsonAsync("/api/retention/leaving-soon/postpone", new { ruleId, entityIds = new[] { "some-other-title" }, days = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("Postpone by a whole number of days from 1 to 3650.", await zero.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, noRule.StatusCode);
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        Assert.Empty((await elsewhere.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        var listed = Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/leaving-soon")).EnumerateArray());
        Assert.Equal(day, listed.GetProperty("removeAfterUtc").GetDateTimeOffset());
    }
}
