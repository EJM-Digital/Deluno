using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.IndexerHistory;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The combined Indexer History over HTTP with the events really stored in the host's SQLite files: an RSS sync and an
/// auth failure from the indexer event store, a search and a grab from the film's own search history, merged newest first,
/// narrowed by action and by indexer, sorted on the server before it is paged, paged with a token that keeps the filters, and
/// cleared without touching the per-title history it reads from.
/// </summary>
public sealed class IndexerHistoryOverHttpOnTheRealStoreTests
{
    private const string Release = "Wanted.Film.2011.1080p.WEB-DL-DELUNO";

    private static async Task<ApplicationTestHost> StartWithFourEventsAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var now = DateTimeOffset.UtcNow;
        await movies.RecordSearchAttemptAsync(film.MovieId, libraryId, "manual", "matched", now.AddMinutes(-20), null, null, Release, "NZBgeek", null, CancellationToken.None);
        await movies.RecordSearchAttemptAsync(film.MovieId, libraryId, "manual-grab", "grabbed", now.AddMinutes(-10), null, null, Release, "NZBgeek", null, CancellationToken.None);
        var events = app.Services.GetRequiredService<IIndexerHistoryEventStore>();
        await events.RecordAsync(new RecordIndexerHistoryEventRequest("idx-eztv", "EZTV", IndexerHistoryEventTypes.RssSync, "read", "Read EZTV's RSS feed. 2 new releases.", null), CancellationToken.None);
        await Wait.ClockMovesOnAsync();
        await events.RecordAsync(new RecordIndexerHistoryEventRequest(null, "1337x", IndexerHistoryEventTypes.AuthFailure, "failed", "1337x answered HTTP 401.", null), CancellationToken.None);
        return app;
    }

    private static async Task<JsonElement> GetAsync(ApplicationTestHost app, string query)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history" + query);

    private static string[] Types(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("eventType").GetString()!).ToArray();

    [Fact]
    public async Task The_four_kinds_of_event_come_back_merged_newest_first_with_their_outcome_and_the_title_they_are_for()
    {
        await using var app = await StartWithFourEventsAsync();

        var page = await GetAsync(app, "");

        Assert.Equal(["auth-failure", "rss-sync", "grab", "search"], Types(page));
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("1337x answered HTTP 401.", items[0].GetProperty("message").GetString());
        Assert.Equal("failed", items[0].GetProperty("outcome").GetString());
        Assert.Equal("Read EZTV's RSS feed. 2 new releases.", items[1].GetProperty("message").GetString());
        Assert.Equal("NZBgeek", items[2].GetProperty("indexerName").GetString());
        Assert.Equal("movie", items[2].GetProperty("relatedEntityType").GetString());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Action_and_indexer_filters_narrow_the_feed_alone_and_together()
    {
        await using var app = await StartWithFourEventsAsync();

        Assert.Equal(["grab"], Types(await GetAsync(app, "?eventType=grab")));
        Assert.Equal(["rss-sync"], Types(await GetAsync(app, "?indexerName=EZTV")));
        Assert.Equal(["grab", "search"], Types(await GetAsync(app, "?indexerName=nzbgeek")));
        Assert.Empty((await GetAsync(app, "?eventType=grab&indexerName=EZTV")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_whole_feed_is_sorted_before_it_is_paged_and_the_next_page_keeps_the_filters_and_the_sort()
    {
        await using var app = await StartWithFourEventsAsync();

        var first = await GetAsync(app, "?pageSize=2&sortBy=indexer&sortDirection=asc");
        Assert.Equal(["auth-failure", "rss-sync"], Types(first));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var token = first.GetProperty("nextPageToken").GetString()!;
        var second = await GetAsync(app, $"?pageSize=2&sortBy=indexer&sortDirection=asc&pageToken={Uri.EscapeDataString(token)}");
        Assert.Equal(["grab", "search"], Types(second).Order().ToArray());
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        var narrowed = await GetAsync(app, "?pageSize=1&indexerName=NZBgeek");
        Assert.Single(Types(narrowed));
        var rest = await GetAsync(app, $"?pageSize=1&indexerName=NZBgeek&pageToken={Uri.EscapeDataString(narrowed.GetProperty("nextPageToken").GetString()!)}");
        Assert.Single(Types(rest));
        Assert.DoesNotContain("rss-sync", Types(narrowed).Concat(Types(rest)));
    }

    [Fact]
    public async Task Clearing_empties_the_combined_feed_but_the_films_own_search_history_stays_and_a_later_event_appears()
    {
        await using var app = await StartWithFourEventsAsync();
        var filmHistoryBefore = await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_search_history;");

        Assert.True((await app.Client.PostAsync("/api/indexer-history/clear", content: null)).IsSuccessStatusCode);

        Assert.Empty(Types(await GetAsync(app, "")));
        Assert.Equal(filmHistoryBefore, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_search_history;"));
        await Wait.ClockMovesOnAsync();
        await app.Services.GetRequiredService<IIndexerHistoryEventStore>().RecordAsync(
            new RecordIndexerHistoryEventRequest("idx-eztv", "EZTV", IndexerHistoryEventTypes.RssSync, "read", "Read EZTV's RSS feed. Nothing new.", null), CancellationToken.None);
        Assert.Equal(["rss-sync"], Types(await GetAsync(app, "")));
    }
}
