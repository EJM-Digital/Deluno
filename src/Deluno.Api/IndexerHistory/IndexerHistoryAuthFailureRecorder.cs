using Deluno.Contracts;

namespace Deluno.Api.IndexerHistory;

/// <summary>
/// The concrete writer behind <see cref="IIndexerAuthFailureRecorder"/> (#754):
/// an interactive/automatic search or a grab that hits an indexer auth
/// failure writes to the same combined Indexer History an RSS sync already
/// does, through the same <see cref="IIndexerHistoryEventStore"/> -- one sink,
/// not a copy of the recording logic per caller.
/// </summary>
public sealed class IndexerHistoryAuthFailureRecorder(IIndexerHistoryEventStore eventStore) : IIndexerAuthFailureRecorder
{
    public Task RecordAsync(string? indexerId, string indexerName, string action, string message, string? detailsJson, CancellationToken cancellationToken)
        => eventStore.RecordAsync(
            new RecordIndexerHistoryEventRequest(
                indexerId,
                indexerName,
                IndexerHistoryEventTypes.AuthFailure,
                "failed",
                message,
                detailsJson),
            cancellationToken);
}
