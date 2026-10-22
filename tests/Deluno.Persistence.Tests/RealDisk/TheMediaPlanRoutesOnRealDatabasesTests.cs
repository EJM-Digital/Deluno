using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The Media Plan (policy set) routes on the real application and its real database files: a plan's saved versions, the
/// review of a change before it is saved (and what happens when the plan moved on in between), and what a preview says
/// about a profile that is not there or is for the other kind of media. Refs #1296.
/// </summary>
public sealed class TheMediaPlanRoutesOnRealDatabasesTests
{
    private static async Task<string> ProfileAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name, mediaType, cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static object Body(string name, string mediaType, string? profileId, int hours, string? expectedPlanHash = null) => new
    {
        name, mediaType, qualityProfileId = profileId, destinationRuleId = (string?)null, customFormatIds = (string?)null,
        searchIntervalOverrideHours = hours, retryDelayOverrideHours = (int?)null, upgradeUntilCutoff = true, isEnabled = true,
        notes = (string?)null, expectedPlanHash
    };

    private static async Task<string> PlanAsync(ApplicationTestHost app, string? profileId, string mediaType = "movies", string name = "Films")
    {
        var created = await app.Client.PostAsJsonAsync("/api/policy-sets", Body(name, mediaType, profileId, 12));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_plans_versions_are_listed_newest_first_and_one_is_read_by_number_or_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profile = await ProfileAsync(app, "movies", "Films 1080p");
        var id = await PlanAsync(app, profile);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films", "movies", profile, 6))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films", "movies", profile, 3))).StatusCode);

        var versions = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{id}/versions")).EnumerateArray().ToArray();

        Assert.True(versions.Length >= 3, "the plan as it was made and each save after it");
        var numbers = versions.Select(item => item.GetProperty("version").GetInt32()).ToArray();
        Assert.Equal(numbers.OrderByDescending(number => number).ToArray(), numbers);
        var oldest = numbers.Min();
        var one = await app.Client.GetAsync($"/api/policy-sets/{id}/versions/{oldest}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(oldest, (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/policy-sets/{id}/versions/9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/policy-sets/no-such-plan/versions/1")).StatusCode);
    }

    [Fact]
    public async Task A_change_reviewed_in_a_preview_is_refused_with_a_conflict_when_the_plan_moved_on_since_and_saved_when_it_did_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profile = await ProfileAsync(app, "movies", "Films 1080p");
        var id = await PlanAsync(app, profile);
        var preview = await (await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Body("Films", "movies", profile, 6))).Content.ReadFromJsonAsync<JsonElement>();
        var reviewed = preview.GetProperty("basePlanHash").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(reviewed));

        // Somebody else changes the plan after the review.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films, edited elsewhere", "movies", profile, 9))).StatusCode);

        var stale = await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films", "movies", profile, 6, reviewed));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/policy-sets")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("Films, edited elsewhere", stored.GetProperty("name").GetString());

        // Reviewed again, the change goes through; a save that names no review is never refused.
        var fresh = await (await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Body("Films", "movies", profile, 6))).Content.ReadFromJsonAsync<JsonElement>();
        var current = fresh.GetProperty("basePlanHash").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films", "movies", profile, 6, current))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync($"/api/policy-sets/{id}", Body("Films", "movies", profile, 5))).StatusCode);
    }

    [Fact]
    public async Task A_preview_that_names_a_profile_that_is_not_there_or_is_for_the_other_kind_is_refused_and_clearing_the_profile_is_allowed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await ProfileAsync(app, "movies", "Films 1080p");
        var id = await PlanAsync(app, films);

        var unknown = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Body("Films", "movies", "no-such-profile", 12));
        var wrongKind = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Body("Films", "tv", films, 12));
        var cleared = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Body("Films", "movies", null, 12));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var body = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("hasChanges").GetBoolean(), "dropping the profile is a change");
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/policy-sets")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal(films, stored.GetProperty("qualityProfileId").GetString());
    }
}
