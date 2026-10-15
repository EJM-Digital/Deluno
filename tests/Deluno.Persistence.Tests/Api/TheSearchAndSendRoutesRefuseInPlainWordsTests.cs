using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Search now", the targeted episode and season searches and the send of a chosen release, on the cases where nothing can be searched or sent (coverage
/// register, area engines): each answer says in plain words what is missing and what to do, the counts stay zero, and nothing reaches a download client.
/// </summary>
public sealed class TheSearchAndSendRoutesRefuseInPlainWordsTests
{
    private sealed class CountingGrabs : IDownloadClientGrabService
    {
        public int Count { get; private set; }

        public Task<DownloadClientGrabResult> GrabAsync(string clientId, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new DownloadClientGrabResult(clientId, request.ReleaseName, true, "sent", "Sent to the client.") { ExternalId = $"hash-{Count}" });
        }
    }

    private static Task<ApplicationTestHost> StartAsync(CountingGrabs? grabs = null)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            if (grabs is not null)
            {
                services.RemoveAll<IDownloadClientGrabService>();
                services.AddSingleton<IDownloadClientGrabService>(grabs);
            }
        });

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == expected || (expected == HttpStatusCode.OK && response.IsSuccessStatusCode), $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> AddFilmAsync(ApplicationTestHost app, string libraryId)
        => (await PostAsync(app, "/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId })).GetProperty("id").GetString()!;

    private static async Task<(string SeriesId, string EpisodeId)> AddShowAsync(ApplicationTestHost app, string libraryId)
    {
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await series.ImportExistingAsync(libraryId, "Breaking Bad", 2008, "missing", "Wanted.", null, "WEB 1080p", false, null, null, [new ImportedEpisodeItem(1, 1, HasFile: false)], CancellationToken.None);
        var seriesId = (await series.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        return (seriesId, (await series.GetInventoryDetailAsync(seriesId, CancellationToken.None))!.Episodes.Single().EpisodeId);
    }

    private static async Task<string> RoutedLibraryAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, mediaType, name, name);
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest($"Client for {name}", "qbittorrent", "localhost", 1234, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Verified.", null, 1, CancellationToken.None);
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new { sources = Array.Empty<object>(), downloadClients = new[] { new { downloadClientId = client.Id, priority = 1, category = (string?)null } } });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        return libraryId;
    }

    [Fact]
    public async Task Search_now_for_a_title_that_does_not_exist_is_a_404_for_a_film_and_for_a_show()
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/movies/no-such-film/search", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/series/no-such-show/search", content: null)).StatusCode);
    }

    [Fact]
    public async Task A_title_no_library_looks_after_answers_blocked_not_searchable_and_one_whose_library_row_is_gone_answers_blocked_library_missing()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var orphan = await AddFilmAsync(app, libraryId);
        var missing = await PostAsync(app, "/api/movies", new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId });
        var missingId = missing.GetProperty("id").GetString()!;
        await ApiScenes.ExecuteAsync(app, "movies", $"DELETE FROM movie_wanted_state WHERE movie_id = '{orphan}';");
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_wanted_state SET library_id = 'a-library-that-is-gone' WHERE movie_id = '{missingId}';");

        var notSearchable = await PostAsync(app, $"/api/movies/{orphan}/search");
        var libraryMissing = await PostAsync(app, $"/api/movies/{missingId}/search");

        Assert.Equal("blocked", notSearchable.GetProperty("outcome").GetString());
        Assert.Equal("not_searchable", notSearchable.GetProperty("reason").GetString());
        Assert.Equal("This movie is not currently linked to a searchable library.", notSearchable.GetProperty("summary").GetString());
        Assert.Equal("blocked", libraryMissing.GetProperty("outcome").GetString());
        Assert.Equal("library_missing", libraryMissing.GetProperty("reason").GetString());
        Assert.Equal("Deluno could not find the linked library for this movie.", libraryMissing.GetProperty("summary").GetString());
        Assert.Equal(0, libraryMissing.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task A_show_no_library_looks_after_or_whose_library_row_is_gone_answers_blocked_with_every_count_zero_on_the_episode_search()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var (seriesId, episodeId) = await AddShowAsync(app, libraryId);
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_wanted_state SET library_id = 'a-library-that-is-gone' WHERE series_id = '{seriesId}';");

        var gone = await PostAsync(app, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });
        await ApiScenes.ExecuteAsync(app, "series", $"DELETE FROM series_wanted_state WHERE series_id = '{seriesId}';");
        var unlinked = await PostAsync(app, $"/api/series/{seriesId}/episodes/search", new { episodeIds = new[] { episodeId }, monitoredOnly = true });

        Assert.Equal(("blocked", "library_missing"), (gone.GetProperty("outcome").GetString(), gone.GetProperty("reason").GetString()));
        Assert.Equal(("blocked", "not_searchable"), (unlinked.GetProperty("outcome").GetString(), unlinked.GetProperty("reason").GetString()));
        foreach (var answer in new[] { gone, unlinked })
        {
            Assert.Equal(1, answer.GetProperty("searchedEpisodes").GetInt32());
            foreach (var count in new[] { "matchedCount", "queuedCount", "sentCount", "plannedCount", "failedCount" })
            {
                Assert.Equal(0, answer.GetProperty(count).GetInt32());
            }
        }
    }

    [Fact]
    public async Task A_targeted_episode_search_is_refused_with_the_matching_message_for_no_ids_ids_that_match_nothing_or_nothing_monitored_and_404_for_an_unknown_show()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var (seriesId, episodeId) = await AddShowAsync(app, libraryId);

        async Task<string> MessageAsync(string id, object body)
        {
            var response = await app.Client.PostAsJsonAsync($"/api/series/{id}/episodes/search", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("episodeIds")[0].GetString()!;
        }

        Assert.Equal("Choose at least one episode before starting a targeted search.", await MessageAsync(seriesId, new { episodeIds = Array.Empty<string>() }));
        Assert.Equal("Deluno could not find those episodes in the tracked inventory.", await MessageAsync(seriesId, new { episodeIds = new[] { "no-such-episode" }, monitoredOnly = false }));
        await app.Client.PutAsJsonAsync("/api/series/episodes/monitoring", new { episodeIds = new[] { episodeId }, monitored = false });
        Assert.Equal("No monitored episodes were found in the provided list.", await MessageAsync(seriesId, new { episodeIds = new[] { episodeId }, monitoredOnly = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/series/no-such-show/episodes/search", new { episodeIds = new[] { episodeId } })).StatusCode);
    }

    [Fact]
    public async Task A_season_search_for_a_show_that_does_not_exist_is_404_and_for_a_season_that_is_not_tracked_is_a_400_saying_so()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var (seriesId, _) = await AddShowAsync(app, libraryId);

        var unknownShow = await app.Client.PostAsync("/api/series/no-such-show/seasons/1/search", content: null);
        var untracked = await app.Client.PostAsync($"/api/series/{seriesId}/seasons/9/search", content: null);

        Assert.Equal(HttpStatusCode.NotFound, unknownShow.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, untracked.StatusCode);
        Assert.Equal("Deluno could not find that season in the tracked inventory.", (await untracked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("seasonNumber")[0].GetString());
    }

    [Fact]
    public async Task A_season_in_a_library_with_no_indexers_or_no_client_records_blocked_for_every_episode_and_answers_blocked()
    {
        await using var app = await StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var (seriesId, episodeId) = await AddShowAsync(app, libraryId);

        var answer = await PostAsync(app, $"/api/series/{seriesId}/seasons/1/search");

        Assert.Equal("blocked", answer.GetProperty("outcome").GetString());
        Assert.Equal("no_indexers", answer.GetProperty("reason").GetString());
        Assert.Equal(1, answer.GetProperty("searchedEpisodes").GetInt32());
        Assert.Equal(0, answer.GetProperty("matchedCount").GetInt32());
        var history = await app.Client.GetStringAsync("/api/series/search-history");
        Assert.Contains(episodeId, history);
        Assert.Contains("blocked", history);
    }

    [Fact]
    public async Task Sending_a_release_for_a_title_that_does_not_exist_is_404_and_a_title_in_two_libraries_must_be_told_which_library_naming_them_and_a_library_it_is_not_in_is_refused()
    {
        var grabs = new CountingGrabs();
        await using var app = await StartAsync(grabs);
        var first = await RoutedLibraryAsync(app, "movies", "A Films HD");
        var second = await RoutedLibraryAsync(app, "movies", "B Films 4K");
        var other = await RoutedLibraryAsync(app, "movies", "C Films Archive");
        var filmId = await AddFilmAsync(app, first);
        (await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/libraries/", new { libraryId = second })).EnsureSuccessStatusCode();
        var release = new { releaseName = "Sintel (2010) 720p BRRip x264 -YTS", downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596", candidateQuality = "Bluray 720p", sizeBytes = 143151596L, force = true, overrideReason = "You chose it." };

        var unknown = await app.Client.PostAsJsonAsync("/api/movies/no-such-film/grab", release);
        var ambiguous = await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", release);
        var notIn = await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab?libraryId={other}", release);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        var named = (await ambiguous.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("libraryId")[0].GetString()!;
        Assert.Equal("This movie is in 2 libraries (A Films HD, B Films 4K). Choose which library to use.", named);
        Assert.Equal(HttpStatusCode.BadRequest, notIn.StatusCode);
        Assert.Equal("This movie is not in that library.", (await notIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("libraryId")[0].GetString());
        Assert.Equal(0, grabs.Count);
    }

    [Fact]
    public async Task A_release_Deluno_would_not_choose_by_itself_is_refused_on_force_with_the_reason_and_how_to_override_and_goes_ahead_once_force_is_set()
    {
        var grabs = new CountingGrabs();
        await using var app = await StartAsync(grabs);
        var library = await RoutedLibraryAsync(app, "movies", "Films");
        var filmId = await AddFilmAsync(app, library);
        object Release(bool force) => new
        {
            releaseName = "Sintel (2010) 720p BRRip x264 -YTS", downloadUrl = "magnet:?xt=urn:btih:A779FA758F2229586EEBC2E5960EB32FE5659596",
            candidateQuality = "Bluray 720p", sizeBytes = 143151596L, force, overrideReason = force ? "You chose this release." : null
        };

        var refused = await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", Release(force: false));
        var forced = await app.Client.PostAsJsonAsync($"/api/movies/{filmId}/grab", Release(force: true));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var reason = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("force")[0].GetString()!;
        Assert.EndsWith("Use force override if you still want this exact release.", reason);
        Assert.True(forced.IsSuccessStatusCode, await forced.Content.ReadAsStringAsync());
        Assert.Equal(1, grabs.Count);
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", (await forced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseName").GetString());
    }
}
