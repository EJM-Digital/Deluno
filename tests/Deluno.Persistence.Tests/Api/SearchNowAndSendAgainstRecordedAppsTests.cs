using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Search now" and the send of a chosen release, in the cases TitleSearchAndGrabRoutesAgainstRecordedAppsTests does not walk (coverage register,
/// area engines): what the answer carries, what is recorded, and what is refused in plain words. Every case runs against the real replies of
/// Jackett 0.24.2850 and qBittorrent 5.2.1 (tests/fixtures/recorded) served over real loopback ports.
/// </summary>
public sealed class SearchNowAndSendAgainstRecordedAppsTests
{
    private const string FilmPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string ShowPath = "/api/v2.0/indexers/thepiratebay/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private sealed class Rig(RecordedAppServer jackett, RecordedAppServer qbit, ApplicationTestHost app, string clientId, string filmIndexerId, string showIndexerId) : IAsyncDisposable
    {
        public RecordedAppServer Jackett { get; } = jackett;

        public RecordedAppServer Qbit { get; } = qbit;

        public ApplicationTestHost App { get; } = app;

        public string ClientId { get; } = clientId;

        public string FilmIndexerId { get; } = filmIndexerId;

        public string ShowIndexerId { get; } = showIndexerId;

        public int AddsToQbit => Qbit.Requests.Count(request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Qbit.DisposeAsync();
            await Jackett.DisposeAsync();
        }
    }

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Rig> RigAsync(bool refuseAdd = false)
    {
        var jackett = await RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-movie-imdb", "search-tv", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.Chain(
                (request, _) => request.RequestUri is { } uri && uri.Query.Contains("t=search&q=", StringComparison.Ordinal)
                    ? (uri.AbsolutePath.Contains("/yts/", StringComparison.Ordinal) ? RecordedReplies.Jackett("search-text").Exchanges[0] : RecordedReplies.Jackett("search-tv").Exchanges[1]).Reply.ToResponse()
                    : null,
                RecordedAppServer.ByQuery(
                    "t",
                    ("caps", RecordedReplies.Jackett("caps").Exchanges[0]),
                    ("movie", RecordedReplies.Jackett("search-movie-imdb").Exchanges[0]),
                    ("tvsearch", RecordedReplies.Jackett("search-tv").Exchanges[0]),
                    ("search", RecordedReplies.Jackett("search-movie-imdb").Exchanges[1]))));
        var qbit = await RecordedAppServer.StartAsync(
            "qbittorrent",
            new[] { "sign-in", "version", "settings", "resumed", "add-refused" }.Select(RecordedReplies.Qbittorrent),
            RecordedAppServer.Chain(
                // The client turns the add down with the words it really used for a file that is not a torrent.
                refuseAdd ? RecordedAppServer.Answering("POST", "torrents/add", RecordedReplies.Qbittorrent("add-refused").Exchanges[0]) : (_, _) => null,
                RecordedAppServer.InOrder("torrents/info", RecordedReplies.Qbittorrent("downloading").Exchanges[0])));
        var app = await ApplicationTestHost.StartAsync();

        async Task<string> IndexerAsync(string path, string scope, string categories)
        {
            var created = await PostOkAsync(app, "/api/indexers", new
            {
                protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + path, apiKey = Key, priority = 25,
                categories, tags = "", mediaScope = scope, isEnabled = true, preferMagnetUrl = true
            });
            var id = created.GetProperty("id").GetString()!;
            Assert.Equal("healthy", (await PostOkAsync(app, $"/api/indexers/{id}/test")).GetProperty("healthStatus").GetString());
            return id;
        }

        var client = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = client.GetProperty("id").GetString()!;
        Assert.Equal("healthy", (await (await app.Client.PostAsync($"/api/download-clients/{clientId}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        var filmIndexer = await IndexerAsync(FilmPath, "movies", "2000");
        var showIndexer = await IndexerAsync(ShowPath, "tv", "5000");
        return new Rig(jackett, qbit, app, clientId, filmIndexer, showIndexer);
    }

    private static async Task<string> LibraryAsync(Rig rig, string mediaType, string name, string? indexerId, string? clientId)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(rig.App, mediaType, name, name);
        var routed = await rig.App.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = indexerId is null ? Array.Empty<object>() : [new { indexerId, priority = 1, requiredTags = "", excludedTags = "" }],
            downloadClients = clientId is null ? Array.Empty<object>() : [new { downloadClientId = clientId, priority = 1, category = (string?)null }]
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return libraryId;
    }

    private static async Task<string> AddFilmAsync(Rig rig, string libraryId)
        => (await PostOkAsync(rig.App, "/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId })).GetProperty("id").GetString()!;

    private static async Task<(string SeriesId, string EpisodeId)> AddShowAsync(Rig rig, string libraryId)
    {
        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            libraryId, "Breaking Bad", 2008, "missing", "Wanted.", null, "WEB 1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: false)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        var episodeId = (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId;
        return (seriesId, episodeId);
    }

    private static async Task<string> AcceptingFilmProfileAsync(Rig rig)
    {
        // The default film profile refuses every real YTS release (small 720p encodes, under its size floor and below its cutoff),
        // so the film gets a profile made through the real API that accepts them.
        var created = await PostOkAsync(rig.App, "/api/quality-profiles", new
        {
            name = "Small 720p films", mediaType = "movies", cutoffQuality = "Bluray 720p", allowedQualities = "Bluray 720p", customFormatIds = string.Empty,
            upgradeUntilCutoff = false, upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        });
        return created.GetProperty("id").GetString()!;
    }

    private static async Task<string> AddFilmWithProfileAsync(Rig rig, string libraryId)
        => (await PostOkAsync(rig.App, "/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId, searchForMissingNow = false, qualityProfileId = await AcceptingFilmProfileAsync(rig) })).GetProperty("id").GetString()!;

    [Fact]
    public async Task Search_now_on_a_film_sends_the_best_release_to_the_routed_qbittorrent_and_answers_what_was_sent_where_and_how()
    {
        await using var rig = await RigAsync();
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, rig.ClientId);
        var filmId = await AddFilmWithProfileAsync(rig, films);

        var answer = await PostOkAsync(rig.App, $"/api/movies/{filmId}/search");

        Assert.Equal("matched", answer.GetProperty("outcome").GetString());
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", answer.GetProperty("releaseName").GetString());
        Assert.Equal("yts", answer.GetProperty("indexerName").GetString());
        Assert.Equal("sent", answer.GetProperty("dispatchStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(answer.GetProperty("dispatchMessage").GetString()));
        Assert.Contains("qBittorrent", answer.GetProperty("downloadClientName").GetString());
        var added = Assert.Single(rig.Qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.Contains("A779FA758F2229586EEBC2E5960EB32FE5659596", added.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Qbit.Unanswered);
    }

    [Fact]
    public async Task A_sent_Search_now_is_recorded_as_started_by_manual_not_replacement_authorised_for_a_missing_film_and_the_film_is_marked_downloading()
    {
        await using var rig = await RigAsync();
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, rig.ClientId);
        var filmId = await AddFilmWithProfileAsync(rig, films);

        await PostOkAsync(rig.App, $"/api/movies/{filmId}/search");

        Assert.Equal("manual", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{filmId}';"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{filmId}';"));
        Assert.Equal("downloading", await ApiScenes.TextAsync(rig.App, "movies", $"SELECT wanted_status FROM movie_wanted_state WHERE movie_id = '{filmId}';"));
    }

    [Fact]
    public async Task Search_now_on_a_film_that_already_has_a_file_records_the_send_as_replacement_authorised_and_started_by_manual()
    {
        await using var rig = await RigAsync();
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, rig.ClientId);
        var profile = await UpgradingFilmProfileAsync(rig);
        var held = await ApiScenes.FilmOnDiskAsync(rig.App, "Sintel", 2010, films, quality: "DVD");
        (await rig.App.Client.PostAsJsonAsync("/api/movies/bulk/quality-profile", new { movieIds = new[] { held.MovieId }, qualityProfileId = profile })).EnsureSuccessStatusCode();

        var answer = await PostOkAsync(rig.App, $"/api/movies/{held.MovieId}/search");

        Assert.Equal("sent", answer.GetProperty("dispatchStatus").GetString());
        Assert.Equal("manual", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT started_by FROM download_dispatches WHERE entity_id = '{held.MovieId}';"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT replacement_authorized FROM download_dispatches WHERE entity_id = '{held.MovieId}';"));
        Assert.Equal(1, rig.AddsToQbit);
    }

    private static async Task<string> UpgradingFilmProfileAsync(Rig rig)
        => (await PostOkAsync(rig.App, "/api/quality-profiles", new
        {
            name = "Small 720p films, upgrading", mediaType = "movies", cutoffQuality = "Bluray 720p", allowedQualities = "DVD,Bluray 720p", customFormatIds = string.Empty,
            upgradeUntilCutoff = true, upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        })).GetProperty("id").GetString()!;

    [Theory]
    [InlineData(false, "no_results")]
    [InlineData(true, "no_indexers")]
    public async Task A_targeted_search_in_a_library_with_no_indexer_or_no_client_is_blocked_for_every_chosen_episode_with_an_activity_line(bool noIndexer, string reason)
    {
        await using var rig = await RigAsync();
        var shows = await LibraryAsync(rig, "tv", "Shows", noIndexer ? null : rig.ShowIndexerId, noIndexer ? rig.ClientId : null);
        var (seriesId, episodeId) = await AddShowAsync(rig, shows);

        var answer = await PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });

        Assert.Equal("blocked", answer.GetProperty("outcome").GetString());
        Assert.Equal(reason, answer.GetProperty("reason").GetString());
        Assert.Equal(1, answer.GetProperty("searchedEpisodes").GetInt32());
        Assert.Equal(0, answer.GetProperty("matchedCount").GetInt32());
        Assert.Contains("Breaking Bad episode search was blocked because routing is incomplete.", await rig.App.Client.GetStringAsync("/api/activity?take=50"));
        var history = await rig.App.Client.GetStringAsync("/api/series/search-history");
        Assert.Contains(episodeId, history);
        Assert.Contains("blocked", history);
        Assert.Equal(0, rig.AddsToQbit);
    }

    [Fact]
    public async Task A_client_that_refuses_the_send_lists_the_typed_failure_in_plain_words_and_nothing_is_counted_as_sent()
    {
        await using var rig = await RigAsync(refuseAdd: true);
        var shows = await LibraryAsync(rig, "tv", "Shows", rig.ShowIndexerId, rig.ClientId);
        var (seriesId, episodeId) = await AddShowAsync(rig, shows);

        var answer = await PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });

        Assert.Equal(0, answer.GetProperty("sentCount").GetInt32());
        var failure = Assert.Single(answer.GetProperty("failures").EnumerateArray());
        Assert.Equal("download-client", failure.GetProperty("serviceType").GetString());
        Assert.Equal("grab", failure.GetProperty("operation").GetString());
        Assert.Equal("RejectedAction", failure.GetProperty("kind").GetString());
        Assert.Equal("qBittorrent refused the release URL. It may be a dead link or a file it could not read.", failure.GetProperty("message").GetString());
        Assert.NotEqual("sent", await ApiScenes.TextAsync(rig.App, "jobs", $"SELECT status FROM download_dispatches WHERE entity_id = '{episodeId}';"));
        Assert.Equal(1, rig.AddsToQbit);
    }

    [Fact]
    public async Task A_film_qBittorrent_refused_leaves_a_dispatch_whose_facts_journey_and_failure_are_what_the_dispatch_drawer_reads()
    {
        await using var rig = await RigAsync(refuseAdd: true);
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, rig.ClientId);
        var filmId = await AddFilmAsync(rig, films);

        var grab = await rig.App.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", new
        {
            releaseName = "Sintel.2010.720p.BluRay.x264-YTS", indexerName = "Fixture Indexer", downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596",
            candidateQuality = "Bluray 720p", sizeBytes = 143151596L, force = true, overrideReason = "You chose this release."
        });
        Assert.NotEqual(0, (int)grab.StatusCode);

        var list = await rig.App.Client.GetFromJsonAsync<JsonElement>($"/api/download-dispatches?entityId={filmId}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Sintel.2010.720p.BluRay.x264-YTS", item.GetProperty("releaseName").GetString());
        Assert.StartsWith("qBittorrent", item.GetProperty("downloadClientName").GetString(), StringComparison.Ordinal);
        Assert.Equal("deadRelease", item.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("grabStatus").GetString()));
        var failure = item.GetProperty("failure");
        Assert.StartsWith("qBittorrent", failure.GetProperty("serviceName").GetString(), StringComparison.Ordinal);
        Assert.Equal("grab", failure.GetProperty("operation").GetString());
        Assert.Equal("RejectedAction", failure.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("summary").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("nextAction").GetString()));
        Assert.Equal("ManualAction", failure.GetProperty("retryState").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("retryAfterUtc").ValueKind);
        Assert.Empty(rig.Qbit.Unanswered);
    }

    // DEFECT (found by this test, 15 Oct 2026): a client that refuses the send (qBittorrent 415 "not a valid torrent file", recorded as add-refused) gives a
    // grab result that is neither "sent" nor "failed", so the episodes/search route counts it in plannedCount (planned 1, failed 0) instead of failedCount.
    [Fact]
    public async Task A_client_that_refuses_the_send_counts_the_episode_as_failed()
    {
        await using var rig = await RigAsync(refuseAdd: true);
        var shows = await LibraryAsync(rig, "tv", "Shows", rig.ShowIndexerId, rig.ClientId);
        var (seriesId, episodeId) = await AddShowAsync(rig, shows);

        var answer = await PostOkAsync(rig.App, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });

        Assert.Equal(1, answer.GetProperty("failedCount").GetInt32());
        Assert.Equal(0, answer.GetProperty("plannedCount").GetInt32());
    }

    [Fact]
    public async Task A_season_search_answers_the_executor_outcome_with_its_counts_the_release_and_the_dispatch()
    {
        await using var rig = await RigAsync();
        var shows = await LibraryAsync(rig, "tv", "Shows", rig.ShowIndexerId, rig.ClientId);
        var (seriesId, _) = await AddShowAsync(rig, shows);

        var answer = await PostOkAsync(rig.App, $"/api/series/{seriesId}/seasons/1/search");

        foreach (var member in new[]
        {
            "outcome", "reason", "seasonNumber", "searchedEpisodes", "installedEpisodeCount", "missingEvidenceEpisodeCount", "matchedCount", "queuedCount",
            "releaseName", "indexerName", "dispatchStatus", "dispatchMessage", "replacementComparisons", "failures"
        })
        {
            Assert.True(answer.TryGetProperty(member, out _), $"the answer should carry {member}: {answer}");
        }

        Assert.Equal(1, answer.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, answer.GetProperty("searchedEpisodes").GetInt32());
        Assert.Equal(0, answer.GetProperty("installedEpisodeCount").GetInt32());
        Assert.Contains(rig.Jackett.Requests, request => request.PathAndQuery.Contains("t=tvsearch", StringComparison.Ordinal));
        Assert.Empty(rig.Jackett.Unanswered);
    }

    [Fact]
    public async Task Sending_a_release_to_a_library_with_no_download_client_is_a_400_that_says_to_link_one()
    {
        await using var rig = await RigAsync();
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, null);
        var filmId = await AddFilmAsync(rig, films);

        var response = await rig.App.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", new
        {
            releaseName = "Sintel (2010) 720p BRRip x264 -YTS", indexerId = rig.FilmIndexerId, indexerName = "yts",
            downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596", candidateQuality = "Bluray 720p", sizeBytes = 143151596L
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Link a download client to this library before grabbing a release.", errors.GetProperty("downloadClient")[0].GetString());
        Assert.Equal(0, rig.AddsToQbit);
    }

    [Fact]
    public async Task Sending_a_release_no_routed_client_can_take_says_why_on_downloadClient_whether_it_is_switched_off_the_wrong_kind_or_tagged_for_other_titles()
    {
        await using var rig = await RigAsync();
        var films = await LibraryAsync(rig, "movies", "Films", rig.FilmIndexerId, rig.ClientId);
        var filmId = await AddFilmAsync(rig, films);
        var release = new
        {
            releaseName = "Sintel (2010) 720p BRRip x264 -YTS", indexerId = rig.FilmIndexerId, indexerName = "yts",
            downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596", candidateQuality = "Bluray 720p", sizeBytes = 143151596L
        };

        async Task<string> ReasonAsync()
        {
            var response = await rig.App.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", release);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("downloadClient")[0].GetString()!;
        }

        // Every routed client switched off.
        Assert.True((await rig.App.Client.PutAsJsonAsync($"/api/download-clients/{rig.ClientId}", new { isEnabled = false })).IsSuccessStatusCode);
        var off = await ReasonAsync();
        Assert.Contains("is switched off, so nothing can be sent to it.", off);

        // On again but tagged for other titles: this film carries no such tag.
        Assert.True((await rig.App.Client.PutAsJsonAsync($"/api/download-clients/{rig.ClientId}", new { isEnabled = true, tags = "kids" })).IsSuccessStatusCode);
        var tagged = await ReasonAsync();
        Assert.Contains("has no download client that can take a release from yts.", tagged);

        // Only a Usenet client is routed for a torrent release.
        Assert.True((await rig.App.Client.PutAsJsonAsync($"/api/download-clients/{rig.ClientId}", new { tags = "" })).IsSuccessStatusCode);
        var usenet = await PostOkAsync(rig.App, "/api/download-clients", new
        {
            protocol = "sabnzbd", host = "127.0.0.1", port = 8080, username = "", password = "a-test-key", moviesCategory = "movies", tvCategory = "tv", priority = 5, isEnabled = true
        });
        var usenetOnly = await LibraryAsync(rig, "movies", "Usenet only", rig.FilmIndexerId, usenet.GetProperty("id").GetString()!);
        var other = await AddFilmInAnotherLibraryAsync(rig, usenetOnly);
        var wrongKind = await rig.App.Client.PostAsJsonAsync($"/api/movies/{other}/grab", release);
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Contains("has no download client that can take a release from yts.", (await wrongKind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("downloadClient")[0].GetString());
        Assert.Equal(0, rig.AddsToQbit);
    }

    private static async Task<string> AddFilmInAnotherLibraryAsync(Rig rig, string libraryId)
        => (await PostOkAsync(rig.App, "/api/movies", new { title = "Tears of Steel", releaseYear = 2012, imdbId = "tt2285752", monitored = true, libraryId })).GetProperty("id").GetString()!;

    [Fact]
    public async Task A_season_whose_installed_episode_has_no_evaluation_under_the_current_plan_is_held_before_any_indexer_is_asked_and_each_episode_records_the_hold()
    {
        await using var rig = await RigAsync();
        var shows = await LibraryAsync(rig, "tv", "Shows", rig.ShowIndexerId, rig.ClientId);
        var root = await ApiScenes.LibraryRootAsync(rig.App, shows);
        var installed = Path.Combine(root, "Breaking Bad", "Season 01", "Breaking Bad S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        await File.WriteAllBytesAsync(installed, [1, 2, 3, 4]);
        var series = rig.App.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(
            shows, "Breaking Bad", 2008, "missing", "Wanted.", "WEB 720p", "WEB 1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: installed, FileSizeBytes: 4), new ImportedEpisodeItem(1, 2, HasFile: false)],
            CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;

        var askedBefore = rig.Jackett.Requests.Count;

        var answer = await PostOkAsync(rig.App, $"/api/series/{seriesId}/seasons/1/search");

        Assert.Equal("held", answer.GetProperty("outcome").GetString());
        Assert.Equal("season_pack_installed_evidence_missing", answer.GetProperty("reason").GetString());
        Assert.Equal(2, answer.GetProperty("searchedEpisodes").GetInt32());
        Assert.Equal(1, answer.GetProperty("installedEpisodeCount").GetInt32());
        Assert.Equal(1, answer.GetProperty("missingEvidenceEpisodeCount").GetInt32());
        Assert.Contains("does not have an exact evaluation under the current release-preference plan", answer.GetProperty("dispatchMessage").GetString() ?? answer.ToString());
        Assert.Equal(askedBefore, rig.Jackett.Requests.Count);
        Assert.Equal(0, rig.AddsToQbit);
        var history = await rig.App.Client.GetStringAsync("/api/series/search-history");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(history, "\"held\"").Count);
        Assert.Contains("search was held until installed-file evidence is current.", await rig.App.Client.GetStringAsync("/api/activity?take=50"));
    }
}
