using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The release-preference plan routes over the real host and the real SQLite files (coverage register, area engines): plans are stored once per id and
/// version and never change, every request that cannot be judged is refused in plain words, and a draft is judged without anything being written.
/// </summary>
public sealed class ReleasePreferencePlanRoutesAnswerPlainlyTests
{
    private const string Base = "/api/v1/release-preferences";

    /// <summary>The plan Deluno compiles for a profile, read the way a client reads it (enum words as the API writes them).</summary>
    private static async Task<ReleasePreferencePlan> CompiledPlanAsync(ApplicationTestHost app, string mediaType)
    {
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).Prop("tiers").EnumerateArray().Select(tier => tier.Prop("name").GetString()!).ToArray();
        var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", new { name = $"Plan source {mediaType}", mediaType, cutoffQuality = tiers[^1], allowedQualities = string.Join(",", tiers), customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).Prop("id").GetString()!;
        var response = await app.Client.GetAsync($"{Base}/plans/quality-profile/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<ReleasePreferencePlan>(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["plan"]!.ToJsonString(), ReleasePreferenceJson.Options)!;
    }

    private static StringContent Json(ReleasePreferencePlan plan) => new(JsonSerializer.Serialize(plan), System.Text.Encoding.UTF8, "application/json");

    /// <summary>A name over 500 characters is stopped at the door by the request limits, in the words they use, before the route's own check.</summary>
    private static async Task AssertTooLongAsync(HttpResponseMessage response, string member, string? message)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(member, body.Prop("member").GetString(), ignoreCase: true);
        if (message is not null)
        {
            Assert.Equal(message, body.Prop("message").GetString());
        }

        Assert.Contains("500", body.Prop("message").GetString());
    }

    private static async Task<string[]> ErrorsOfAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Prop("errors").Prop(field).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task Stored_plans_are_listed_for_one_media_type_or_all_and_a_plan_is_read_back_by_id_newest_version_or_the_one_asked_for_with_its_hash()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = (await CompiledPlanAsync(app, "movies")) with { Id = "custom-plan", Version = "v1" };
        var shows = await CompiledPlanAsync(app, "tv");
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsync($"{Base}/plans", Json(movies))).StatusCode);
        await Task.Delay(20); // reason: the newest version is told by the time it was stored, which has a resolution of its own
        var posted = await app.Client.PostAsync($"{Base}/plans", Json(movies with { Version = "v2" }));
        Assert.True(posted.StatusCode == HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());

        var all = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans");
        var onlyTv = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans?mediaType=tv");
        var newest = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans/custom-plan");
        var firstVersion = await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans/custom-plan?version=v1");

        Assert.Equal(4, all.GetArrayLength());
        Assert.All(onlyTv.EnumerateArray(), item => Assert.Equal(shows.MediaType, item.Prop("plan").Prop("mediaType").GetString()));
        Assert.Equal("v2", newest.Prop("plan").Prop("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(newest.Prop("planHash").GetString()));
        Assert.Equal("v1", firstVersion.Prop("plan").Prop("version").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"{Base}/plans/no-such-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"{Base}/plans/custom-plan?version=no-such-version")).StatusCode);
    }

    // DEFECT (found by this test, 15 Oct 2026): every plan compiled for a quality profile has an id with a slash ("quality-profile/<profile id>"), and
    // GET /api/v1/release-preferences/plans/{planId} cannot address an id with a slash: the path (even with the slash percent-encoded) lands on the
    // compile route /plans/quality-profile/{profileId} or a 404, so a profile's stored plan can be listed but not read back by its id or a chosen version.
    [Fact]
    public async Task A_profiles_compiled_plan_is_read_back_by_its_id()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var plan = await CompiledPlanAsync(app, "movies");

        var response = await app.Client.GetAsync($"{Base}/plans/{Uri.EscapeDataString(plan.Id)}?version={Uri.EscapeDataString(plan.Version)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(plan.Version, (await response.Content.ReadFromJsonAsync<JsonElement>()).Prop("plan").Prop("version").GetString());
    }

    [Fact]
    public async Task What_GET_returns_for_a_plan_is_accepted_by_POST_enum_words_and_all()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var plan = await CompiledPlanAsync(app, "movies");
        var read = await app.Client.GetAsync($"{Base}/plans/{Uri.EscapeDataString(plan.Id)}");
        var body = await read.Content.ReadAsStringAsync();
        var asRead = JsonNode.Parse(body)!["plan"]!.AsObject();
        Assert.Contains("\"", asRead.ToJsonString());
        asRead["version"] = "round-trip-1";

        var posted = await app.Client.PostAsync($"{Base}/plans", new StringContent(asRead.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        Assert.True(posted.StatusCode == HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());
        var notAPlan = await app.Client.PostAsync($"{Base}/plans", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notAPlan.StatusCode);
    }

    [Fact]
    public async Task A_new_plan_version_is_stored_with_201_saving_the_same_definition_again_is_fine_and_a_different_one_under_a_used_version_is_refused_as_immutable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var plan = (await CompiledPlanAsync(app, "movies")) with { Version = "custom-1" };

        var first = await app.Client.PostAsync($"{Base}/plans", Json(plan));
        var again = await app.Client.PostAsync($"{Base}/plans", Json(plan));
        var refused = await app.Client.PostAsync($"{Base}/plans", Json(plan with { Provenance = "someone changed the definition after it was used" }));

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).Prop("planHash").GetString(), (await again.Content.ReadFromJsonAsync<JsonElement>()).Prop("planHash").GetString());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("immutable", (await refused.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plan_that_cannot_work_is_refused_400_under_plan_naming_each_fault()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var plan = await CompiledPlanAsync(app, "movies");

        async Task<string[]> ProblemsAsync(Func<ReleasePreferencePlan, ReleasePreferencePlan> spoil)
            => await ErrorsOfAsync(await app.Client.PostAsync($"{Base}/plans", Json(spoil(plan with { Version = "spoilt" }))), "plan");

        Assert.Contains("Plan id is required.", await ProblemsAsync(copy => copy with { Id = " " }));
        Assert.Contains("Plan version is required.", await ProblemsAsync(copy => copy with { Version = "" }));
        Assert.Contains("Plan media type is required.", await ProblemsAsync(copy => copy with { MediaType = "" }));
        Assert.NotEmpty(await ProblemsAsync(copy => copy with { Families = [] }));
        var noLevels = await ProblemsAsync(copy => copy with { Families = [copy.Families[0] with { Levels = [] }, .. copy.Families.Skip(1)] });
        Assert.Contains(noLevels, problem => problem.EndsWith("must declare at least one level.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_is_refused_for_each_missing_or_too_long_part_and_a_plan_that_is_not_stored_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var plan = await CompiledPlanAsync(app, "movies");
        var planId = plan.Id;
        var tooLong = new string('x', 501);

        var noPlan = await app.Client.PostAsJsonAsync($"{Base}/preview", new { releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP" });
        var noRelease = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = " " });
        var longRelease = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = tooLong });
        var longCurrent = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP", currentReleaseName = tooLong });
        var negative = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP", seeders = -1 });
        var unknown = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId = "no-such-plan", releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP" });
        var judged = await app.Client.PostAsJsonAsync($"{Base}/preview", new { planId, releaseName = "Some.Film.2020.1080p.WEB-DL.x264-GRP", currentReleaseName = "Some.Film.2020.720p.WEB-DL.x264-GRP" });

        Assert.Contains("A persisted release-preference plan is required.", await ErrorsOfAsync(noPlan, "planId"));
        Assert.Contains("Release name is required.", await ErrorsOfAsync(noRelease, "releaseName"));
        await AssertTooLongAsync(longRelease, "releaseName", "The release name is too long. It can be at most 500 characters.");
        await AssertTooLongAsync(longCurrent, "currentReleaseName", null);
        Assert.Contains("Seeders cannot be negative.", await ErrorsOfAsync(negative, "seeders"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, judged.StatusCode);
        var preview = await judged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(planId, preview.Prop("planId").GetString());
        Assert.True(preview.TryGetProperty("candidateEvaluation", out _));
        Assert.True(preview.TryGetProperty("comparison", out var comparison) && comparison.ValueKind != JsonValueKind.Null, "a current release was given, so the two are compared");
    }

    [Fact]
    public async Task A_draft_is_judged_without_saving_anything_under_the_draft_plan_id_and_is_refused_for_a_blank_or_over_long_name_and_negative_seeders()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).Prop("tiers").EnumerateArray().Select(tier => tier.Prop("name").GetString()!).ToArray();
        var plansBefore = (await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans")).GetArrayLength();
        object Draft(string? release, int? seeders = null, string? current = null) => new
        {
            name = "Nobody has saved this", mediaType = "movies", allowedQualities = tiers, cutoffQuality = tiers[0], customFormatIds = Array.Empty<string>(),
            formatIntents = new Dictionary<string, string>(), upgradeUntilCutoff = true, upgradeUnknownItems = false, allowLowerQualityReplacements = false,
            releaseName = release, currentReleaseName = current, seeders
        };

        var blank = await app.Client.PostAsJsonAsync($"{Base}/judge-draft", Draft(" "));
        var tooLong = await app.Client.PostAsJsonAsync($"{Base}/judge-draft", Draft(new string('x', 501)));
        var longCurrent = await app.Client.PostAsJsonAsync($"{Base}/judge-draft", Draft("Some.Film.2020.1080p.WEB-DL.x264-GRP", current: new string('x', 501)));
        var negative = await app.Client.PostAsJsonAsync($"{Base}/judge-draft", Draft("Some.Film.2020.1080p.WEB-DL.x264-GRP", seeders: -3));
        var judged = await app.Client.PostAsJsonAsync($"{Base}/judge-draft", Draft("Some.Film.2020.1080p.WEB-DL.x264-GRP", seeders: 12));

        Assert.Contains("Type a release name to see how this profile would judge it.", await ErrorsOfAsync(blank, "releaseName"));
        await AssertTooLongAsync(tooLong, "releaseName", "The release name is too long. It can be at most 500 characters.");
        await AssertTooLongAsync(longCurrent, "currentReleaseName", null);
        Assert.Contains("Seeders cannot be negative.", await ErrorsOfAsync(negative, "seeders"));
        Assert.Equal(HttpStatusCode.OK, judged.StatusCode);
        var verdict = await judged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verdict.TryGetProperty("plan", out var draftPlan), verdict.ToString());
        Assert.EndsWith("draft", draftPlan.Prop("id").GetString());
        Assert.Equal("Some.Film.2020.1080p.WEB-DL.x264-GRP", verdict.Prop("releaseName").GetString());
        Assert.Equal(plansBefore, (await app.Client.GetFromJsonAsync<JsonElement>($"{Base}/plans")).GetArrayLength());
    }

    [Fact]
    public async Task A_draft_that_does_not_allow_the_releases_quality_refuses_it_outright_and_says_why_while_one_that_does_not_refuses_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).Prop("tiers").EnumerateArray().Select(tier => tier.Prop("name").GetString()!).ToArray();
        async Task<JsonElement> JudgeAsync(string[] allowed)
            => await (await app.Client.PostAsJsonAsync($"{Base}/judge-draft", new
            {
                name = "Draft", mediaType = "movies", allowedQualities = allowed, cutoffQuality = allowed[0], customFormatIds = Array.Empty<string>(), formatIntents = new Dictionary<string, string>(),
                upgradeUntilCutoff = true, upgradeUnknownItems = false, allowLowerQualityReplacements = false, releaseName = "Some.Film.2020.2160p.BluRay.x265-GRP"
            })).Content.ReadFromJsonAsync<JsonElement>();

        var onlyDvd = await JudgeAsync([tiers.First(tier => tier.Contains("DVD", StringComparison.OrdinalIgnoreCase))]);
        var everything = await JudgeAsync(tiers);

        Assert.False(string.IsNullOrWhiteSpace(onlyDvd.Prop("refusal").GetString()), onlyDvd.ToString());
        Assert.True(everything.Prop("refusal").ValueKind == JsonValueKind.Null, everything.ToString());
    }
}

internal static class PlanJson
{
    public static JsonElement Prop(this JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : throw new Xunit.Sdk.XunitException($"no '{name}' in {element}");
}
