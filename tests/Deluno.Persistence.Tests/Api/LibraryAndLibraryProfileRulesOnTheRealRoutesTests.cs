using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the library drawer and the Library Profile drawer rely on the server to refuse and to do, over the real routes and
/// the real databases: a library without a name, folder or kind is refused in plain words and stored nowhere, a profile
/// without a name is refused, and giving a library a Library Profile queues a quality recalculation for that library.
/// </summary>
public sealed class LibraryAndLibraryProfileRulesOnTheRealRoutesTests
{
    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
    }

    private static string First(JsonElement errors, string field) => errors.GetProperty(field)[0].GetString()!;

    private static async Task<string> CreateProfileAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var profile = await app.Client.PostAsJsonAsync("/api/quality-profiles", new { name, mediaType, cutoffQuality = "WEB 1080p", allowedQualities = "WEB 1080p", customFormatIds = "", upgradeUntilCutoff = true, upgradeUnknownItems = false });
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        return (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreatePlanAsync(ApplicationTestHost app, string mediaType, string name, string profileId)
    {
        var plan = await app.Client.PostAsJsonAsync("/api/policy-sets", new { name, mediaType, qualityProfileId = profileId, customFormatIds = "", upgradeUntilCutoff = true, isEnabled = true });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        return (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_library_with_no_name_no_folder_and_no_kind_is_refused_in_plain_words_and_nothing_is_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var errors = await ProblemAsync(await app.Client.PostAsJsonAsync("/api/libraries/", new { name = " ", mediaType = "podcasts", purpose = "collection", rootPath = "" }));

        Assert.Equal("Give this library a name.", First(errors, "name"));
        Assert.Equal("Choose a folder for this library.", First(errors, "rootPath"));
        Assert.Equal("Choose Movies or TV Shows.", First(errors, "mediaType"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM libraries;"));
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    [InlineData("TV Shows")]
    public async Task A_library_of_either_kind_is_accepted_when_it_has_a_name_and_a_folder_that_exists(string mediaType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "lib")).FullName;

        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Mine", mediaType, purpose = "collection", rootPath = root, importWorkflow = "copy" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM libraries;"));
    }

    [Fact]
    public async Task A_library_folder_that_does_not_exist_is_refused_and_nothing_is_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var errors = await ProblemAsync(await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Mine", mediaType = "movies", purpose = "collection", rootPath = Path.Combine(app.DataRoot, "not-made") }));

        Assert.Contains("rootPath", errors.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM libraries;"));
    }

    [Theory]
    [InlineData("movies", "movies.quality.recalculate")]
    [InlineData("tv", "series.quality.recalculate")]
    public async Task Giving_a_library_a_Library_Profile_queues_one_quality_recalculation_for_that_library_and_taking_it_away_queues_another(string mediaType, string jobType)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, mediaType);
        var planId = await CreatePlanAsync(app, mediaType, "The plan", await CreateProfileAsync(app, mediaType, "Plan profile"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE job_type = '{jobType}';"));

        var assigned = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = planId });

        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE job_type = '{jobType}' AND related_entity_type = 'library' AND related_entity_id = '{libraryId}';"));
        var payload = JsonDocument.Parse((await ApiScenes.TextAsync(app, "jobs", $"SELECT payload_json FROM job_queue WHERE job_type = '{jobType}';"))!).RootElement;
        Assert.Equal(libraryId, payload.GetProperty("libraryId").GetString());
        Assert.Equal(planId, payload.GetProperty("policySetId").GetString());
        Assert.Equal(mediaType, payload.GetProperty("mediaType").GetString());

        var removed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = (string?)null });

        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM job_queue WHERE job_type = '{jobType}' AND related_entity_id = '{libraryId}';"));
    }

    [Fact]
    public async Task A_profile_for_the_other_kind_of_library_and_a_library_that_is_not_there_queue_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var filmLibrary = await ApiScenes.CreateLibraryAsync(app, "movies");
        var tvPlanId = await CreatePlanAsync(app, "tv", "TV plan", await CreateProfileAsync(app, "tv", "TV profile"));

        var wrongKind = await app.Client.PutAsJsonAsync($"/api/libraries/{filmLibrary}/media-plan", new { policySetId = tvPlanId });
        var missing = await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/media-plan", new { policySetId = tvPlanId });

        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type LIKE '%quality.recalculate';"));
    }

    [Fact]
    public async Task A_Library_Profile_with_no_name_is_refused_on_create_and_on_change_and_nothing_is_stored()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = await CreateProfileAsync(app, "movies", "P");

        var errors = await ProblemAsync(await app.Client.PostAsJsonAsync("/api/policy-sets", new { name = "  ", mediaType = "movies", qualityProfileId = profileId, customFormatIds = "", upgradeUntilCutoff = true, isEnabled = true }));
        Assert.Equal("Give this policy set a name.", First(errors, "name"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM policy_sets;"));

        var planId = await CreatePlanAsync(app, "movies", "Fine", profileId);
        var renamed = await ProblemAsync(await app.Client.PutAsJsonAsync($"/api/policy-sets/{planId}", new { name = "", mediaType = "movies", qualityProfileId = profileId, customFormatIds = "", upgradeUntilCutoff = true, isEnabled = true }));
        Assert.Equal("Give this policy set a name.", First(renamed, "name"));
        Assert.Equal("Fine", await ApiScenes.TextAsync(app, "platform", $"SELECT name FROM policy_sets WHERE id = '{planId}';"));
    }
}
