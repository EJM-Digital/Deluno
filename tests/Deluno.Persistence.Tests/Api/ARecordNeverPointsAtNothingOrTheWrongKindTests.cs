using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1316: every create or update route that stores the id of another record
/// (quality profile, media plan, library, download client, indexer, proxy,
/// processor connection, destination rule, notification connection) checks it
/// exists and is the right kind, through one helper, and refuses with a plain
/// 400 that names it. A saved record can never point at nothing.
/// </summary>
public sealed class ARecordNeverPointsAtNothingOrTheWrongKindTests
{
    private const string Missing = "no-such-record";

    [Fact]
    public async Task Every_route_that_stores_a_reference_refuses_one_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);
        var client = app.Client;
        var libraryId = scene.FilmsLibrary;

        // Media plans
        await RefusedAsync("PUT policy-sets unknown profile", client.PutAsJsonAsync($"/api/policy-sets/{scene.FilmsPlan}", Plan("Films plan", "movies", Missing)), Missing);
        await RefusedAsync("PUT policy-sets wrong-type profile", client.PutAsJsonAsync($"/api/policy-sets/{scene.FilmsPlan}", Plan("Films plan", "movies", scene.TvProfile)), "made for TV");
        await RefusedAsync("PUT policy-sets unknown destination rule", client.PutAsJsonAsync($"/api/policy-sets/{scene.FilmsPlan}", new { name = "Films plan", mediaType = "movies", qualityProfileId = scene.FilmsProfile, destinationRuleId = Missing, upgradeUntilCutoff = true, isEnabled = true }), Missing);
        await RefusedAsync("POST policy-sets unknown destination rule", client.PostAsJsonAsync("/api/policy-sets", new { name = "Another", mediaType = "movies", destinationRuleId = Missing, upgradeUntilCutoff = true, isEnabled = true }), Missing);
        await RefusedAsync("POST policy-sets preview unknown profile", client.PostAsJsonAsync($"/api/policy-sets/{scene.FilmsPlan}/preview", Plan("Films plan", "movies", Missing)), Missing);

        // Libraries
        await RefusedAsync("POST libraries unknown profile", client.PostAsJsonAsync("/api/libraries", Library("A", "movies", scene.Folder, qualityProfileId: Missing)), Missing);
        await RefusedAsync("POST libraries wrong-type profile", client.PostAsJsonAsync("/api/libraries", Library("B", "movies", scene.Folder, qualityProfileId: scene.TvProfile)), "made for TV");
        await RefusedAsync("POST libraries unknown processor", client.PostAsJsonAsync("/api/libraries", Library("C", "movies", scene.Folder, processorConnectionId: Missing)), Missing);
        await RefusedAsync("PUT library media-plan unknown plan", client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = Missing }), Missing);
        await RefusedAsync("PUT library media-plan wrong-type plan", client.PutAsJsonAsync($"/api/libraries/{libraryId}/media-plan", new { policySetId = scene.TvPlan }), "made for TV");
        await RefusedAsync("PUT library quality-profile unknown", client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = Missing }), Missing);
        await RefusedAsync("PUT library quality-profile wrong type", client.PutAsJsonAsync($"/api/libraries/{libraryId}/quality-profile", new { qualityProfileId = scene.TvProfile }), "made for TV");
        await RefusedAsync("PUT library workflow unknown processor", client.PutAsJsonAsync($"/api/libraries/{libraryId}/workflow", new { importWorkflow = "standard", processorConnectionId = Missing }), Missing);
        await RefusedAsync("PUT library routing unknown indexer", client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = new[] { new { indexerId = Missing, priority = 1 } }, downloadClients = Array.Empty<object>() }), Missing);
        await RefusedAsync("PUT library routing unknown client", client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = new[] { new { downloadClientId = Missing, priority = 1 } } }), Missing);

        // Destination rules and saved views
        var rule = new { name = "Rule", mediaType = "movies", matchKind = "genre", matchValue = "Drama", rootPath = scene.Folder, priority = 1, isEnabled = true, libraryId = Missing };
        await RefusedAsync("POST destination-rules unknown library", client.PostAsJsonAsync("/api/destination-rules", rule), Missing);
        await RefusedAsync("PUT destination-rules unknown library", client.PutAsJsonAsync("/api/destination-rules/anything", rule), Missing);
        var view = new { variant = "movies", libraryId = Missing, name = "View", quickFilter = "all", monitoring = "any", sortField = "title", sortDirection = "asc" };
        await RefusedAsync("POST library-views unknown library", client.PostAsJsonAsync("/api/library-views", view), Missing);
        await RefusedAsync("PUT library-views unknown library", client.PutAsJsonAsync("/api/library-views/anything", view), Missing);

        // Lists, request servers, indexers
        await RefusedAsync("POST intake-sources unknown library", client.PostAsJsonAsync("/api/intake-sources", Feed(libraryId: Missing)), Missing);
        await RefusedAsync("POST intake-sources wrong-type profile", client.PostAsJsonAsync("/api/intake-sources", Feed(qualityProfileId: scene.TvProfile)), "made for TV");
        await RefusedAsync("PUT intake-sources unknown profile", client.PutAsJsonAsync("/api/intake-sources/anything", Feed(qualityProfileId: Missing)), Missing);
        await RefusedAsync("POST seerr unknown movie library", client.PostAsJsonAsync("/api/seerr-servers", Seerr(movieLibraryId: Missing)), Missing);
        await RefusedAsync("POST seerr tv profile for movies", client.PostAsJsonAsync("/api/seerr-servers", Seerr(movieQualityProfileId: scene.TvProfile)), "made for TV");
        await RefusedAsync("POST seerr films library for series", client.PostAsJsonAsync("/api/seerr-servers", Seerr(seriesLibraryId: libraryId)), "made for movies");
        var indexer = new { name = "Planet", protocol = "newznab", baseUrl = "api.nzbplanet.net", apiKey = "k", mediaScope = "both", isEnabled = true, proxyId = Missing };
        await RefusedAsync("POST indexers unknown proxy", client.PostAsJsonAsync("/api/indexers", indexer), Missing);
        await RefusedAsync("PUT indexers unknown proxy", client.PutAsJsonAsync("/api/indexers/anything", new { proxyId = Missing }), Missing);

        // Tags on connections, auto-tag rules, release rules
        await RefusedAsync("POST tags bulk-connections unknown indexer", client.PostAsJsonAsync("/api/tags/bulk-connections", new { kind = "indexer", ids = new[] { Missing }, tags = new[] { "kids" }, mode = "add" }), Missing);
        await RefusedAsync("POST tags bulk-connections unknown client", client.PostAsJsonAsync("/api/tags/bulk-connections", new { kind = "downloadClient", ids = new[] { Missing }, tags = new[] { "kids" }, mode = "add" }), Missing);
        await RefusedAsync("POST tags bulk-connections unknown notification", client.PostAsJsonAsync("/api/tags/bulk-connections", new { kind = "notification", ids = new[] { Missing }, tags = new[] { "kids" }, mode = "add" }), Missing);
        await RefusedAsync("POST auto-tag-rules unknown profile", client.PostAsJsonAsync("/api/auto-tag-rules", AutoTag("qualityProfile", Missing)), Missing);
        await RefusedAsync("PUT auto-tag-rules unknown library", client.PutAsJsonAsync("/api/auto-tag-rules/anything", AutoTag("library", Missing)), Missing);
        await RefusedAsync("POST release-profiles unknown indexer", client.PostAsJsonAsync("/api/release-profiles", new { name = "Rule", tagNames = new[] { "kids" }, indexerIds = new[] { Missing } }), Missing);
        await RefusedAsync("PUT release-profiles unknown indexer", client.PutAsJsonAsync("/api/release-profiles/anything", new { indexerIds = new[] { Missing } }), Missing);

        // Titles: bulk quality, bulk move, collections
        await RefusedAsync("POST movies bulk quality unknown profile", client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { "m1" }, operation = "quality", qualityProfileId = Missing }), Missing);
        await RefusedAsync("POST movies bulk quality-profile wrong type", client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { "m1" }, qualityProfileId = scene.TvProfile }), "made for TV");
        await RefusedAsync("POST series bulk quality-profile wrong type", client.PostAsJsonAsync("/api/series/bulk/quality-profile", new { seriesIds = new[] { "s1" }, qualityProfileId = scene.FilmsProfile }), "made for movies");
        await RefusedAsync("POST series bulk quality unknown profile", client.PostAsJsonAsync("/api/series/bulk", new { seriesIds = new[] { "s1" }, operation = "quality", qualityProfileId = Missing }), Missing);
        await RefusedAsync("POST movies reassign-library unknown library", client.PostAsJsonAsync("/api/movies/bulk/reassign-library", new { movieIds = new[] { "m1" }, fromLibraryId = libraryId, toLibraryId = Missing }), Missing);
        await RefusedAsync("POST series reassign-library films library", client.PostAsJsonAsync("/api/series/bulk/reassign-library", new { seriesIds = new[] { "s1" }, fromLibraryId = scene.ShowsLibrary, toLibraryId = libraryId }), "made for movies");
        await RefusedAsync("POST movie-collections unknown profile", client.PostAsJsonAsync("/api/movie-collections/", new { providerId = "10", libraryId, qualityProfileId = Missing }), Missing);
        await RefusedAsync("PUT movie-collections wrong-type profile", client.PutAsJsonAsync("/api/movie-collections/anything", new { qualityProfileId = scene.TvProfile }), "made for TV");
        await RefusedAsync("PUT movie-collections bulk unknown profile", client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = new[] { "c1" }, qualityProfileId = Missing }), Missing);

        // A download client that is not there already answers 404 for its own path mappings.
        using var mapping = await client.PostAsJsonAsync($"/api/download-clients/{Missing}/path-mappings", new { remotePath = "/downloads", localPath = scene.Folder, isEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, mapping.StatusCode);
    }

    [Fact]
    public async Task A_reference_that_is_there_and_the_right_kind_is_still_accepted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scene = await SceneAsync(app);

        using var plan = await app.Client.PutAsJsonAsync($"/api/policy-sets/{scene.FilmsPlan}", Plan("Films plan", "movies", scene.FilmsProfile));
        using var profile = await app.Client.PutAsJsonAsync($"/api/libraries/{scene.FilmsLibrary}/quality-profile", new { qualityProfileId = scene.FilmsProfile });
        using var assigned = await app.Client.PutAsJsonAsync($"/api/libraries/{scene.FilmsLibrary}/media-plan", new { policySetId = scene.FilmsPlan });
        using var none = await app.Client.PutAsJsonAsync($"/api/libraries/{scene.FilmsLibrary}/media-plan", new { policySetId = (string?)null });

        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync());
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        Assert.True(none.IsSuccessStatusCode, await none.Content.ReadAsStringAsync());
    }

    private static async Task RefusedAsync(string route, Task<HttpResponseMessage> sending, string mustSay)
    {
        using var response = await sending;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{route}: expected 400, got {(int)response.StatusCode}: {body}");
        Assert.True(body.Contains(mustSay, StringComparison.OrdinalIgnoreCase), $"{route}: the answer should say \"{mustSay}\": {body}");
    }

    private static object Plan(string name, string mediaType, string qualityProfileId)
        => new { name, mediaType, qualityProfileId, customFormatIds = string.Empty, upgradeUntilCutoff = true, isEnabled = true };

    private static object AutoTag(string type, string value)
        => new { name = "Rule", tags = new[] { "kids" }, specifications = new[] { new { type, negate = false, value } } };

    private static object Library(string name, string mediaType, string folder, string? qualityProfileId = null, string? processorConnectionId = null)
        => new { name, mediaType, rootPath = folder, qualityProfileId, processorConnectionId };

    private static object Feed(string? libraryId = null, string? qualityProfileId = null)
        => new { name = "Feed", provider = "rss", feedUrl = "https://example.invalid/feed.xml", mediaType = "movies", isEnabled = true, libraryId, qualityProfileId };

    private static object Seerr(string? movieLibraryId = null, string? movieQualityProfileId = null, string? seriesLibraryId = null)
        => new { baseUrl = "http://request.example:5055", apiKey = "k", isEnabled = true, movieLibraryId, movieQualityProfileId, seriesLibraryId };

    private sealed record Scene(string Folder, string FilmsLibrary, string ShowsLibrary, string FilmsProfile, string TvProfile, string FilmsPlan, string TvPlan);

    private static async Task<Scene> SceneAsync(ApplicationTestHost app)
    {
        var folder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var showsFolder = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Shows")).FullName;
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers")
            .EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var filmsProfile = await CreateAsync(app, "/api/quality-profiles", new { name = "Films profile", mediaType = "movies", cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false });
        var tvProfile = await CreateAsync(app, "/api/quality-profiles", new { name = "Shows profile", mediaType = "tv", cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty, upgradeUntilCutoff = true, upgradeUnknownItems = false });
        var films = await CreateAsync(app, "/api/libraries", new { name = "Films", mediaType = "movies", rootPath = folder });
        var shows = await CreateAsync(app, "/api/libraries", new { name = "Shows", mediaType = "tv", rootPath = showsFolder });
        var filmsPlan = await CreateAsync(app, "/api/policy-sets", Plan("Films plan", "movies", filmsProfile));
        var tvPlan = await CreateAsync(app, "/api/policy-sets", Plan("Shows plan", "tv", tvProfile));
        return new Scene(folder, films, shows, filmsProfile, tvProfile, filmsPlan, tvPlan);
    }

    private static async Task<string> CreateAsync(ApplicationTestHost app, string path, object body)
    {
        using var response = await app.Client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, path + ": " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
