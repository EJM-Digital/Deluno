using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Editing a release rule, previewing a plan change and asking for a recommendation (coverage register, area api): each is called the
/// way its screen calls it and read back, and a refusal changes nothing.
/// </summary>
public sealed class ReleaseRulesPlanPreviewsAndRecommendationsTests
{
    private static async Task<string> AddRuleAsync(ApplicationTestHost app, string name, string? tag)
    {
        var response = await app.Client.PostAsJsonAsync("/api/release-profiles", new
        {
            name, tagName = tag, preferredProtocol = "any", usenetDelayMinutes = 0, torrentDelayMinutes = 0, mustContain = (string?)null, mustNotContain = "CAM",
            preferredTerms = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> RuleAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/release-profiles")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Editing_a_release_rule_changes_what_is_stored_leaves_the_other_rule_alone_and_a_tag_another_rule_has_or_a_rule_that_is_gone_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var kids = await AddRuleAsync(app, "Kids", "kids");
        var adults = await AddRuleAsync(app, "Adults", "adults");

        var edited = await app.Client.PutAsJsonAsync($"/api/release-profiles/{kids}", new
        {
            name = "Kids, usenet first", tagName = "kids", preferredProtocol = "usenet", usenetDelayMinutes = 0, torrentDelayMinutes = 90,
            mustContain = (string?)null, mustNotContain = "CAM,TS", preferredTerms = Array.Empty<object>(), enabled = false
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var rule = await RuleAsync(app, kids);
        Assert.Equal("Kids, usenet first", rule.GetProperty("name").GetString());
        Assert.Equal("usenet", rule.GetProperty("preferredProtocol").GetString());
        Assert.Equal(90, rule.GetProperty("torrentDelayMinutes").GetInt32());
        Assert.False(rule.GetProperty("enabled").GetBoolean());
        Assert.Equal("Adults", (await RuleAsync(app, adults)).GetProperty("name").GetString());

        var taken = await app.Client.PutAsJsonAsync($"/api/release-profiles/{kids}", new
        {
            name = "Kids, usenet first", tagName = "adults", preferredProtocol = "usenet", usenetDelayMinutes = 0, torrentDelayMinutes = 90,
            mustContain = (string?)null, mustNotContain = "CAM,TS", preferredTerms = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("kids", (await RuleAsync(app, kids)).GetProperty("tagName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/release-profiles/no-such-rule", new
        {
            name = "x", tagName = "x", preferredProtocol = "any", usenetDelayMinutes = 0, torrentDelayMinutes = 0, preferredTerms = Array.Empty<object>()
        })).StatusCode);
    }

    [Fact]
    public async Task Previewing_a_plan_change_lists_what_would_change_without_saving_it_and_a_plan_that_is_not_there_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profile = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Films 1080p", mediaType = "movies", cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p,WEB 2160p", customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false
        })).Content.ReadFromJsonAsync<JsonElement>();
        var created = await app.Client.PostAsJsonAsync("/api/policy-sets", new
        {
            name = "Films", mediaType = "movies", qualityProfileId = profile.GetProperty("id").GetString(), destinationRuleId = (string?)null, customFormatIds = (string?)null,
            searchIntervalOverrideHours = 12, retryDelayOverrideHours = (int?)null, upgradeUntilCutoff = true, isEnabled = true, notes = (string?)null
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        object Plan(int hours, string name) => new
        {
            name, mediaType = "movies", qualityProfileId = profile.GetProperty("id").GetString(), destinationRuleId = (string?)null, customFormatIds = (string?)null,
            searchIntervalOverrideHours = hours, retryDelayOverrideHours = (int?)null, upgradeUntilCutoff = true, isEnabled = true, notes = (string?)null
        };

        var changed = await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Plan(6, "Films, faster"));

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var preview = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("hasChanges").GetBoolean());
        Assert.NotEmpty(preview.GetProperty("changes").EnumerateArray());
        Assert.Equal("Films, faster", preview.GetProperty("proposed").GetProperty("name").GetString());
        Assert.Equal("Films", preview.GetProperty("current").GetProperty("name").GetString());

        var same = await (await app.Client.PostAsJsonAsync($"/api/policy-sets/{id}/preview", Plan(12, "Films"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(same.GetProperty("hasChanges").GetBoolean());
        Assert.Empty(same.GetProperty("changes").EnumerateArray());

        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/policy-sets")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("Films", stored.GetProperty("name").GetString());
        Assert.Equal(12, stored.GetProperty("searchIntervalOverrideHours").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/policy-sets/no-such-plan/preview", Plan(6, "x"))).StatusCode);
    }

    [Fact]
    public async Task A_recommendation_scores_a_well_seeded_better_release_above_a_poor_one_and_says_which_in_a_word_and_a_sentence()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        object Release(string name, int seeders, int delta, int score) => new
        {
            releaseName = name, seeders, sizeBytes = 8_000_000_000L, qualityDelta = delta, customFormatScore = score, sourcePriorityScore = 0,
            indexerName = (string?)null, downloadClientId = (string?)null, estimatedBitrateMbps = (double?)null, releaseAgeHours = (double?)null
        };

        var good = await (await app.Client.PostAsJsonAsync("/api/intelligent-routing/recommend-release", Release("Dune.2021.2160p.WEB-DL.x265-GRP", 400, 2, 600))).Content.ReadFromJsonAsync<JsonElement>();
        var poor = await (await app.Client.PostAsJsonAsync("/api/intelligent-routing/recommend-release", Release("Dune.2021.480p.HDTV.x264-GRP", 1, -2, -600))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(good.GetProperty("recommendationScore").GetInt32() > poor.GetProperty("recommendationScore").GetInt32());
        Assert.Contains(good.GetProperty("recommendationLabel").GetString(), new[] { "strong", "review" });
        Assert.Equal("avoid", poor.GetProperty("recommendationLabel").GetString());
        Assert.Equal(
            $"Recommendation {good.GetProperty("recommendationScore").GetInt32()}/100 ({good.GetProperty("recommendationLabel").GetString()}) for Dune.2021.2160p.WEB-DL.x265-GRP.",
            good.GetProperty("summary").GetString());
        Assert.InRange(poor.GetProperty("recommendationScore").GetInt32(), 0, 100);
        Assert.Equal(JsonValueKind.Null, good.GetProperty("indexerSuccessRate").ValueKind);
    }
}
