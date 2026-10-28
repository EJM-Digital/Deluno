using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the lists a person fills by saving things are read back with what was saved, in the order the screen shows them: the library
/// views by name, the sharing rules by priority.
/// </summary>
public sealed class TheSavedViewsAndSharingRulesListWhatWasSavedTests
{
    private static object View(string name, string? libraryId, string filter) => new
    {
        variant = "movies",
        libraryId,
        name,
        quickFilter = filter,
        monitoring = "monitored",
        sortField = "title",
        sortDirection = "asc",
        viewMode = "poster",
        cardSize = "medium",
        displayOptionsJson = "{}",
        rulesJson = "[]"
    };

    [Fact]
    public async Task The_saved_views_are_listed_with_their_name_library_and_quick_filter()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        Assert.True((await app.Client.PostAsJsonAsync("/api/library-views", View("Missing only", library, "missing"))).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/library-views", View("Everything", null, "all"))).IsSuccessStatusCode);

        var views = (await app.Client.GetFromJsonAsync<JsonElement>("/api/library-views")).EnumerateArray().ToArray();

        Assert.Equal(2, views.Length);
        var missing = Assert.Single(views, view => view.GetProperty("name").GetString() == "Missing only");
        Assert.Equal(library, missing.GetProperty("libraryId").GetString());
        Assert.Equal("missing", missing.GetProperty("quickFilter").GetString());
        var everything = Assert.Single(views, view => view.GetProperty("name").GetString() == "Everything");
        Assert.Equal(JsonValueKind.Null, everything.GetProperty("libraryId").ValueKind);
        Assert.Equal("all", everything.GetProperty("quickFilter").GetString());
    }

    [Fact]
    public async Task The_sharing_rules_are_listed_by_priority_with_what_each_matches_and_how_long_it_shares()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.True((await app.Client.PostAsJsonAsync("/api/sharing-rules", new { priority = 5, categoryPattern = "movies", forHours = 72, minSeeders = 2 })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/sharing-rules", new { priority = 1, trackerPattern = "example-tracker", forHours = 24 })).IsSuccessStatusCode);

        var rules = (await app.Client.GetFromJsonAsync<JsonElement>("/api/sharing-rules")).EnumerateArray().ToArray();

        Assert.Equal(2, rules.Length);
        Assert.Equal("example-tracker", rules[0].GetProperty("trackerPattern").GetString());
        Assert.Equal(1, rules[0].GetProperty("priority").GetInt32());
        Assert.Equal(24, rules[0].GetProperty("forHours").GetInt32());
        Assert.Equal("movies", rules[1].GetProperty("categoryPattern").GetString());
        Assert.Equal(72, rules[1].GetProperty("forHours").GetInt32());
        Assert.Equal(2, rules[1].GetProperty("minSeeders").GetInt32());
    }
}
