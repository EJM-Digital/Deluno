using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Api.IndexerHistory;

/// <summary>
/// Merges RSS-sync and auth-failure events (<see cref="IIndexerHistoryEventStore"/>)
/// with the existing per-movie and per-series search history (which already
/// covers interactive/automatic searches and grabs, each carrying an indexer
/// name) into one combined, filterable, newest-first feed (#754). Nothing is
/// duplicated into a new table: search and grab rows are read live from the
/// same tables the per-title screens already read, so those screens stay
/// exactly as they are.
/// </summary>
public sealed class IndexerHistoryService(
    IIndexerHistoryEventStore eventStore,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    TimeProvider timeProvider) : IIndexerHistoryService
{
    // Bounded, not a real cross-database join: each source is read for its
    // own most recent window, merged and sorted in memory, then paged. Fine
    // for the volume one Deluno instance produces; a much larger install
    // would want a real event table for search/grab too, which is a
    // deliberately separate, larger change from #754's own scope.
    private const int PerSourceLimit = 500;

    public async Task<Page<IndexerHistoryItem>> ListAsync(
        PageRequest request,
        string? eventType,
        string? indexerName,
        CancellationToken cancellationToken)
    {
        var clearedBeforeUtc = await eventStore.GetClearedBeforeUtcAsync(cancellationToken);

        var events = await eventStore.ListAsync(clearedBeforeUtc, cancellationToken);
        var movieHistory = await movieCatalogRepository.ListRecentSearchHistoryAsync(PerSourceLimit, cancellationToken);
        var seriesHistory = await seriesCatalogRepository.ListRecentSearchHistoryAsync(PerSourceLimit, cancellationToken);

        var merged = events
            .Concat(movieHistory.Select(item => FromMovie(item)))
            .Concat(seriesHistory.Select(item => FromSeries(item)))
            .Where(item => clearedBeforeUtc is null || item.CreatedUtc > clearedBeforeUtc)
            .Where(item => eventType is null || string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(indexerName) || string.Equals(item.IndexerName, indexerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedUtc)
            .ToArray();

        var offset = ParseOffset(request.PageToken);
        var pageSize = request.BoundedPageSize;
        var page = merged.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + page.Length;
        var nextToken = nextOffset < merged.Length ? DelunoPageToken.Encode(nextOffset.ToString()) : null;

        return Page<IndexerHistoryItem>.Of(page, nextToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
        => eventStore.SetClearedBeforeUtcAsync(timeProvider.GetUtcNow(), cancellationToken);

    private static int ParseOffset(string? pageToken)
    {
        var decoded = DelunoPageToken.Decode(pageToken, expectedValues: 1);
        return decoded is [{ } raw] && int.TryParse(raw, out var offset) && offset >= 0 ? offset : 0;
    }

    /// <summary>
    /// A search-history row's <c>TriggerKind</c> is written by whatever asked
    /// for the search ("manual", "manual-grab", "manual-force-grab", an
    /// automatic cycle's own kind, or "rss" when RSS sync queued it): a name
    /// containing "grab" is a grab, everything else is a search that may or
    /// may not have ended in one -- the row's own Outcome still says which.
    /// </summary>
    private static bool IsGrab(string triggerKind)
        => triggerKind.Contains("grab", StringComparison.OrdinalIgnoreCase);

    private static IndexerHistoryItem FromMovie(MovieSearchHistoryItem item)
        => new(
            item.Id,
            null,
            string.IsNullOrWhiteSpace(item.IndexerName) ? "Unknown indexer" : item.IndexerName,
            IsGrab(item.TriggerKind) ? IndexerHistoryEventTypes.Grab : IndexerHistoryEventTypes.Search,
            item.Outcome,
            DescribeSearch(item.TriggerKind, item.Outcome, item.ReleaseName),
            item.DetailsJson,
            "movie",
            item.MovieId,
            item.CreatedUtc);

    private static IndexerHistoryItem FromSeries(SeriesSearchHistoryItem item)
        => new(
            item.Id,
            null,
            string.IsNullOrWhiteSpace(item.IndexerName) ? "Unknown indexer" : item.IndexerName,
            IsGrab(item.TriggerKind) ? IndexerHistoryEventTypes.Grab : IndexerHistoryEventTypes.Search,
            item.Outcome,
            DescribeSearch(item.TriggerKind, item.Outcome, item.ReleaseName),
            item.DetailsJson,
            "series",
            item.SeriesId,
            item.CreatedUtc);

    private static string DescribeSearch(string triggerKind, string outcome, string? releaseName)
        => releaseName is { Length: > 0 }
            ? $"{triggerKind} search: {outcome} ({releaseName})"
            : $"{triggerKind} search: {outcome}";
}
