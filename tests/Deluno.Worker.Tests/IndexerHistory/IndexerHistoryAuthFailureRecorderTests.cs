using Deluno.Api.IndexerHistory;
using Deluno.Contracts;

namespace Deluno.Worker.Tests.IndexerHistory;

/// <summary>
/// #754: the one writer behind <see cref="IIndexerAuthFailureRecorder"/> --
/// whatever noticed the auth failure (a search, a grab), this is the single
/// place it becomes a combined-history row, through the same
/// <see cref="IIndexerHistoryEventStore"/> an RSS sync's own auth failure uses.
/// </summary>
public sealed class IndexerHistoryAuthFailureRecorderTests
{
    [Fact]
    public async Task Writes_an_auth_failure_event_through_the_shared_store()
    {
        var store = new RecordingEventStore();
        var recorder = new IndexerHistoryAuthFailureRecorder(store);

        await recorder.RecordAsync("indexer-1", "Indexer One", "grab", "Indexer One would not hand over this release: unauthorized", "{\"detail\":true}", CancellationToken.None);

        var request = Assert.Single(store.Recorded);
        Assert.Equal("indexer-1", request.IndexerId);
        Assert.Equal("Indexer One", request.IndexerName);
        Assert.Equal(IndexerHistoryEventTypes.AuthFailure, request.EventType);
        Assert.Equal("failed", request.Outcome);
        Assert.Equal("{\"detail\":true}", request.DetailsJson);
    }

    private sealed class RecordingEventStore : IIndexerHistoryEventStore
    {
        public List<RecordIndexerHistoryEventRequest> Recorded { get; } = [];

        public Task RecordAsync(RecordIndexerHistoryEventRequest request, CancellationToken cancellationToken)
        {
            Recorded.Add(request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IndexerHistoryItem>> ListAsync(DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IndexerHistoryItem>>([]);

        public Task<DateTimeOffset?> GetClearedBeforeUtcAsync(CancellationToken cancellationToken)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task SetClearedBeforeUtcAsync(DateTimeOffset clearedBeforeUtc, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
