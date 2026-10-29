using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the read routes of the API, each asked after the data they list was really stored (a library, a connection, a
/// dispatch, an activity line, a strike, a subtitle, a rule, a granted permit), so what comes back is what was seeded and
/// not an empty list.
/// </summary>
public sealed class ApiReadsOfWhatWasSeededLowRowsTests
{
    private static async Task<JsonElement> GetAsync(ApplicationTestHost app, string path)
        => await app.Client.GetFromJsonAsync<JsonElement>(path);

    private static async Task<string> DispatchAsync(ApplicationTestHost app, string libraryId, string mediaType, string release, string clientId = "client-1")
        => await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, mediaType, mediaType == "movies" ? "movie" : "series", "title-1", release, "Fixture", clientId, "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);

    [Fact]
    public async Task External_health_counts_what_is_set_up_and_names_the_instance_and_the_problems_among_the_enabled_connections()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await ApiScenes.CreateLibraryAsync(app, "movies");
        Assert.True((await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Idx", protocol = "newznab", baseUrl = "https://api.example.test", apiKey = "k", mediaScope = "both", isEnabled = true })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/download-clients", new { name = "Mine", protocol = "qbittorrent", host = "localhost", port = 8080, isEnabled = true })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/download-clients", new { name = "Off", protocol = "sabnzbd", host = "localhost", port = 8085, isEnabled = false })).IsSuccessStatusCode);
        var unhealthy = (await GetAsync(app, "/api/indexers")).EnumerateArray().Concat((await GetAsync(app, "/api/download-clients")).EnumerateArray())
            .Count(item => item.GetProperty("isEnabled").GetBoolean() && item.GetProperty("healthStatus").GetString() != "healthy");

        var health = await GetAsync(app, "/api/integrations/external/health");

        Assert.Equal("online", health.GetProperty("status").GetString());
        Assert.Equal(DelunoHostName.Current, health.GetProperty("instanceName").GetString());
        Assert.Equal(1, health.GetProperty("libraryCount").GetInt32());
        Assert.Equal(1, health.GetProperty("enabledIndexerCount").GetInt32());
        Assert.Equal(1, health.GetProperty("enabledDownloadClientCount").GetInt32());
        Assert.Equal(unhealthy, health.GetProperty("problemCount").GetInt32());
        Assert.Equal(0, health.GetProperty("activeJobCount").GetInt32());
    }

    [Fact]
    public async Task External_queue_lists_the_dispatches_sent_and_can_be_cut_to_one_media_type_and_a_count()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");
        await DispatchAsync(app, movies, "movies", "Film.One.2020.1080p-DELUNO");
        await DispatchAsync(app, shows, "tv", "Show.S01E01.1080p-DELUNO");

        var all = await GetAsync(app, "/api/integrations/external/queue");
        var onlyFilms = await GetAsync(app, "/api/integrations/external/queue?mediaType=movies");
        var one = await GetAsync(app, "/api/integrations/external/queue?take=1");

        Assert.Equal(["Film.One.2020.1080p-DELUNO", "Show.S01E01.1080p-DELUNO"], all.GetProperty("dispatches").EnumerateArray().Select(d => d.GetProperty("releaseName").GetString()!).Order());
        Assert.Equal("Film.One.2020.1080p-DELUNO", Assert.Single(onlyFilms.GetProperty("dispatches").EnumerateArray()).GetProperty("releaseName").GetString());
        Assert.Equal(1, one.GetProperty("dispatches").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, all.GetProperty("jobs").ValueKind);
    }

    [Fact]
    public async Task The_legacy_download_dispatches_path_lists_the_dispatch_with_the_client_and_release_it_was_sent_with()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        await DispatchAsync(app, movies, "movies", "Film.One.2020.1080p-DELUNO");

        var page = await GetAsync(app, "/api/download-dispatches");

        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("Film.One.2020.1080p-DELUNO", item.GetProperty("releaseName").GetString());
        Assert.Equal("qBittorrent", item.GetProperty("downloadClientName").GetString());
        Assert.Equal("sent", item.GetProperty("status").GetString());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task External_activity_lists_what_was_recorded_and_take_cuts_it_to_the_newest()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var feed = app.Services.GetRequiredService<IActivityFeedRepository>();
        await feed.RecordActivityAsync("test.entry", "First line", null, null, null, null, CancellationToken.None);
        await Task.Delay(5); // reason: two activity lines need distinct timestamps to have a newest
        await feed.RecordActivityAsync("test.entry", "Second line", null, null, null, null, CancellationToken.None);

        var all = (await GetAsync(app, "/api/integrations/external/activity")).EnumerateArray().Select(item => item.GetProperty("message").GetString()).ToList();
        var one = (await GetAsync(app, "/api/integrations/external/activity?take=1")).EnumerateArray().Select(item => item.GetProperty("message").GetString()).ToList();

        Assert.True(all.IndexOf("Second line") < all.IndexOf("First line"));
        Assert.Equal(["Second line"], one);
    }

    [Fact]
    public async Task The_outbound_throttle_lists_a_host_that_was_paced_with_how_many_requests_it_let_through()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var throttle = app.Services.GetRequiredService<IOutboundRequestThrottle>();
        Assert.NotNull(await throttle.TryAcquireAsync("paced.example.test", OutboundRate.PerIndexerDefault, TimeSpan.FromSeconds(1), CancellationToken.None));

        var hosts = (await GetAsync(app, "/api/integrations/outbound-throttle")).GetProperty("hosts").EnumerateArray().ToList();

        var host = Assert.Single(hosts, item => item.GetProperty("host").GetString() == "paced.example.test");
        Assert.Equal(1, host.GetProperty("grantedCount").GetInt32());
        Assert.Equal(0, host.GetProperty("refusedCount").GetInt32());
        Assert.InRange(host.GetProperty("nextPermitInSeconds").GetDouble(), 0, 2);
    }

    [Fact]
    public async Task The_download_health_list_shows_the_strike_of_a_download_Deluno_sent_and_the_ignore_list_shows_an_entry_added()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var dispatchId = await DispatchAsync(app, libraryId, "movies", "Stuck.Film.2020.1080p-DELUNO");
        await app.Services.GetRequiredService<IDownloadDispatchesRepository>().RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, CancellationToken.None, externalId: "item-1");
        await app.Services.GetRequiredService<IDownloadHealthRepository>().RecordDownloadHealthObservationsAsync(
            "client-1", ["item-1"],
            [new DownloadHealthObservation("client-1", "item-1", "Stuck.Film.2020.1080p-DELUNO", DownloadHealthStrikes.ClientStalled, "warning", "No throughput for 30 minutes.")],
            DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None);
        Assert.Empty((await GetAsync(app, "/api/download-health/ignore-list")).EnumerateArray());
        var added = await app.Client.PostAsJsonAsync("/api/download-health/ignore-list", new { hash = "ABC123", note = "Keep this one" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var page = await GetAsync(app, "/api/download-health");
        var ignored = await GetAsync(app, "/api/download-health/ignore-list");

        var record = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Contains("Stuck.Film.2020.1080p-DELUNO", record.ToString(), StringComparison.Ordinal);
        var entry = Assert.Single(ignored.EnumerateArray());
        Assert.Equal("ABC123", entry.GetProperty("hash").GetString());
        Assert.Equal("Keep this one", entry.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_release_rule_is_read_back_by_its_id_with_what_was_saved_and_an_unknown_id_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/release-profiles", new
        {
            name = "Family", tagNames = new[] { "kids" }, preferredProtocol = "usenet", usenetDelayMinutes = 0, torrentDelayMinutes = 30,
            mustContain = "WEB-DL", mustNotContain = "CAM", preferredTerms = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var rule = await GetAsync(app, $"/api/release-profiles/{id}");

        Assert.Equal("Family", rule.GetProperty("name").GetString());
        Assert.Equal("usenet", rule.GetProperty("preferredProtocol").GetString());
        Assert.Equal(30, rule.GetProperty("torrentDelayMinutes").GetInt32());
        Assert.Equal("WEB-DL", rule.GetProperty("mustContain").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/release-profiles/no-such-rule")).StatusCode);
    }

    [Fact]
    public async Task A_films_subtitles_and_an_episodes_subtitles_list_the_tracks_held_for_that_title_only()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        var filmId = (await ApiScenes.FilmOnDiskAsync(app)).MovieId;
        var episodeId = (await ApiScenes.ShowOnDiskAsync(app)).EpisodeId;
        await subtitles.RecordFetchedAsync(MediaKind.Movie, filmId, new MediaSubtitleRow("en", "sidecar", false, false, @"D:MediaDuneDune.en.srt", null, "srt", "gestdown", 2), CancellationToken.None);
        await subtitles.RecordFetchedAsync(MediaKind.Series, episodeId, new MediaSubtitleRow("fr", "sidecar", true, false, @"D:MediaShowS01E01.fr.srt", null, "srt", "gestdown", 1), CancellationToken.None);

        var film = await GetAsync(app, $"/api/movies/{filmId}/subtitles");
        var episode = await GetAsync(app, $"/api/series/episodes/{episodeId}/subtitles");

        var filmTrack = Assert.Single(film.EnumerateArray());
        Assert.Equal("en", filmTrack.GetProperty("language").GetString());
        Assert.Equal("gestdown", filmTrack.GetProperty("provider").GetString());
        var episodeTrack = Assert.Single(episode.EnumerateArray());
        Assert.Equal("fr", episodeTrack.GetProperty("language").GetString());
        Assert.True(episodeTrack.GetProperty("forced").GetBoolean());
        Assert.Empty((await GetAsync(app, "/api/series/episodes/another-episode/subtitles")).EnumerateArray());
    }

    [Fact]
    public async Task An_episodes_subtitle_history_lists_the_attempts_recorded_for_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var episodeId = (await ApiScenes.ShowOnDiskAsync(app)).EpisodeId;
        await app.Services.GetRequiredService<IMediaSubtitleRepository>().RecordAttemptHistoryAsync(MediaKind.Series, episodeId, "de", "gestdown", "Nothing found", CancellationToken.None);

        var history = await GetAsync(app, $"/api/series/episodes/{episodeId}/subtitles/history");

        var row = Assert.Single(history.EnumerateArray());
        Assert.Equal("de", row.GetProperty("language").GetString());
        Assert.Equal("gestdown", row.GetProperty("provider").GetString());
        Assert.Empty((await GetAsync(app, "/api/series/episodes/other-episode/subtitles/history")).EnumerateArray());
    }
}
