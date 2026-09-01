using Deluno.Contracts;

namespace Deluno.Jobs.Contracts;

/// <summary>
/// One bounded telemetry event for an outbound indexer request. The query text
/// is the human-readable title/search expression; credentials and the full URL
/// are deliberately never persisted.
/// </summary>
public sealed record IndexerQueryLogEntry(
    string IndexerId,
    string IndexerName,
    string QueryText,
    string Categories,
    string MediaType,
    string QueryKind,
    string Outcome,
    int ElapsedMilliseconds,
    int CandidateCount,
    DateTimeOffset CreatedUtc,
    string? ErrorMessage = null,
    IntegrationFailure? Failure = null);

/// <summary>Aggregated query telemetry for one indexer in a requested window.</summary>
public sealed record IndexerQueryStatsItem(
    string IndexerId,
    string IndexerName,
    long TotalQueries,
    long SearchQueries,
    long RssQueries,
    long AuthQueries,
    long FailedQueries,
    double AverageResponseMilliseconds,
    long CandidatesReturned);

/// <summary>Dispatch/grab telemetry grouped by the indexer name on the dispatch.</summary>
public sealed record IndexerGrabStatsItem(
    string IndexerName,
    long TotalGrabs,
    long SuccessfulGrabs);

/// <summary>The raw aggregates used by the indexer scoreboard endpoint.</summary>
public sealed record IndexerScoreboardSnapshot(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long TotalQueries,
    long TotalGrabs,
    long SuccessfulGrabs,
    IReadOnlyList<IndexerQueryStatsItem> QueryStats,
    IReadOnlyList<IndexerGrabStatsItem> GrabStats);

/// <summary>
/// Storage boundary for indexer telemetry. Writers submit one batch per
/// search plan so a cycle does not become a write-per-request cycle.
/// </summary>
public interface IIndexerQueryStatsRepository
{
    Task RecordBatchAsync(
        IReadOnlyList<IndexerQueryLogEntry> entries,
        CancellationToken cancellationToken);

    Task<IndexerScoreboardSnapshot> GetScoreboardAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<int> PruneAsync(
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken);
}
