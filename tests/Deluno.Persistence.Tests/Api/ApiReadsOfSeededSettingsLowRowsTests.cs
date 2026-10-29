using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Deluno.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: more read routes of the API asked after the data they list was really stored: path mappings, download speed
/// samples, the qBittorrent blocklist push, a library's routing, the dashboard day counts, the guide's versions and
/// compiled profiles, and the titles a retention rule marked.
/// </summary>
public sealed class ApiReadsOfSeededSettingsLowRowsTests
{
    private static async Task<JsonElement> GetAsync(ApplicationTestHost app, string path)
        => await app.Client.GetFromJsonAsync<JsonElement>(path);

    private static async Task<string> ClientAsync(ApplicationTestHost app, string name, int port, string host = "localhost")
    {
        var created = await app.Client.PostAsJsonAsync("/api/download-clients", new { name, protocol = "qbittorrent", host, port, isEnabled = true });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Path_mappings_list_across_every_client_and_for_one_client_with_what_was_saved_and_a_client_on_another_host_does_not_see_them()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await ClientAsync(app, "First", 8080);
        var second = await ClientAsync(app, "Second", 8081, "nas.local");
        Assert.Empty((await GetAsync(app, "/api/download-clients/path-mappings")).EnumerateArray());
        (await app.Client.PostAsJsonAsync($"/api/download-clients/{first}/path-mappings", new { remotePath = "/downloads/", localPath = @"D:\Downloads\", isEnabled = true, priority = 1 })).EnsureSuccessStatusCode();
        (await app.Client.PostAsJsonAsync($"/api/download-clients/{second}/path-mappings", new { remotePath = "/data/", localPath = @"E:\Data\", isEnabled = true, priority = 2 })).EnsureSuccessStatusCode();

        var all = (await GetAsync(app, "/api/download-clients/path-mappings")).EnumerateArray().ToList();
        var onlyFirst = (await GetAsync(app, $"/api/download-clients/{first}/path-mappings")).EnumerateArray().ToList();

        Assert.Equal(["/data/", "/downloads/"], all.Select(item => item.GetProperty("remotePath").GetString()).Order());
        var mapping = Assert.Single(onlyFirst);
        Assert.Equal("/downloads/", mapping.GetProperty("remotePath").GetString());
        Assert.Equal(@"D:\Downloads\", mapping.GetProperty("localPath").GetString());
    }

    [Fact]
    public async Task Throughput_lists_the_samples_inside_the_window_oldest_first_and_a_shorter_window_leaves_the_older_ones_out()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var samples = app.Services.GetRequiredService<IDownloadThroughputRepository>();
        var now = DateTimeOffset.UtcNow;
        await samples.RecordSampleAsync(new DownloadThroughputSample(now.AddHours(-3), 12.5, 2, 1.5), CancellationToken.None);
        await samples.RecordSampleAsync(new DownloadThroughputSample(now.AddMinutes(-10), 40, 3, 4), CancellationToken.None);

        var defaultWindow = await GetAsync(app, "/api/download-clients/throughput");
        var oneHour = await GetAsync(app, "/api/download-clients/throughput?hours=1");
        var clamped = await GetAsync(app, "/api/download-clients/throughput?hours=500");

        Assert.Equal(6, defaultWindow.GetProperty("hours").GetInt32());
        Assert.Equal([12.5, 40], defaultWindow.GetProperty("samples").EnumerateArray().Select(item => item.GetProperty("speedMbps").GetDouble()));
        Assert.Equal(1, oneHour.GetProperty("hours").GetInt32());
        var recent = Assert.Single(oneHour.GetProperty("samples").EnumerateArray());
        Assert.Equal(40, recent.GetProperty("speedMbps").GetDouble());
        Assert.Equal(3, recent.GetProperty("activeCount").GetInt32());
        Assert.Equal(48, clamped.GetProperty("hours").GetInt32());
    }

    [Fact]
    public async Task The_blocklist_sync_status_lists_each_clients_last_push_with_whether_it_worked_and_why_not()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.Empty((await GetAsync(app, "/api/download-health/content-blocklist/qbittorrent-sync-status")).EnumerateObject());
        var health = app.Services.GetRequiredService<IDownloadHealthRepository>();
        await health.RecordQbittorrentBlocklistSyncAsync("client-ok", "hash-1", true, null, CancellationToken.None);
        await health.RecordQbittorrentBlocklistSyncAsync("client-bad", "hash-1", false, "qBittorrent refused the login.", CancellationToken.None);

        var status = await GetAsync(app, "/api/download-health/content-blocklist/qbittorrent-sync-status");

        Assert.True(status.GetProperty("client-ok").GetProperty("succeeded").GetBoolean());
        var bad = status.GetProperty("client-bad");
        Assert.False(bad.GetProperty("succeeded").GetBoolean());
        Assert.Equal("qBittorrent refused the login.", bad.GetProperty("message").GetString());
        Assert.Equal("hash-1", bad.GetProperty("blocklistHash").GetString());
    }

    [Fact]
    public async Task The_metadata_status_names_the_provider_and_whether_title_matching_is_ready_with_each_source()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await GetAsync(app, "/api/metadata/status");

        Assert.Equal("deluno", status.GetProperty("provider").GetString());
        Assert.True(status.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("broker", status.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("message").GetString()));
        var broker = Assert.Single(status.GetProperty("sources").EnumerateArray(), item => item.GetProperty("source").GetString() == "broker");
        Assert.True(broker.GetProperty("isConfigured").GetBoolean());
    }

    [Fact]
    public async Task A_librarys_routing_names_the_library_and_lists_its_sources_and_clients_and_an_unknown_library_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", name: "Films");

        var routing = await GetAsync(app, $"/api/libraries/{libraryId}/routing");

        Assert.Equal(libraryId, routing.GetProperty("libraryId").GetString());
        Assert.Equal("Films", routing.GetProperty("libraryName").GetString());
        Assert.Equal(JsonValueKind.Array, routing.GetProperty("sources").ValueKind);
        Assert.Equal(JsonValueKind.Array, routing.GetProperty("downloadClients").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/libraries/no-such-library/routing")).StatusCode);
    }

    [Fact]
    public async Task The_dashboard_day_counts_cover_the_asked_window_and_count_a_title_added_today()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.FilmOnDiskAsync(app);

        var week = await GetAsync(app, "/api/dashboard/metrics?days=7");
        var clamped = await GetAsync(app, "/api/dashboard/metrics?days=0");

        Assert.Equal(7, week.GetProperty("days").GetInt32());
        Assert.Equal(7, week.GetProperty("librarySize").GetArrayLength());
        Assert.Equal(7, week.GetProperty("titlesAdded").GetArrayLength());
        Assert.Equal(1, week.GetProperty("librarySize").EnumerateArray().Last().GetProperty("value").GetInt32());
        Assert.Equal(1, week.GetProperty("titlesAdded").EnumerateArray().Last().GetProperty("value").GetInt32());
        Assert.Equal(0, week.GetProperty("librarySize").EnumerateArray().First().GetProperty("value").GetInt32());
        Assert.Equal(1, clamped.GetProperty("days").GetInt32());
        Assert.Equal(1, clamped.GetProperty("librarySize").GetArrayLength());
    }

    [Fact]
    public async Task A_guide_package_version_is_read_by_its_number_and_an_unknown_number_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var shipped = GuidePackageCatalog.Current.Version;
        var applied = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/apply", new { package = GuidePackageCatalog.Current with { Version = shipped + 1 }, expectedCurrentIntegritySha256 = GuidePackageCatalog.Current.IntegritySha256 });
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());

        var read = await app.Client.GetAsync($"/api/v1/guides/trash/versions/{shipped + 1}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(shipped + 1, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("package").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/guides/trash/versions/987654")).StatusCode);
    }

    [Fact]
    public async Task A_guide_profile_compiles_into_a_plan_and_an_unknown_profile_is_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var profileId = GuidePackageCatalog.Current.QualityProfiles.First().Id;

        var response = await app.Client.GetAsync($"/api/v1/guides/trash/profiles/{profileId}/compile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, plan.ValueKind);
        Assert.Contains(profileId, plan.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/guides/trash/profiles/no-such-profile/compile")).StatusCode);
    }

    [Fact]
    public async Task A_retention_rules_title_search_lists_the_films_it_could_take_and_flags_the_one_already_on_its_list()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);
        var rule = await app.Client.PostAsJsonAsync("/api/retention/rules", new
        {
            name = "Old films", level = "movie", isEnabled = true, steps = new[] { new { type = "condition", op = "keep", condition = "year:max:2100" } },
            mediaServerIds = Array.Empty<string>(), removeFromServers = false, waitDays = 10, removalMode = "automatic", checkIntervalHours = 8
        });
        var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.False(Assert.Single((await GetAsync(app, $"/api/retention/rules/{ruleId}/titles?query=Dune")).EnumerateArray()).GetProperty("alreadyListed").GetBoolean());
        (await app.Client.PostAsJsonAsync($"/api/retention/rules/{ruleId}/titles", new { entityId = film.MovieId })).EnsureSuccessStatusCode();

        var titles = (await GetAsync(app, $"/api/retention/rules/{ruleId}/titles?query=Dune")).EnumerateArray().ToList();

        var mark = Assert.Single(titles);
        Assert.Equal("Dune", mark.GetProperty("title").GetString());
        Assert.Equal(film.MovieId, mark.GetProperty("entityId").GetString());
        Assert.True(mark.GetProperty("alreadyListed").GetBoolean());
        Assert.Empty((await GetAsync(app, $"/api/retention/rules/{ruleId}/titles?query=zzz-no-such-film")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/retention/rules/no-such-rule/titles")).StatusCode);
    }

    private sealed class FixedWatchers : IRealtimeWatchers
    {
        public int ConnectionCount => 4;

        public int Count(string subject) => subject switch
        {
            RealtimeGroups.Dashboard => 2,
            RealtimeGroups.Queue => 1,
            RealtimeGroups.Activity => 3,
            RealtimeGroups.Live => 1,
            "title:dune" => 2,
            _ => 0
        };
    }

    [Fact]
    public async Task The_test_hook_reports_who_is_watching_each_part_of_the_hub_and_one_titles_page_when_asked_and_is_absent_otherwise()
    {
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services =>
            {
                services.RemoveAll<IRealtimeWatchers>();
                services.AddSingleton<IRealtimeWatchers>(new FixedWatchers());
            },
            settings: new Dictionary<string, string?> { ["Testing:EnableRealtimePushEndpoint"] = "true" });

        var all = await GetAsync(app, "/api/testing/realtime/watchers");
        var one = await GetAsync(app, "/api/testing/realtime/watchers?title=dune");

        Assert.Equal((4, 2, 1, 3, 1, 0), (all.GetProperty("connections").GetInt32(), all.GetProperty("dashboard").GetInt32(), all.GetProperty("queue").GetInt32(), all.GetProperty("activity").GetInt32(), all.GetProperty("live").GetInt32(), all.GetProperty("title").GetInt32()));
        Assert.Equal(2, one.GetProperty("title").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync("/api/testing/realtime/watchers")).StatusCode);
    }
}
