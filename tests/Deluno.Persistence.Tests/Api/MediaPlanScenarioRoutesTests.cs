using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The scenario catalogue the "Start from a scenario" box reads, and what each scenario would do, over the real routes:
/// the list narrows to one kind of library, a scenario compiles for the kind asked, a scenario that serves both kinds
/// needs one to be named, and an unknown scenario is a 404.
/// </summary>
public sealed class MediaPlanScenarioRoutesTests
{
    private static async Task<JsonElement[]> ListAsync(ApplicationTestHost app, string? mediaType = null)
    {
        var path = mediaType is null ? "/api/media-plan-scenarios" : $"/api/media-plan-scenarios?mediaType={mediaType}";
        var response = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string[] MediaTypesOf(JsonElement scenario) => scenario.GetProperty("mediaTypes").EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Fact]
    public async Task The_catalogue_lists_each_scenario_with_its_name_kinds_requirements_version_and_variants_and_narrows_to_one_kind_of_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var all = await ListAsync(app);
        var movies = await ListAsync(app, "movies");
        var tv = await ListAsync(app, "tv");

        Assert.NotEmpty(all);
        foreach (var scenario in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(scenario.GetProperty("name").GetString()));
            Assert.True(scenario.GetProperty("version").GetInt32() > 0);
            Assert.NotEmpty(MediaTypesOf(scenario));
            Assert.NotEmpty(scenario.GetProperty("variants").EnumerateArray());
        }

        Assert.All(movies, scenario => Assert.Contains("movies", MediaTypesOf(scenario)));
        Assert.All(tv, scenario => Assert.Contains("tv", MediaTypesOf(scenario)));
        Assert.Equal(all.Count(scenario => MediaTypesOf(scenario).Contains("movies")), movies.Length);
        Assert.Equal(all.Count(scenario => MediaTypesOf(scenario).Contains("tv")), tv.Length);
    }

    [Fact]
    public async Task A_scenario_compiles_for_the_kind_asked_and_says_what_each_of_its_behaviours_will_do()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scenario = (await ListAsync(app, "movies")).First();
        var id = scenario.GetProperty("id").GetString()!;

        var response = await app.Client.GetAsync($"/api/media-plan-scenarios/{id}/compile?mediaType=movies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var compiled = await response.Content.ReadFromJsonAsync<JsonElement>();
        var behaviors = compiled.GetProperty("behaviors").EnumerateArray().ToArray();
        Assert.NotEmpty(behaviors);
        Assert.All(behaviors, behavior =>
        {
            Assert.False(string.IsNullOrWhiteSpace(behavior.GetProperty("area").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(behavior.GetProperty("intent").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(behavior.GetProperty("explanation").GetString()));
            Assert.Contains(behavior.GetProperty("applicationStatus").GetString(), new[] { "applied", "requires-configuration", "informational" });
        });
    }

    [Fact]
    public async Task A_scenario_that_serves_both_kinds_needs_one_named_and_an_unknown_scenario_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var both = (await ListAsync(app)).FirstOrDefault(scenario => MediaTypesOf(scenario).Length > 1);

        if (both.ValueKind == JsonValueKind.Object)
        {
            var id = both.GetProperty("id").GetString()!;
            var response = await app.Client.GetAsync($"/api/media-plan-scenarios/{id}/compile");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("mediaType", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        var unknown = await app.Client.GetAsync("/api/media-plan-scenarios/no-such-scenario/compile?mediaType=movies");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_can_read_the_catalogue()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/media-plan-scenarios")).StatusCode);
    }
}
