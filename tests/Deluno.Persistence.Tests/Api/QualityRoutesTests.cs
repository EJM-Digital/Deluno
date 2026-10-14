using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The quality model, quality profiles and media plans (policy sets) through the real host: what is refused, what is kept as it was
/// given, who may change them, and what a plan change does to the libraries that use it.
/// </summary>
public sealed class QualityRoutesTests
{
    // ---- the quality model refuses a table that cannot work ----

    public static TheoryData<string> InvalidTables => new()
    {
        "duplicate name",
        "empty name",
        "rank zero",
        "rank negative",
        "negative film minimum",
        "inverted film band",
        "negative episode minimum",
        "inverted episode band",
        "film preferred above max",
        "film preferred below min",
        "episode preferred above max"
    };

    [Theory]
    [MemberData(nameof(InvalidTables))]
    public async Task The_quality_model_refuses_a_table_that_cannot_work_and_keeps_the_one_it_has(string fault)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var before = TableOf(await app.Client.GetStringAsync("/api/quality-model"));
        var table = JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!.AsObject();
        var tiers = table["tiers"]!.AsArray();
        Break(tiers, fault);

        using var response = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers, upgradeStop = table["upgradeStop"] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("qualityModel", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, TableOf(await app.Client.GetStringAsync("/api/quality-model")));
    }

    [Fact]
    public async Task A_maximum_of_zero_means_no_ceiling_and_a_good_table_is_saved()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var table = JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!.AsObject();
        var first = table["tiers"]![0]!.AsObject();
        first["movieMaxGb"] = 0;
        first["moviePreferredGb"] = first["movieMinGb"]!.GetValue<double>();

        using var response = await app.Client.PutAsJsonAsync("/api/quality-model", new { tiers = table["tiers"], upgradeStop = table["upgradeStop"] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = JsonNode.Parse(await app.Client.GetStringAsync("/api/quality-model"))!;
        Assert.Equal(0, saved["tiers"]![0]!["movieMaxGb"]!.GetValue<double>());
    }

    /// <summary>The tiers and the upgrade-stop choice, without the time the table was read.</summary>
    private static string TableOf(string model)
    {
        var node = JsonNode.Parse(model)!.AsObject();
        return node["tiers"]!.ToJsonString() + node["upgradeStop"]!.ToJsonString();
    }

    private static void Break(JsonArray tiers, string fault)
    {
        var first = tiers[0]!.AsObject();
        switch (fault)
        {
            case "duplicate name": tiers[1]!.AsObject()["name"] = first["name"]!.GetValue<string>(); break;
            case "empty name": first["name"] = " "; break;
            case "rank zero": first["rank"] = 0; break;
            case "rank negative": first["rank"] = -3; break;
            case "negative film minimum": first["movieMinGb"] = -1; break;
            case "inverted film band": first["movieMinGb"] = 20; first["movieMaxGb"] = 5; first["moviePreferredGb"] = 10; break;
            case "negative episode minimum": first["episodeMinMb"] = -1; break;
            case "inverted episode band": first["episodeMinMb"] = 900; first["episodeMaxMb"] = 100; first["episodePreferredMb"] = 500; break;
            case "film preferred above max": first["movieMinGb"] = 1; first["movieMaxGb"] = 5; first["moviePreferredGb"] = 9; break;
            case "film preferred below min": first["movieMinGb"] = 4; first["movieMaxGb"] = 8; first["moviePreferredGb"] = 2; break;
            case "episode preferred above max": first["episodeMinMb"] = 100; first["episodeMaxMb"] = 500; first["episodePreferredMb"] = 900; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
    }

    // ---- a saved profile keeps what it was given ----

    [Fact]
    public async Task A_saved_profile_gives_back_every_plain_answer_it_was_given_on_the_next_list_and_after_an_edit()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TierNamesAsync(app);
        var cutoff = tiers[^1];
        var allowed = string.Join(",", tiers);

        var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", Profile("Careful", allowed, cutoff, minScore: 100, protocol: "usenet", usenetDelay: 30, require: true));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var listed = await ProfileAsync(app, id);
        Assert.Equal("Careful", listed.GetProperty("name").GetString());
        Assert.Equal(cutoff, listed.GetProperty("cutoffQuality").GetString());
        Assert.True(listed.GetProperty("upgradeUntilCutoff").GetBoolean());
        Assert.Equal(100, listed.GetProperty("scoreLimits").GetProperty("minFormatScore").GetInt32());
        Assert.Equal(25, listed.GetProperty("scoreLimits").GetProperty("minUpgradeFormatScore").GetInt32());
        Assert.Equal("usenet", listed.GetProperty("acquisition").GetProperty("preferredProtocol").GetString());
        Assert.Equal(30, listed.GetProperty("acquisition").GetProperty("usenetDelayMinutes").GetInt32());
        Assert.Equal("proper", listed.GetProperty("acquisition").GetProperty("mustContain").GetString());
        Assert.Equal("cam", listed.GetProperty("acquisition").GetProperty("mustNotContain").GetString());
        Assert.True(listed.GetProperty("languagePreference").GetProperty("requireLanguage").GetBoolean());
        Assert.Equal("fr", listed.GetProperty("languagePreference").GetProperty("preferredLanguages")[0].GetString());
        Assert.True(listed.GetProperty("upgradeStop").GetProperty("stopWhenCutoffMet").GetBoolean());
        Assert.Equal("prefer", listed.GetProperty("formatIntents").GetProperty("hdr10").GetString());
        Assert.Equal(tiers[0], listed.GetProperty("sizeRules")[0].GetProperty("quality").GetString());
        Assert.Equal(2, listed.GetProperty("sizeRules")[0].GetProperty("minGb").GetDouble());

        var edited = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Profile("Careful, edited", allowed, cutoff, minScore: 150, protocol: "torrent", usenetDelay: 0, require: false));
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());

        var again = await ProfileAsync(app, id);
        Assert.Equal("Careful, edited", again.GetProperty("name").GetString());
        Assert.Equal(150, again.GetProperty("scoreLimits").GetProperty("minFormatScore").GetInt32());
        Assert.Equal("torrent", again.GetProperty("acquisition").GetProperty("preferredProtocol").GetString());
        Assert.False(again.GetProperty("languagePreference").GetProperty("requireLanguage").GetBoolean());
    }

    private static object Profile(string name, string allowed, string cutoff, int minScore, string protocol, int usenetDelay, bool require) => new
    {
        name,
        mediaType = "movies",
        cutoffQuality = cutoff,
        allowedQualities = allowed,
        customFormatIds = string.Empty,
        upgradeUntilCutoff = true,
        upgradeUnknownItems = false,
        sizeRules = new[] { new { quality = allowed.Split(',')[0], minGb = 2.0, maxGb = 9.0, minMb = 100.0, maxMb = 900.0 } },
        upgradeStop = new { stopWhenCutoffMet = true, requireCustomFormatGainForSameQuality = true },
        formatIntents = new Dictionary<string, string> { ["hdr10"] = "prefer" },
        acquisition = new { preferredProtocol = protocol, usenetDelayMinutes = usenetDelay, torrentDelayMinutes = 0, mustContain = "proper", mustNotContain = "cam" },
        scoreLimits = new { minFormatScore = minScore, cutoffFormatScore = 500, minUpgradeFormatScore = 25 },
        languagePreference = new { preferredLanguages = new[] { "fr" }, requireLanguage = require }
    };

    private static async Task<JsonElement> ProfileAsync(ApplicationTestHost app, string id)
    {
        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles");
        return list.EnumerateArray().First(item => item.GetProperty("id").GetString() == id);
    }

    private static async Task<string[]> TierNamesAsync(ApplicationTestHost app)
    {
        var model = await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model");
        return model.GetProperty("tiers").EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
    }

    // ---- who may change profiles, custom formats, plans and the quality model ----

    public static TheoryData<string, string, string> WriteRoutes => new()
    {
        { "POST", "/api/quality-profiles", """{"name":"x","mediaType":"movies","cutoffQuality":"x","allowedQualities":"x","upgradeUntilCutoff":true,"upgradeUnknownItems":false}""" },
        { "PUT", "/api/quality-profiles/no-such-profile", """{"name":"x","cutoffQuality":"x","allowedQualities":"x","upgradeUntilCutoff":true,"upgradeUnknownItems":false}""" },
        { "DELETE", "/api/quality-profiles/no-such-profile", "" },
        { "PUT", "/api/quality-model", """{"tiers":[],"upgradeStop":null}""" },
        { "POST", "/api/custom-formats", """{"name":"x","mediaType":"movies"}""" },
        { "PUT", "/api/custom-formats/no-such-format", """{"name":"x","mediaType":"movies"}""" },
        { "DELETE", "/api/custom-formats/no-such-format", "" },
        { "POST", "/api/policy-sets", """{"name":"x","mediaType":"movies","upgradeUntilCutoff":true,"isEnabled":true}""" },
        { "PUT", "/api/policy-sets/no-such-plan", """{"name":"x","mediaType":"movies","upgradeUntilCutoff":true,"isEnabled":true}""" },
        { "DELETE", "/api/policy-sets/no-such-plan", "" }
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_route_for_profiles_formats_plans_and_the_model_refuses_no_key_a_wrong_key_and_a_read_only_key(string method, string path, string body)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var wrong = app.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "deluno_not-a-key-deluno-issued");
        using var readOnly = await KeyClientAsync(app, "read");
        using var writer = await KeyClientAsync(app, "write");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(wrong, method, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, method, path, body)).StatusCode);
        var allowed = (await SendAsync(writer, method, path, body)).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    // ---- the release-preference routes ----

    [Fact]
    public async Task Release_preference_plans_are_read_with_a_read_key_and_saved_only_with_a_write_key()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");
        using var writer = await KeyClientAsync(app, "write");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", "/api/v1/release-preferences/plans", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(readOnly, "GET", "/api/v1/release-preferences/plans", null)).StatusCode);

        const string plan = "{}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "POST", "/api/v1/release-preferences/plans", plan)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, "POST", "/api/v1/release-preferences/plans", plan)).StatusCode);
        var allowed = (await SendAsync(writer, "POST", "/api/v1/release-preferences/plans", plan)).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    [Theory]
    [InlineData("/api/v1/release-preferences/preview")]
    [InlineData("/api/v1/release-preferences/judge-draft")]
    public async Task Previewing_and_judging_a_draft_only_evaluate_so_a_read_key_may_but_no_sign_in_may_not(string path)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "POST", path, "{}")).StatusCode);
        var allowed = (await SendAsync(readOnly, "POST", path, "{}")).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    // ---- the guide routes ----

    public static TheoryData<string, string> GuideWrites => new()
    {
        { "POST", "/api/v1/guides/trash/preview" },
        { "POST", "/api/v1/guides/trash/sync/preview" },
        { "PUT", "/api/v1/guides/trash/update-check/settings" },
        { "POST", "/api/v1/guides/trash/update-check/run" },
        { "POST", "/api/v1/guides/trash/apply" },
        { "POST", "/api/v1/guides/trash/versions/1/activate" },
        { "POST", "/api/v1/guides/trash/sync/apply" }
    };

    [Theory]
    [MemberData(nameof(GuideWrites))]
    public async Task A_guide_write_route_refuses_no_sign_in_and_a_read_only_key(string method, string path)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");
        using var writer = await KeyClientAsync(app, "write");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, method, path, "{}")).StatusCode);
        var allowed = (await SendAsync(writer, method, path, "{}")).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    [Theory]
    [InlineData("/api/v1/guides/trash/package")]
    [InlineData("/api/v1/guides/trash/inventory")]
    [InlineData("/api/v1/guides/trash/update-check")]
    [InlineData("/api/v1/guides/trash/versions")]
    public async Task A_guide_read_route_refuses_no_sign_in_and_answers_a_read_only_key(string path)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();
        using var readOnly = await KeyClientAsync(app, "read");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "GET", path, null)).StatusCode);
        var allowed = (await SendAsync(readOnly, "GET", path, null)).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    // ---- a media plan change reaches the libraries that use it ----

    [Fact]
    public async Task Changing_a_plan_adds_a_version_pushes_its_profile_and_cadence_to_the_libraries_using_it_and_an_unknown_plan_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TierNamesAsync(app);
        var allowed = string.Join(",", tiers);
        var firstProfile = await CreateProfileAsync(app, "First profile", allowed, tiers[0]);
        var secondProfile = await CreateProfileAsync(app, "Second profile", allowed, tiers[^1]);

        var created = await app.Client.PostAsJsonAsync("/api/policy-sets", new
        {
            name = "Films plan",
            mediaType = "movies",
            qualityProfileId = firstProfile,
            customFormatIds = string.Empty,
            searchIntervalOverrideHours = 6,
            retryDelayOverrideHours = 12,
            upgradeUntilCutoff = true,
            isEnabled = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var planId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var root = Path.Combine(app.DataRoot, "libraries", "films");
        Directory.CreateDirectory(root);
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
        var assigned = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = planId });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        var versionsBefore = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{planId}/versions")).GetArrayLength();

        var updated = await app.Client.PutAsJsonAsync($"/api/policy-sets/{planId}", new
        {
            name = "Films plan",
            mediaType = "movies",
            qualityProfileId = secondProfile,
            customFormatIds = string.Empty,
            searchIntervalOverrideHours = 3,
            retryDelayOverrideHours = 24,
            upgradeUntilCutoff = true,
            isEnabled = true
        });

        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
        var versionsAfter = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/policy-sets/{planId}/versions")).GetArrayLength();
        Assert.Equal(versionsBefore + 1, versionsAfter);
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries");
        var pushed = libraries.EnumerateArray().First(item => item.GetProperty("id").GetString() == libraryId);
        Assert.Equal(secondProfile, pushed.GetProperty("qualityProfileId").GetString());
        Assert.Equal(180, pushed.GetProperty("searchIntervalMinutes").GetInt32());
        Assert.Equal(24, pushed.GetProperty("retryDelayHours").GetInt32());

        var unknown = await app.Client.PutAsJsonAsync("/api/policy-sets/no-such-plan", new { name = "x", mediaType = "movies", upgradeUntilCutoff = true, isEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<string> CreateProfileAsync(ApplicationTestHost app, string name, string allowed, string cutoff)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name,
            mediaType = "movies",
            cutoffQuality = cutoff,
            allowedQualities = allowed,
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Scope " + scopes, scopes });
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
