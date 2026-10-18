using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// GET /api/decisions is what a title page reads for "why did Deluno choose this": the decision rows of one title (relatedEntityType and
/// relatedEntityId), in pages, and nothing else that happens to sit in Activity. Real host, real activity database.
/// </summary>
public sealed class TheDecisionsRouteListsOnlyDecisionRowsForOneTitleTests
{
    private static DecisionExplanationPayload Decision(string reason)
        => new("release-selection", "selected", reason, new Dictionary<string, string?> { ["release"] = reason }, "sent", []);

    private static async Task<ApplicationTestHost> SeedAsync(params (string? EntityType, string? EntityId, string Reason, bool Decision)[] rows)
    {
        var app = await ApplicationTestHost.StartAsync();
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        foreach (var row in rows)
        {
            if (row.Decision)
            {
                await activity.RecordDecisionAsync(Decision(row.Reason), null, row.EntityType, row.EntityId, CancellationToken.None);
            }
            else
            {
                await activity.RecordActivityAsync("grab.sent", row.Reason, "{\"plain\":true}", null, row.EntityType, row.EntityId, CancellationToken.None);
            }
        }

        return app;
    }

    private static async Task<JsonElement> GetAsync(ApplicationTestHost app, string query)
    {
        var response = await app.Client.GetAsync("/api/decisions" + query);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Reasons(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("reason").GetString()!).ToArray();

    [Fact]
    public async Task Asking_for_one_title_lists_its_decisions_only_and_leaves_out_other_titles_and_events_that_are_not_decisions()
    {
        await using var app = await SeedAsync(
            ("movie", "film-1", "Chose the 1080p release for film one", true),
            ("movie", "film-2", "Chose the 720p release for film two", true),
            ("series", "film-1", "Chose a release for a show that shares the id", true),
            ("movie", "film-1", "Sent a release to the client", false));

        var page = await GetAsync(app, "?relatedEntityType=movie&relatedEntityId=film-1");

        Assert.Equal(["Chose the 1080p release for film one"], Reasons(page));
        var item = page.GetProperty("items")[0];
        Assert.Equal("movie", item.GetProperty("relatedEntityType").GetString());
        Assert.Equal("film-1", item.GetProperty("relatedEntityId").GetString());
        Assert.Equal("release-selection", item.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task With_no_filter_every_decision_is_listed_and_the_events_that_are_not_decisions_are_not()
    {
        await using var app = await SeedAsync(
            ("movie", "film-1", "Decision one", true),
            ("movie", "film-2", "Decision two", true),
            ("movie", "film-1", "Not a decision", false));

        var page = await GetAsync(app, string.Empty);

        Assert.Equal(["Decision one", "Decision two"], Reasons(page).Order().ToArray());
    }

    [Fact]
    public async Task A_page_of_one_hands_back_a_token_and_the_next_page_continues_from_it_without_repeating_a_decision()
    {
        await using var app = await SeedAsync(
            ("movie", "film-1", "First", true),
            ("movie", "film-1", "Second", true),
            ("movie", "film-1", "Third", true));

        var first = await GetAsync(app, "?relatedEntityType=movie&relatedEntityId=film-1&pageSize=1");
        var token = first.GetProperty("nextPageToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        var second = await GetAsync(app, $"?relatedEntityType=movie&relatedEntityId=film-1&pageSize=1&pageToken={Uri.EscapeDataString(token!)}");

        var seen = Reasons(first).Concat(Reasons(second)).ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Equal(seen.Length, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_title_with_no_decisions_gets_an_empty_page_not_an_error()
    {
        await using var app = await SeedAsync(("movie", "film-1", "Decision one", true));

        var page = await GetAsync(app, "?relatedEntityType=movie&relatedEntityId=nobody");

        Assert.Empty(Reasons(page));
    }
}
