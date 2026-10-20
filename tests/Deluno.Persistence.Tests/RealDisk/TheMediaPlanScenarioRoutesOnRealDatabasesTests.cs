using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Scenarios;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The starting-scenario routes of the Media Plan screen on the real application: the seven scenarios (all, or those that
/// apply to movies or TV in any of the spellings of TV), and the plan request each one compiles to. Refs #1296.
/// </summary>
public sealed class TheMediaPlanScenarioRoutesOnRealDatabasesTests
{
    private static readonly string[] Seven =
        ["family-1080p", "premium-4k-hdr", "low-storage", "usenet-first", "private-tracker", "mixed-sources", "anime"];

    private static string[] Ids(JsonElement list) => list.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task The_seven_scenarios_are_listed_with_what_they_need_and_filtered_to_movies_or_tv_in_any_spelling_of_tv()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var all = await app.Client.GetFromJsonAsync<JsonElement>("/api/media-plan-scenarios");
        Assert.Equal(Seven, Ids(all));
        foreach (var scenario in all.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("name").GetString()));
            Assert.True(scenario.GetProperty("mediaTypes").GetArrayLength() > 0);
            Assert.True(scenario.GetProperty("requirements").GetArrayLength() > 0);
            Assert.True(scenario.GetProperty("variants").GetArrayLength() > 0);
        }

        var movies = await app.Client.GetFromJsonAsync<JsonElement>("/api/media-plan-scenarios?mediaType=movies");
        Assert.DoesNotContain("anime", Ids(movies));
        Assert.Contains("family-1080p", Ids(movies));
        foreach (var spelling in new[] { "tv", "TV", "tv%20shows", "tvshows" })
        {
            var tv = await app.Client.GetFromJsonAsync<JsonElement>($"/api/media-plan-scenarios?mediaType={spelling}");
            Assert.Contains("anime", Ids(tv));
            Assert.Contains("family-1080p", Ids(tv));
        }
    }

    [Fact]
    public async Task A_scenario_compiles_to_the_same_plan_request_every_time_with_each_behaviour_marked_applied_needing_setup_or_informational()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await app.Client.GetStringAsync("/api/media-plan-scenarios/premium-4k-hdr/compile?mediaType=movies");
        var second = await app.Client.GetStringAsync("/api/media-plan-scenarios/premium-4k-hdr/compile?mediaType=movies");
        Assert.Equal(first, second);

        var compiled = JsonDocument.Parse(first).RootElement;
        var plan = compiled.GetProperty("policySet");
        Assert.Equal("movies", plan.GetProperty("mediaType").GetString());
        Assert.Equal(6, plan.GetProperty("searchIntervalOverrideHours").GetInt32());
        Assert.Equal(3, plan.GetProperty("retryDelayOverrideHours").GetInt32());
        Assert.True(plan.GetProperty("upgradeUntilCutoff").GetBoolean());
        Assert.True(plan.GetProperty("isEnabled").GetBoolean());
        Assert.Contains("Scenario: premium-4k-hdr v1", plan.GetProperty("notes").GetString());
        Assert.Equal("premium-4k-hdr", plan.GetProperty("automationIntent").GetProperty("scenarioId").GetString());
        var statuses = compiled.GetProperty("behaviors").EnumerateArray().Select(item => item.GetProperty("applicationStatus").GetString()).ToHashSet();
        Assert.Subset(new HashSet<string?> { "applied", "requires-configuration", "informational" }, statuses);
        Assert.Contains("applied", statuses);
        Assert.Contains("requires-configuration", statuses);
        Assert.Contains("informational", statuses);
    }

    [Fact]
    public async Task A_name_given_is_the_name_the_plan_gets_and_the_enabled_flag_is_honoured()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var named = await app.Client.GetFromJsonAsync<JsonElement>("/api/media-plan-scenarios/family-1080p/compile?mediaType=tv&name=Evening%20shows");
        Assert.Equal("Evening shows", named.GetProperty("policySet").GetProperty("name").GetString());
        var unnamed = await app.Client.GetFromJsonAsync<JsonElement>("/api/media-plan-scenarios/family-1080p/compile?mediaType=tv");
        Assert.Contains("TV", unnamed.GetProperty("policySet").GetProperty("name").GetString());

        // The route does not carry an enabled flag; the compiler it calls does, and a plan switched off stays switched off.
        Assert.False(MediaPlanScenarioCompiler.Compile("family-1080p", "movies", null, isEnabled: false).PolicySet.IsEnabled);
        Assert.True(MediaPlanScenarioCompiler.Compile("family-1080p", "movies").PolicySet.IsEnabled);
    }

    [Fact]
    public async Task An_unknown_scenario_is_not_found_and_a_scenario_for_both_kinds_without_a_kind_or_for_the_other_kind_is_refused_naming_the_field()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/media-plan-scenarios/no-such-scenario/compile?mediaType=movies")).StatusCode);

        var noKind = await app.Client.GetAsync("/api/media-plan-scenarios/family-1080p/compile");
        Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);
        Assert.Contains("mediaType", await noKind.Content.ReadAsStringAsync());

        var wrongKind = await app.Client.GetAsync("/api/media-plan-scenarios/anime/compile?mediaType=movies");
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        var body = await wrongKind.Content.ReadAsStringAsync();
        Assert.Contains("mediaType", body);
        Assert.Contains("does not apply", body);

        // A scenario that has only one kind needs none given.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/media-plan-scenarios/anime/compile")).StatusCode);
    }
}
