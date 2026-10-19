using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The library settings routes (coverage register, area api): the automation switches, the subtitle languages, the media plan and the
/// saved views. Each is called the way the screen calls it and then read back from the library list and from the database file, so what is
/// saved is what a later search cycle will read, and a refusal stores nothing.
/// </summary>
public sealed class LibrarySettingsRoutesDoWhatTheySayTests
{
    private static async Task<JsonElement> LibraryAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    private static async Task<string> ProfileAsync(ApplicationTestHost app, string mediaType)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name = mediaType + " profile",
            mediaType,
            cutoffQuality = "WEB 1080p",
            allowedQualities = "WEB 1080p,WEB 2160p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = true,
            upgradeUnknownItems = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> PlanAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var profileId = await ProfileAsync(app, mediaType);
        var response = await app.Client.PostAsJsonAsync("/api/policy-sets", new
        {
            name,
            mediaType,
            qualityProfileId = profileId,
            destinationRuleId = (string?)null,
            customFormatIds = (string?)null,
            searchIntervalOverrideHours = (int?)null,
            retryDelayOverrideHours = (int?)null,
            upgradeUntilCutoff = true,
            isEnabled = true,
            notes = (string?)null
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Saving_a_librarys_automation_switches_changes_what_its_search_cycle_reads_and_a_nonsense_number_is_refused_with_nothing_changed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await ApiScenes.CreateLibraryAsync(app, "movies");
        Assert.False((await LibraryAsync(app, id)).GetProperty("autoSearchEnabled").GetBoolean());

        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{id}/automation", new
        {
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = 90,
            retryDelayHours = 12,
            maxItemsPerRun = 7,
            searchWindowStartHour = 1,
            searchWindowEndHour = 6
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var library = await LibraryAsync(app, id);
        Assert.True(library.GetProperty("autoSearchEnabled").GetBoolean());
        Assert.True(library.GetProperty("missingSearchEnabled").GetBoolean());
        Assert.False(library.GetProperty("upgradeSearchEnabled").GetBoolean());
        Assert.Equal(90, library.GetProperty("searchIntervalMinutes").GetInt32());
        Assert.Equal(12, library.GetProperty("retryDelayHours").GetInt32());
        Assert.Equal(1, library.GetProperty("searchWindowStartHour").GetInt32());
        Assert.Equal(6, library.GetProperty("searchWindowEndHour").GetInt32());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", $"SELECT auto_search_enabled FROM libraries WHERE id = '{id}'"));
        Assert.Equal(90, await ApiScenes.ScalarAsync(app, "platform", $"SELECT search_interval_minutes FROM libraries WHERE id = '{id}'"));

        var refused = await app.Client.PutAsJsonAsync($"/api/libraries/{id}/automation", new
        {
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalMinutes = 0,
            retryDelayHours = 12,
            maxItemsPerRun = 7
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Choose how often Deluno should check this library.", await refused.Content.ReadAsStringAsync());
        Assert.True((await LibraryAsync(app, id)).GetProperty("autoSearchEnabled").GetBoolean());
        Assert.Equal(90, (await LibraryAsync(app, id)).GetProperty("searchIntervalMinutes").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/automation", new
        {
            autoSearchEnabled = true, missingSearchEnabled = true, upgradeSearchEnabled = true, searchIntervalMinutes = 60, retryDelayHours = 6, maxItemsPerRun = 5
        })).StatusCode);
    }

    [Fact]
    public async Task Saving_a_librarys_subtitle_languages_keeps_them_in_order_by_their_two_letter_code_without_repeats_and_with_the_mode_chosen()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await ApiScenes.CreateLibraryAsync(app, "movies");

        var saved = await app.Client.PutAsJsonAsync($"/api/libraries/{id}/subtitles", new { languages = new[] { "English", "FR", "en", "fra" }, mode = "first" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var library = await LibraryAsync(app, id);
        Assert.Equal(new[] { "en", "fr" }, library.GetProperty("subtitleLanguages").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("first", library.GetProperty("subtitleLanguageMode").GetString());
        Assert.Equal("en,fr", await ApiScenes.TextAsync(app, "platform", $"SELECT subtitle_languages FROM libraries WHERE id = '{id}'"));

        var cleared = await app.Client.PutAsJsonAsync($"/api/libraries/{id}/subtitles", new { languages = Array.Empty<string>(), mode = "all" });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await LibraryAsync(app, id)).GetProperty("subtitleLanguages").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/subtitles", new { languages = new[] { "en" }, mode = "all" })).StatusCode);
    }

    [Fact]
    public async Task Attaching_a_media_plan_to_a_library_shows_it_there_a_plan_for_the_other_kind_is_refused_and_attaching_none_takes_it_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var filmPlan = await PlanAsync(app, "movies", "Films, 1080p");
        var showPlan = await PlanAsync(app, "tv", "Shows, 1080p");

        var attached = await app.Client.PutAsJsonAsync($"/api/libraries/{movies}/media-plan", new { policySetId = filmPlan });

        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        var library = await LibraryAsync(app, movies);
        Assert.Equal(filmPlan, library.GetProperty("defaultPolicySetId").GetString());
        Assert.Equal("Films, 1080p", library.GetProperty("defaultPolicySetName").GetString());
        Assert.Equal(filmPlan, await ApiScenes.TextAsync(app, "platform", $"SELECT default_policy_set_id FROM libraries WHERE id = '{movies}'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'movies.quality.recalculate'"));

        var wrongKind = await app.Client.PutAsJsonAsync($"/api/libraries/{movies}/media-plan", new { policySetId = showPlan });

        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Equal(filmPlan, (await LibraryAsync(app, movies)).GetProperty("defaultPolicySetId").GetString());

        var removed = await app.Client.PutAsJsonAsync($"/api/libraries/{movies}/media-plan", new { policySetId = (string?)null });

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await LibraryAsync(app, movies)).GetProperty("defaultPolicySetId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/libraries/no-such-library/media-plan", new { policySetId = filmPlan })).StatusCode);
    }

    [Fact]
    public async Task A_saved_view_is_kept_for_the_person_edited_in_place_and_refuses_a_blank_name_or_a_library_that_is_not_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        object View(string name, string? libraryId, string filter) => new
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

        var created = await app.Client.PostAsJsonAsync("/api/library-views", View("Missing only", library, "missing"));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var listed = (await app.Client.GetFromJsonAsync<JsonElement>("/api/library-views?variant=movies")).EnumerateArray().Single();
        Assert.Equal("Missing only", listed.GetProperty("name").GetString());
        Assert.Equal("missing", listed.GetProperty("quickFilter").GetString());
        Assert.Equal(library, listed.GetProperty("libraryId").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM library_views"));

        var edited = await app.Client.PutAsJsonAsync($"/api/library-views/{id}", new
        {
            libraryId = (string?)null,
            name = "Upgrades",
            quickFilter = "upgrade",
            monitoring = "any",
            sortField = "year",
            sortDirection = "desc",
            viewMode = "table",
            cardSize = "small",
            displayOptionsJson = "{}",
            rulesJson = "[]"
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var after = (await app.Client.GetFromJsonAsync<JsonElement>("/api/library-views?variant=movies")).EnumerateArray().Single();
        Assert.Equal(id, after.GetProperty("id").GetString());
        Assert.Equal("Upgrades", after.GetProperty("name").GetString());
        Assert.Equal("year", after.GetProperty("sortField").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("libraryId").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/library-views", View("   ", null, "all"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/library-views", View("Elsewhere", "no-such-library", "all"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/library-views/no-such-view", View("x", null, "all"))).StatusCode);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "platform", "SELECT COUNT(*) FROM library_views"));
    }
}
