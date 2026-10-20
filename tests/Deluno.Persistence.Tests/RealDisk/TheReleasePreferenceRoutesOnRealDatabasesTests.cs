using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The release-preference routes on the real application and its real database files: a profile's plan compiled and
/// stored once, a stored plan read back with its hash, and a preview that judges a candidate (and the installed release
/// when one is named) without ever producing one score. Refs #1296.
/// </summary>
public sealed class TheReleasePreferenceRoutesOnRealDatabasesTests
{
    private const string Base = "/api/v1/release-preferences";

    private static async Task<string> ProfileAsync(ApplicationTestHost app, string mediaType = "movies")
    {
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers").EnumerateArray()
            .Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = "Compiled", mediaType, cutoffQuality = "WEB 1080p", allowedQualities = string.Join(",", tiers),
            customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_profiles_plan_is_compiled_and_stored_with_its_hash_warnings_and_review_flag_and_an_unknown_profile_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await ProfileAsync(app);

        var response = await app.Client.GetAsync($"{Base}/plans/quality-profile/{profileId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var compiled = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(compiled.GetProperty("registryVersion").GetString()));
        Assert.Equal(profileId, compiled.GetProperty("profileId").GetString());
        Assert.Equal("Compiled", compiled.GetProperty("profileName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(compiled.GetProperty("planHash").GetString()));
        Assert.Equal(JsonValueKind.Array, compiled.GetProperty("advancedRules").ValueKind);
        Assert.Equal(JsonValueKind.Array, compiled.GetProperty("warnings").ValueKind);
        Assert.NotEqual(default, compiled.GetProperty("storedUtc").GetDateTimeOffset());
        Assert.True(compiled.GetProperty("requiresReview").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var planId = compiled.GetProperty("plan").GetProperty("id").GetString()!;
        var version = compiled.GetProperty("plan").GetProperty("version").GetString()!;

        // Stored, and stored once: the same profile compiles to the same plan, read back by its id with the same hash.
        var again = await (await app.Client.GetAsync($"{Base}/plans/quality-profile/{profileId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(compiled.GetProperty("planHash").GetString(), again.GetProperty("planHash").GetString());
        var stored = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans/{Uri.EscapeDataString(planId)}?version={Uri.EscapeDataString(version)}");
        Assert.Equal(compiled.GetProperty("planHash").GetString(), stored.GetProperty("planHash").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"{Base}/plans/quality-profile/no-such-profile")).StatusCode);
    }

    [Fact]
    public async Task A_preview_judges_the_candidate_and_the_installed_release_by_the_stored_plan_and_never_gives_one_score()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await ProfileAsync(app);
        var compiled = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans/quality-profile/{profileId}");
        var planId = compiled.GetProperty("plan").GetProperty("id").GetString()!;

        var alone = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP", seeders = 25 });
        var against = await app.Client.PostAsJsonAsync($"{Base}/preview", new
        {
            planId,
            releaseName = "Some.Film.2020.2160p.BluRay.x265-GRP",
            currentReleaseName = "Some.Film.2020.720p.WEB-DL.x264-GRP"
        });
        var byQuality = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = "Some.Film.2020.x264-GRP", candidateQuality = "WEB 1080p" });

        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
        var candidateOnly = await alone.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(planId, candidateOnly.GetProperty("planId").GetString());
        Assert.Equal(compiled.GetProperty("plan").GetProperty("version").GetString(), candidateOnly.GetProperty("planVersion").GetString());
        Assert.Equal(compiled.GetProperty("planHash").GetString(), candidateOnly.GetProperty("planHash").GetString());
        Assert.True(candidateOnly.GetProperty("candidateFacts").GetArrayLength() > 0, "the name gave facts");
        Assert.Equal(JsonValueKind.Object, candidateOnly.GetProperty("candidateEvaluation").ValueKind);
        Assert.Equal(JsonValueKind.Null, candidateOnly.GetProperty("currentEvaluation").ValueKind);
        Assert.Equal(JsonValueKind.Null, candidateOnly.GetProperty("comparison").ValueKind);
        Assert.DoesNotContain("score", candidateOnly.GetRawText(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, against.StatusCode);
        var compared = await against.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Some.Film.2020.720p.WEB-DL.x264-GRP", compared.GetProperty("currentReleaseName").GetString());
        Assert.True(compared.GetProperty("currentFacts").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Object, compared.GetProperty("currentEvaluation").ValueKind);
        Assert.Equal(JsonValueKind.Object, compared.GetProperty("comparison").ValueKind);
        Assert.DoesNotContain("score", compared.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // A quality named by the caller counts as a fact even when the name does not say it.
        var named = await byQuality.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("1080", named.GetProperty("candidateFacts").GetRawText());
    }
}
