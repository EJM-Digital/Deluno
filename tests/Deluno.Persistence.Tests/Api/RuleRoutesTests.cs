using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The rule lists of Settings over a real host and the real platform database: seeding (sharing) rules, auto-tag rules and the
/// quality model's reset. A rule is saved only when it can match something, an edit changes only what was sent, removal is
/// final and a rule that is not there is a plain 404.
/// </summary>
public sealed class RuleRoutesTests
{
    [Fact]
    public async Task A_sharing_rule_is_added_edited_and_removed_and_one_that_matches_nothing_or_has_a_negative_guard_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var nothing = await app.Client.PostAsJsonAsync("/api/sharing-rules", new { priority = 1, mode = "forever" });
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        Assert.Contains("matches nothing", (await nothing.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/sharing-rules", new { categoryPattern = "movies", minSeeders = -1 })).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/sharing-rules")).Replace(" ", ""));

        var added = await app.Client.PostAsJsonAsync("/api/sharing-rules", new { priority = 5, categoryPattern = "movies", forHours = 72, minSeeders = 2 });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var edited = await app.Client.PutAsJsonAsync($"/api/sharing-rules/{id}", new { trackerPattern = "example-tracker", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("example-tracker", body.GetProperty("trackerPattern").GetString());
        Assert.Equal("movies", body.GetProperty("categoryPattern").GetString());
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PutAsJsonAsync($"/api/sharing-rules/{id}", new { maxInactiveDays = -3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/sharing-rules/no-such-rule", new { priority = 2 })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/sharing-rules/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/sharing-rules")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/sharing-rules/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_auto_tag_rule_is_added_edited_and_removed_and_a_rule_with_no_name_tag_or_usable_condition_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        object Rule(string name, string[] tags, object[] specifications) => new { name, tags, removeAutomatically = true, appliesTo = "movies", isEnabled = true, specifications };
        var genre = new object[] { new { type = "genre", negate = false, value = "Horror" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("", ["scary"], genre))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("Scary", [], genre))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("Scary", ["scary"], []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("Scary", ["scary"], [new { type = "moon-phase", negate = false, value = "full" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("Scary", ["scary"], [new { type = "year", negate = false, value = "sometime" }]))).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/auto-tag-rules")).Replace(" ", ""));

        var added = await app.Client.PostAsJsonAsync("/api/auto-tag-rules", Rule("Scary", ["scary"], genre));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var edited = await app.Client.PutAsJsonAsync($"/api/auto-tag-rules/{id}", Rule("Very scary", ["scary", "halloween"], [new { type = "year", negate = true, value = "-1999" }]));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Very scary", body.GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("tags").GetArrayLength());
        Assert.Equal("year", Assert.Single(body.GetProperty("specifications").EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/auto-tag-rules/no-such-rule", Rule("x", ["x"], genre))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PutAsJsonAsync($"/api/auto-tag-rules/{id}", Rule("", ["x"], genre))).StatusCode);
        Assert.Equal("Very scary", Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>("/api/auto-tag-rules")).EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/auto-tag-rules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/auto-tag-rules/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/auto-tag-rules")).Replace(" ", ""));
    }

    [Fact]
    public async Task Resetting_the_quality_model_returns_the_shipped_sizes_and_says_so_in_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var reset = await app.Client.PostAsync("/api/quality-model/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var returned = await reset.Content.ReadAsStringAsync();
        var current = await app.Client.GetStringAsync("/api/quality-model");
        Assert.Equal(JsonDocument.Parse(current).RootElement.GetRawText(), JsonDocument.Parse(returned).RootElement.GetRawText());
        Assert.Contains("quality.model.reset", await app.Client.GetStringAsync("/api/activity?take=20"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsync("/api/quality-model/reset", content: null)).StatusCode);
    }
}
