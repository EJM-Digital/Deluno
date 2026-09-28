using Deluno.Contracts;

namespace Deluno.Api.IndexerHistory;

/// <summary>
/// The concrete writer behind <see cref="IIndexerHistoryRecorder"/> (#978):
/// a search or grab made from the title-less Search page writes to the same
/// combined Indexer History an RSS sync or auth failure already does,
/// through the same <see cref="IIndexerHistoryEventStore"/>.
/// </summary>
public sealed class IndexerHistoryRecorder(IIndexerHistoryEventStore eventStore) : IIndexerHistoryRecorder
{
    public Task RecordSearchAsync(string? indexerId, string indexerName, string outcome, string message, string? detailsJson, CancellationToken cancellationToken)
        => eventStore.RecordAsync(
            new RecordIndexerHistoryEventRequest(
                indexerId,
                indexerName,
                IndexerHistoryEventTypes.Search,
                outcome,
                message,
                detailsJson),
            cancellationToken);

    public Task RecordGrabAsync(string? indexerId, string indexerName, string outcome, string message, string? detailsJson, CancellationToken cancellationToken)
        => eventStore.RecordAsync(
            new RecordIndexerHistoryEventRequest(
                indexerId,
                indexerName,
                IndexerHistoryEventTypes.Grab,
                outcome,
                message,
                detailsJson),
            cancellationToken);
}
