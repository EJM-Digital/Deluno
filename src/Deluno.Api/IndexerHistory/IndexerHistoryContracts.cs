using Deluno.Contracts;

namespace Deluno.Api.IndexerHistory;

/// <summary>The action kinds the combined Indexer History screen groups by (#754).</summary>
public static class IndexerHistoryEventTypes
{
    public const string RssSync = "rss-sync";
    public const string Search = "search";
    public const string Grab = "grab";
    public const string AuthFailure = "auth-failure";

    public static bool IsKnown(string? value)
        => value is RssSync or Search or Grab or AuthFailure;
}

/// <summary>
/// One row of the combined Indexer History (#754): an RSS sync, a search
/// (interactive or automatic), a grab, or an auth failure, whichever indexer
/// it happened against. <see cref="RelatedEntityType"/>/<see cref="RelatedEntityId"/>
/// point back at the movie or series a search/grab row belongs to, when there
/// is one -- an RSS-sync or auth-failure row has neither.
/// </summary>
public sealed record IndexerHistoryItem(
    string Id,
    string? IndexerId,
    string IndexerName,
    string EventType,
    string Outcome,
    string Message,
    string? DetailsJson,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc);

/// <summary>A newly observed event to add to the combined history (#754), written by whatever noticed it.</summary>
public sealed record RecordIndexerHistoryEventRequest(
    string? IndexerId,
    string IndexerName,
    string EventType,
    string Outcome,
    string Message,
    string? DetailsJson);

/// <summary>Persists and reads the RSS-sync and auth-failure events (#754) no other table already records per-indexer.</summary>
public interface IIndexerHistoryEventStore
{
    Task RecordAsync(RecordIndexerHistoryEventRequest request, CancellationToken cancellationToken);

    /// <summary>Every stored event at or after <paramref name="sinceUtc"/>, newest first.</summary>
    Task<IReadOnlyList<IndexerHistoryItem>> ListAsync(DateTimeOffset? sinceUtc, CancellationToken cancellationToken);

    Task<DateTimeOffset?> GetClearedBeforeUtcAsync(CancellationToken cancellationToken);

    Task SetClearedBeforeUtcAsync(DateTimeOffset clearedBeforeUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Aggregates the combined Indexer History (#754) from the RSS-sync/auth-failure
/// store above and the existing per-movie/per-series search history -- the
/// same events those screens already show, never a second copy of them.
/// </summary>
public interface IIndexerHistoryService
{
    Task<Page<IndexerHistoryItem>> ListAsync(
        PageRequest request,
        string? eventType,
        string? indexerName,
        CancellationToken cancellationToken);

    /// <summary>Hides every event up to now from the combined view (#754) without deleting anyone's underlying data.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
