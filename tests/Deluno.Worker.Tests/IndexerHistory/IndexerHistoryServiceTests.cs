using Deluno.Api.IndexerHistory;
using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Worker.Tests.IndexerHistory;

/// <summary>
/// #754: the combined Indexer History merges RSS-sync/auth-failure events
/// with the existing per-movie and per-series search history (which already
/// covers interactive/automatic searches and grabs), sorted newest first,
/// filterable by action type and indexer, and clearable without touching the
/// per-title history it reads from.
/// </summary>
public sealed class IndexerHistoryServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    private (IndexerHistoryService Service, FakeEventStore EventStore, Mock<IMovieCatalogRepository> Movies, Mock<ISeriesCatalogRepository> Series) Build()
    {
        var eventStore = new FakeEventStore();
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        movies.Setup(m => m.ListRecentSearchHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MovieSearchHistoryItem>)[]);
        series.Setup(s => s.ListRecentSearchHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SeriesSearchHistoryItem>)[]);

        var service = new IndexerHistoryService(eventStore, movies.Object, series.Object, new FakeClock(Now));
        return (service, eventStore, movies, series);
    }

    [Fact]
    public async Task Merges_rss_sync_events_with_movie_and_series_search_history_newest_first()
    {
        var (service, eventStore, movies, series) = Build();
        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-1)));
        movies.Setup(m => m.ListRecentSearchHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MovieSearchHistoryItem>)[
                new MovieSearchHistoryItem("m1", "movie-1", "lib-1", "manual", "matched", "Movie.2026.1080p", "indexer-a", null, Now.AddMinutes(-3))
            ]);
        series.Setup(s => s.ListRecentSearchHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SeriesSearchHistoryItem>)[
                new SeriesSearchHistoryItem("s1", "series-1", null, 1, 1, "lib-2", "manual-grab", "grabbed", "Show.S01E01", "indexer-b", null, Now.AddMinutes(-2))
            ]);

        var page = await service.ListAsync(new PageRequest(50, null), eventType: null, indexerName: null, CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        // Newest first: rss-sync (-1m), grab (-2m), search (-3m).
        Assert.Equal(IndexerHistoryEventTypes.RssSync, page.Items[0].EventType);
        Assert.Equal(IndexerHistoryEventTypes.Grab, page.Items[1].EventType);
        Assert.Equal(IndexerHistoryEventTypes.Search, page.Items[2].EventType);
        Assert.Equal("indexer-b", page.Items[1].IndexerName);
        Assert.Equal("movie", page.Items[2].RelatedEntityType);
        Assert.Equal("movie-1", page.Items[2].RelatedEntityId);
    }

    [Fact]
    public async Task Filters_by_event_type()
    {
        var (service, eventStore, movies, _) = Build();
        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-1)));
        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.AuthFailure, Now.AddMinutes(-2)));

        var page = await service.ListAsync(new PageRequest(50, null), eventType: IndexerHistoryEventTypes.AuthFailure, indexerName: null, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(IndexerHistoryEventTypes.AuthFailure, item.EventType);
    }

    [Fact]
    public async Task Filters_by_indexer_name()
    {
        var (service, eventStore, _, _) = Build();
        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-1)));
        eventStore.Events.Add(Event("indexer-b", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-2)));

        var page = await service.ListAsync(new PageRequest(50, null), eventType: null, indexerName: "indexer-b", CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("indexer-b", item.IndexerName);
    }

    [Fact]
    public async Task Clearing_hides_earlier_events_but_a_new_one_still_appears()
    {
        var (service, eventStore, _, _) = Build();
        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-5)));

        await service.ClearAsync(CancellationToken.None);
        var afterClear = await service.ListAsync(new PageRequest(50, null), null, null, CancellationToken.None);
        Assert.Empty(afterClear.Items);

        eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(1)));
        var afterNewEvent = await service.ListAsync(new PageRequest(50, null), null, null, CancellationToken.None);
        Assert.Single(afterNewEvent.Items);
    }

    [Fact]
    public async Task Pages_through_results_with_a_token()
    {
        var (service, eventStore, _, _) = Build();
        for (var i = 0; i < 5; i++)
        {
            eventStore.Events.Add(Event("indexer-a", IndexerHistoryEventTypes.RssSync, Now.AddMinutes(-i)));
        }

        var first = await service.ListAsync(new PageRequest(2, null), null, null, CancellationToken.None);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextPageToken);

        var second = await service.ListAsync(new PageRequest(2, first.NextPageToken), null, null, CancellationToken.None);
        Assert.Equal(2, second.Items.Count);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }

    private static IndexerHistoryItem Event(string indexerName, string eventType, DateTimeOffset createdUtc)
        => new(Guid.NewGuid().ToString("n"), null, indexerName, eventType, "read", "message", null, null, null, createdUtc);

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeEventStore : IIndexerHistoryEventStore
    {
        public List<IndexerHistoryItem> Events { get; } = [];
        private DateTimeOffset? clearedBeforeUtc;

        public Task RecordAsync(RecordIndexerHistoryEventRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<IndexerHistoryItem>> ListAsync(DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IndexerHistoryItem>>(
                Events.Where(item => sinceUtc is null || item.CreatedUtc > sinceUtc).OrderByDescending(item => item.CreatedUtc).ToArray());

        public Task<DateTimeOffset?> GetClearedBeforeUtcAsync(CancellationToken cancellationToken)
            => Task.FromResult(clearedBeforeUtc);

        public Task SetClearedBeforeUtcAsync(DateTimeOffset value, CancellationToken cancellationToken)
        {
            clearedBeforeUtc = value;
            return Task.CompletedTask;
        }
    }
}
