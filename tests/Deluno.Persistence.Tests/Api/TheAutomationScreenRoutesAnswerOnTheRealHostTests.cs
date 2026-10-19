using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The routes the Automation screen reads and writes, on the real host over real SQLite files: the global pause switch, the paged
/// schedule of every library, the search cycles (newest first, per library) and the titles held back until their retry time, the
/// content blocklist and the Retention collections and Seerr clean-ups lists.
/// </summary>
public sealed class TheAutomationScreenRoutesAnswerOnTheRealHostTests
{
    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    // ---- PUT /api/settings/automation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pausing_and_resuming_answers_the_settings_snapshot_pushes_a_Settings_change_and_needs_a_sign_in()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));

        var resumed = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = true });
        var paused = await app.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = false });
        using var anonymous = app.CreateAnonymousClient();
        var refused = await anonymous.PutAsJsonAsync("/api/settings/automation", new { isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.True((await JsonOf(resumed)).GetProperty("autoStartJobs").GetBoolean(), "the answer is the settings snapshot, now resumed");
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.False((await JsonOf(paused)).GetProperty("autoStartJobs").GetBoolean());
        Assert.Equal(2, publisher.Published.Count(item => item is { Type: "Settings", Id: "settings" }));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean(), "a refused call changes nothing");
        Assert.Equal(2, publisher.Published.Count(item => item.Type == "Settings"));
    }

    // ---- GET /api/library-automation, /api/search-cycles, /api/search-retry-windows -----------------------------------------------

    private static async Task<(string Films, string Shows)> TwoLibrariesAsync(ApplicationTestHost app)
    {
        var films = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv", "Shows", "Shows");
        return (films, shows);
    }

    [Fact]
    public async Task The_schedule_lists_every_library_with_its_status_next_searches_and_last_error_and_pages_by_the_size_asked()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (films, shows) = await TwoLibrariesAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.EnsureLibraryAutomationStateAsync(new LibraryAutomationPlanItem(films, "Films", "movies", true, true, true, 60, 6, 25, null, null), CancellationToken.None);
        await jobs.EnsureLibraryAutomationStateAsync(new LibraryAutomationPlanItem(shows, "Shows", "tv", false, false, false, 60, 6, 25, null, null), CancellationToken.None);

        var all = await JsonOf(await app.Client.GetAsync("/api/library-automation"));

        var items = all.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("libraryId").GetString()!);
        Assert.Equal(2, items.Count);
        Assert.Equal("Films", items[films].GetProperty("libraryName").GetString());
        Assert.Equal("movies", items[films].GetProperty("mediaType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(items[films].GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, items[films].GetProperty("lastError").ValueKind);
        Assert.True(items[films].TryGetProperty("nextMissingSearchUtc", out _) && items[films].TryGetProperty("nextUpgradeSearchUtc", out _));
        Assert.Equal("tv", items[shows].GetProperty("mediaType").GetString());

        var first = await JsonOf(await app.Client.GetAsync("/api/library-automation?pageSize=1"));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var second = await JsonOf(await app.Client.GetAsync($"/api/library-automation?pageSize=1&pageToken={Uri.EscapeDataString(first.GetProperty("nextPageToken").GetString()!)}"));
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("libraryId").GetString(), second.GetProperty("items")[0].GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Search_cycles_come_newest_first_and_can_be_narrowed_to_one_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (films, shows) = await TwoLibrariesAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var now = DateTimeOffset.UtcNow;
        await jobs.RecordSearchCycleRunAsync(new RecordSearchCycleRunRequest(films, "Films", "movies", "schedule", "completed", 5, 2, 3, null, now.AddHours(-3), now.AddHours(-3).AddMinutes(1)), CancellationToken.None);
        await jobs.RecordSearchCycleRunAsync(new RecordSearchCycleRunRequest(shows, "Shows", "tv", "schedule", "completed", 4, 1, 3, null, now.AddHours(-2), now.AddHours(-2).AddMinutes(1)), CancellationToken.None);
        await jobs.RecordSearchCycleRunAsync(new RecordSearchCycleRunRequest(films, "Films", "movies", "manual", "completed", 6, 6, 0, null, now.AddHours(-1), now.AddHours(-1).AddMinutes(1)), CancellationToken.None);

        var all = (await JsonOf(await app.Client.GetAsync("/api/search-cycles"))).GetProperty("items").EnumerateArray().ToArray();
        var filmsOnly = (await JsonOf(await app.Client.GetAsync($"/api/search-cycles?libraryId={films}"))).GetProperty("items").EnumerateArray().ToArray();
        var firstPage = await JsonOf(await app.Client.GetAsync("/api/search-cycles?pageSize=2"));

        Assert.Equal(["manual", "schedule", "schedule"], all.Select(item => item.GetProperty("triggerKind").GetString()));
        Assert.Equal([films, shows, films], all.Select(item => item.GetProperty("libraryId").GetString()));
        Assert.Equal([6, 6, 0], [all[0].GetProperty("plannedCount").GetInt32(), all[0].GetProperty("queuedCount").GetInt32(), all[0].GetProperty("skippedCount").GetInt32()]);
        Assert.Equal([films, films], filmsOnly.Select(item => item.GetProperty("libraryId").GetString()));
        Assert.Equal(["manual", "schedule"], filmsOnly.Select(item => item.GetProperty("triggerKind").GetString()));
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Titles_held_back_until_their_retry_time_are_listed_soonest_first_and_can_be_narrowed_to_one_library()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (films, shows) = await TwoLibrariesAsync(app);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var now = DateTimeOffset.UtcNow;
        await jobs.RecordSearchRetryWindowAsync("movie", "film-late", films, "movies", "missing", now.AddHours(20), now.AddHours(-4), "Nothing found.", CancellationToken.None);
        await jobs.RecordSearchRetryWindowAsync("episode", "episode-soon", shows, "tv", "upgrade", now.AddHours(2), now.AddHours(-1), "Nothing better found.", CancellationToken.None);
        await jobs.RecordSearchRetryWindowAsync("movie", "film-soon", films, "movies", "missing", now.AddHours(5), now.AddHours(-2), "Nothing found.", CancellationToken.None);

        var all = (await JsonOf(await app.Client.GetAsync("/api/search-retry-windows"))).GetProperty("items").EnumerateArray().ToArray();
        var showsOnly = (await JsonOf(await app.Client.GetAsync($"/api/search-retry-windows?libraryId={shows}"))).GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(["episode-soon", "film-soon", "film-late"], all.Select(item => item.GetProperty("entityId").GetString()));
        Assert.Equal("Nothing better found.", all[0].GetProperty("lastResult").GetString());
        Assert.Equal("upgrade", all[0].GetProperty("actionKind").GetString());
        Assert.Equal(["episode-soon"], showsOnly.Select(item => item.GetProperty("entityId").GetString()));
        Assert.Empty((await JsonOf(await app.Client.GetAsync("/api/search-retry-windows?libraryId=no-such-library"))).GetProperty("items").EnumerateArray());
    }

    // ---- /api/download-health/content-blocklist ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_content_blocklist_is_off_on_a_new_install_is_saved_and_read_back_refuses_an_unknown_mode_and_needs_a_sign_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var fresh = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist");
        Assert.False(fresh.GetProperty("enabled").GetBoolean(), "nothing is on by default");

        var saved = await app.Client.PutAsJsonAsync("/api/download-health/content-blocklist", new
        {
            enabled = true, mode = "blacklist", useBuiltInPatterns = false, customPatterns = new[] { "*.scr", "*.lnk" }, remoteUrl = (string?)null, remotePatterns = Array.Empty<string>(),
            lastRefreshedUtc = (string?)null, updatedUtc = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var back = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist");
        Assert.True(back.GetProperty("enabled").GetBoolean());
        Assert.Equal(["*.scr", "*.lnk"], back.GetProperty("customPatterns").EnumerateArray().Select(item => item.GetString()));

        var unknown = await app.Client.PutAsJsonAsync("/api/download-health/content-blocklist", new
        {
            enabled = true, mode = "sometimes", useBuiltInPatterns = false, customPatterns = Array.Empty<string>(), remoteUrl = (string?)null, remotePatterns = Array.Empty<string>(),
            lastRefreshedUtc = (string?)null, updatedUtc = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Choose blacklist or whitelist.", await unknown.Content.ReadAsStringAsync());
        Assert.Equal("blacklist", (await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist")).GetProperty("mode").GetString());

        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/download-health/content-blocklist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/download-health/content-blocklist/qbittorrent-sync-status")).StatusCode);
    }

    [Fact]
    public async Task With_no_qbittorrent_pushed_to_yet_the_sync_status_is_empty()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/content-blocklist/qbittorrent-sync-status");

        // One entry per qBittorrent client Deluno has tried to push to, keyed by the client.
        Assert.Equal(JsonValueKind.Object, status.ValueKind);
        Assert.Empty(status.EnumerateObject());
    }

    // ---- /api/retention/collections and /api/retention/seerr-cleanups -----------------------------------------------------------

    [Fact]
    public async Task A_new_install_has_no_leaving_soon_collections_and_nothing_Seerr_has_not_been_cleared_of()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var collections = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/collections");
        var cleanups = await app.Client.GetFromJsonAsync<JsonElement>("/api/retention/seerr-cleanups");

        Assert.Equal(0, collections.GetArrayLength());
        Assert.Equal(0, cleanups.GetArrayLength());
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/retention/collections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/retention/seerr-cleanups")).StatusCode);
    }

    // ---- the Blocklist tab follows the server ------------------------------------------------------------------------------------

    [Fact(Skip = "DEFECT (coverage register): blocking or lifting a release over /api/blocked-releases pushes nothing live. "
        + "ConfigChangedPush.Areas has no 'blocked-releases' prefix and BlockedReleasesEndpointRouteBuilderExtensions never publishes, so the Blocklist tab (search-cycles-page.tsx, which listens to LibraryChanged, SettingsChanged, QualityProfileChanged and the download-health and quality-model areas only) "
        + "does not change when a release is blocked or lifted in another tab, from the queue or from the search screen.")]
    public async Task Blocking_a_release_tells_every_open_screen_so_the_Blocklist_tab_changes_by_itself()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));

        var response = await app.Client.PostAsJsonAsync("/api/blocked-releases", new { releaseName = "Arrival.2016.2160p.UHD.BluRay", indexerName = "Nebula", mediaType = "movie", title = "Arrival" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(publisher.DashboardEvents.Count > 0 || publisher.Published.Count > 0, "nothing was pushed to the screens when the release was blocked");
    }
}
