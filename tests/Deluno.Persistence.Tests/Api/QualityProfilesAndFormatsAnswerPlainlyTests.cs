using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Quality profiles and custom formats over the real routes and the real SQLite files (coverage register, area engines): every refusal says in plain words what to
/// change, a profile starts with its own size answers, an edit keeps or clears exactly what it should, and a rule is saved or matched the way the routes say.
/// </summary>
public sealed class QualityProfilesAndFormatsAnswerPlainlyTests
{
    private static async Task<string[]> TiersAsync(ApplicationTestHost app)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers").EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();

    private static object Body(string? name, string? mediaType, string? cutoff, string? allowed, string? customFormatIds = "", object? acquisition = null, object? sizeRules = null)
        => new { name, mediaType, cutoffQuality = cutoff, allowedQualities = allowed, customFormatIds, upgradeUntilCutoff = true, upgradeUnknownItems = false, acquisition, sizeRules };

    private static async Task<JsonElement> ProfileAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles")).EnumerateArray().First(item => item.GetProperty("id").GetString() == id);

    private static async Task<string> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetRawText();
    }

    [Fact]
    public async Task Saving_a_profile_is_refused_in_plain_words_for_a_blank_name_an_unknown_media_type_an_unknown_tier_and_a_missing_or_unknown_cutoff()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TiersAsync(app);
        var good = string.Join(",", tiers.Take(2));

        Assert.Contains("Give this quality profile a name.", await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("  ", "movies", tiers[0], good))));
        Assert.Contains("Choose whether this profile is for Movies or TV Shows.", await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Mine", "music", tiers[0], good))));
        var unknownTier = await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Mine", "movies", tiers[0], good + ",Betamax,VHS")));
        Assert.Contains("Unknown quality tier(s): Betamax, VHS. Choose tiers from the quality model.", unknownTier);
        Assert.Contains("Choose the quality Deluno should aim for.", await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Mine", "movies", " ", good))));
        Assert.Contains("Unknown cutoff quality 'Betamax'. Choose a tier from the quality model.", await ErrorsAsync(await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Mine", "movies", "Betamax", good))));
        foreach (var accepted in new[] { "movies", "tv", "tv shows", "TVShows" })
        {
            var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", Body($"Accepted {accepted}", accepted, tiers[0], good));
            Assert.True(response.IsSuccessStatusCode, $"{accepted}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Changing_a_profile_is_held_to_the_same_rules_and_a_blank_allowed_list_keeps_the_stored_one_while_a_blank_format_list_clears_the_chosen_formats()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TiersAsync(app);
        var allowed = string.Join(",", tiers.Take(3));
        var format = await (await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = "Scene group", mediaType = "movies", score = 10, conditions = "[{\"type\":\"releaseGroup\",\"value\":\"SCENE\",\"required\":true}]", upgradeAllowed = true })).Content.ReadFromJsonAsync<JsonElement>();
        var created = await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Mine", "movies", tiers[0], allowed, customFormatIds: format.GetProperty("id").GetString()));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(format.GetProperty("id").GetString(), (await ProfileAsync(app, id)).GetProperty("customFormatIds").GetString());

        Assert.Contains("Give this quality profile a name.", await ErrorsAsync(await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Body("", null, tiers[0], allowed))));
        Assert.Contains("Unknown quality tier(s): Betamax.", await ErrorsAsync(await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Body("Mine", null, tiers[0], "Betamax"))));
        Assert.Contains("Choose the quality Deluno should aim for.", await ErrorsAsync(await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Body("Mine", null, null, allowed))));
        Assert.Contains("Unknown cutoff quality 'Betamax'.", await ErrorsAsync(await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Body("Mine", null, "Betamax", allowed))));
        var unchanged = await ProfileAsync(app, id);
        Assert.Equal(allowed.Split(','), unchanged.GetProperty("allowedQualities").GetString()!.Split(',', StringSplitOptions.TrimEntries));

        var saved = await app.Client.PutAsJsonAsync($"/api/quality-profiles/{id}", Body("Mine, renamed", null, tiers[0], "", customFormatIds: ""));

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var after = await ProfileAsync(app, id);
        Assert.Equal("Mine, renamed", after.GetProperty("name").GetString());
        Assert.Equal(allowed.Split(','), after.GetProperty("allowedQualities").GetString()!.Split(',', StringSplitOptions.TrimEntries));
        Assert.True(string.IsNullOrEmpty(after.GetProperty("customFormatIds").GetString()), "a blank format list clears the chosen formats");
    }

    [Fact]
    public async Task A_new_profile_without_size_answers_starts_with_the_typical_band_of_every_tier_it_allows_and_explicit_answers_are_kept_one_per_tier_the_right_way_round()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TiersAsync(app);
        var allowed = tiers.Take(3).ToArray();

        var starting = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Starts typical", "movies", allowed[0], string.Join(",", allowed)))).Content.ReadFromJsonAsync<JsonElement>();
        var explicitRules = await (await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Own sizes", "movies", allowed[0], string.Join(",", allowed), sizeRules: new[]
        {
            new { quality = $"  {allowed[0]}  ", minGb = 9.0, maxGb = 2.0, minMb = 0.0, maxMb = 0.0 },
            new { quality = allowed[0].ToLowerInvariant(), minGb = 3.0, maxGb = 8.0, minMb = 500.0, maxMb = 100.0 },
            new { quality = " ", minGb = 1.0, maxGb = 2.0, minMb = 1.0, maxMb = 2.0 }
        }))).Content.ReadFromJsonAsync<JsonElement>();

        var startingRules = starting.GetProperty("sizeRules").EnumerateArray().ToArray();
        Assert.Equal(allowed.Order(StringComparer.OrdinalIgnoreCase).ToArray(), startingRules.Select(rule => rule.GetProperty("quality").GetString()!).ToArray());
        Assert.All(startingRules, rule => Assert.True(rule.GetProperty("maxGb").GetDouble() >= rule.GetProperty("minGb").GetDouble()));
        var own = Assert.Single(explicitRules.GetProperty("sizeRules").EnumerateArray());
        Assert.Equal(allowed[0].ToLowerInvariant(), own.GetProperty("quality").GetString()!.ToLowerInvariant());
        Assert.Equal(3.0, own.GetProperty("minGb").GetDouble());
        Assert.Equal(8.0, own.GetProperty("maxGb").GetDouble());
        Assert.Equal(100.0, own.GetProperty("minMb").GetDouble());
        Assert.Equal(500.0, own.GetProperty("maxMb").GetDouble());
    }

    [Fact]
    public void Size_answers_are_trimmed_one_per_tier_last_one_wins_and_a_handle_dragged_past_the_other_is_put_back_the_right_way_round()
    {
        var rules = ProfileSizeRulesCodec.Normalize(
        [
            new ProfileSizeRule("  WEB 1080p ", 9, 2, 700, 100),
            new ProfileSizeRule("web 1080p", 4, 12, 0, 0),
            new ProfileSizeRule("Bluray 2160p", 20, 0, 0, 0),
            new ProfileSizeRule("   ", 1, 2, 1, 2),
            new ProfileSizeRule("DVD", -3, -1, -5, -1)
        ]);

        Assert.Equal(["Bluray 2160p", "DVD", "WEB 1080p"], rules.Select(rule => rule.Quality).ToArray());
        Assert.Equal(new ProfileSizeRule("WEB 1080p", 4, 12, 0, 0), rules.Single(rule => rule.Quality == "WEB 1080p"));
        Assert.Equal(new ProfileSizeRule("Bluray 2160p", 20, 0, 0, 0), rules.Single(rule => rule.Quality == "Bluray 2160p"));
        Assert.Equal(new ProfileSizeRule("DVD", 0, 0, 0, 0), rules.Single(rule => rule.Quality == "DVD"));
        var turned = ProfileSizeRulesCodec.Normalize([new ProfileSizeRule("WEB 720p", 9, 2, 700, 100)]).Single();
        Assert.Equal((2d, 9d, 100d, 700d), (turned.MinGb, turned.MaxGb, turned.MinMb, turned.MaxMb));
    }

    [Fact]
    public async Task A_must_contain_pattern_that_is_malformed_is_stored_silently_by_a_profile_but_refused_by_a_release_rule()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TiersAsync(app);

        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", Body("Sloppy", "movies", tiers[0], tiers[0], acquisition: new { preferredProtocol = "any", usenetDelayMinutes = 0, torrentDelayMinutes = 0, mustContain = "/unclosed(/", mustNotContain = "/also[bad/" }));
        var rule = await app.Client.PostAsJsonAsync("/api/release-profiles", new { name = "Sloppy rule", tagName = (string?)null, mustContain = "/unclosed(/", preferredProtocol = "any", usenetDelayMinutes = 0, torrentDelayMinutes = 0 });

        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        var stored = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acquisition");
        Assert.Equal("/unclosed(/", stored.GetProperty("mustContain").GetString());
        Assert.Equal("/also[bad/", stored.GetProperty("mustNotContain").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, rule.StatusCode);
    }

    [Fact]
    public async Task A_library_made_with_no_valid_profile_gets_the_one_with_standard_in_its_name_for_its_media_type_else_the_first_by_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tiers = await TiersAsync(app);
        foreach (var name in new[] { "Zebra films", "Aardvark Standard films" })
        {
            Assert.True((await app.Client.PostAsJsonAsync("/api/quality-profiles", Body(name, "movies", tiers[0], tiers[0]))).IsSuccessStatusCode);
        }

        async Task<string?> ProfileNameOfNewLibraryAsync(string name, string mediaType, string? profileId)
        {
            var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
            var response = await app.Client.PostAsJsonAsync("/api/libraries", new { name, mediaType, rootPath = root, qualityProfileId = profileId });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var profile = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("qualityProfileName");
            return profile.ValueKind == JsonValueKind.String ? profile.GetString() : null;
        }

        var none = await ProfileNameOfNewLibraryAsync("No profile", "movies", null);
        Assert.Equal("Aardvark Standard films", none);
        var standard = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-profiles")).EnumerateArray().First(item => item.GetProperty("name").GetString() == "Aardvark Standard films").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/quality-profiles/{standard}")).StatusCode);
        Assert.Equal("Zebra films", await ProfileNameOfNewLibraryAsync("After delete", "movies", null));
    }

    [Fact]
    public async Task A_custom_format_needs_a_name_and_otherwise_saves_whatever_conditions_text_it_is_given_though_such_a_rule_can_never_match()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var blank = await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = " ", mediaType = "movies", score = 0, conditions = "x", upgradeAllowed = true });
        Assert.Contains("Give this custom format a name.", await ErrorsAsync(blank));
        var conditions = new[] { "", "this is not json at all", "[{\"type\":\"noSuchCondition\",\"value\":\"x\",\"required\":true}]" };
        var ids = new List<string>();
        for (var i = 0; i < conditions.Length; i++)
        {
            var saved = await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = $"Odd {i}", mediaType = "movies", score = 5, conditions = conditions[i], upgradeAllowed = true });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            ids.Add((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        var edited = await app.Client.PutAsJsonAsync($"/api/custom-formats/{ids[0]}", new { name = "Odd 0, edited", mediaType = "movies", score = 5, conditions = "still not json", upgradeAllowed = true });
        var dryRun = await app.Client.PostAsJsonAsync("/api/custom-formats/dry-run", new { releaseName = "Some.Film.2020.1080p.WEB-DL.x264-SCENE", mediaType = "movies" });

        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
        var results = (await dryRun.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.False(result.GetProperty("isMatch").GetBoolean(), "a rule with conditions nobody can evaluate never matches"));
    }

    [Fact]
    public async Task Changing_a_custom_format_refuses_a_blank_name_and_another_rules_name_but_not_its_own()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await (await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = "Group A", mediaType = "movies", score = 1, conditions = "[{\"type\":\"releaseGroup\",\"value\":\"A\",\"required\":true}]", upgradeAllowed = true })).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = "Group B", mediaType = "movies", score = 1, conditions = "[{\"type\":\"releaseGroup\",\"value\":\"B\",\"required\":true}]", upgradeAllowed = true })).Content.ReadFromJsonAsync<JsonElement>();
        var secondId = second.GetProperty("id").GetString()!;

        var blank = await app.Client.PutAsJsonAsync($"/api/custom-formats/{secondId}", new { name = "", mediaType = "movies", score = 1, conditions = "x", upgradeAllowed = true });
        var taken = await app.Client.PutAsJsonAsync($"/api/custom-formats/{secondId}", new { name = " group a ", mediaType = "movies", score = 1, conditions = "x", upgradeAllowed = true });
        var own = await app.Client.PutAsJsonAsync($"/api/custom-formats/{secondId}", new { name = "GROUP B", mediaType = "movies", score = 2, conditions = "x", upgradeAllowed = true });

        Assert.Contains("Give this custom format a name.", await ErrorsAsync(blank));
        Assert.Contains("already exists", await ErrorsAsync(taken));
        Assert.True(own.IsSuccessStatusCode, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_dry_run_refuses_a_blank_release_name_and_otherwise_says_for_every_rule_whether_the_release_matches_and_which_conditions_hit_and_missed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await (await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = "Scene group", mediaType = "movies", score = 10, conditions = "[{\"type\":\"releaseGroup\",\"value\":\"SCENE\",\"required\":true},{\"type\":\"source\",\"value\":\"BluRay\",\"required\":true}]", upgradeAllowed = true })).Content.ReadFromJsonAsync<JsonElement>();
        await app.Client.PostAsJsonAsync("/api/custom-formats", new { name = "TV only", mediaType = "tv", score = 3, conditions = "[{\"type\":\"releaseGroup\",\"value\":\"SCENE\",\"required\":true}]", upgradeAllowed = true });

        var blank = await app.Client.PostAsJsonAsync("/api/custom-formats/dry-run", new { releaseName = " " });
        var movies = await (await app.Client.PostAsJsonAsync("/api/custom-formats/dry-run", new { releaseName = "Some.Film.2020.1080p.WEB-DL.x264-SCENE", mediaType = "movies" })).Content.ReadFromJsonAsync<JsonElement>();
        var every = await (await app.Client.PostAsJsonAsync("/api/custom-formats/dry-run", new { releaseName = "Some.Film.2020.1080p.WEB-DL.x264-SCENE" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("Release name is required.", await ErrorsAsync(blank));
        var onlyOne = Assert.Single(movies.EnumerateArray());
        Assert.Equal(scene.GetProperty("id").GetString(), onlyOne.GetProperty("formatId").GetString());
        Assert.False(onlyOne.GetProperty("isMatch").GetBoolean(), "SCENE is the group but the source is WEB-DL, not BluRay");
        Assert.NotEmpty(onlyOne.GetProperty("matchedConditions").EnumerateArray());
        Assert.NotEmpty(onlyOne.GetProperty("missedConditions").EnumerateArray());
        Assert.Equal(2, every.GetArrayLength());
    }
}
