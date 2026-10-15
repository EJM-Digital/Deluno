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

/// <summary>
/// Aggregated query telemetry for one indexer in a requested window. Only calls that were sent are queries (#1330);
/// <c>HeldBackQueries</c> counts the calls Deluno itself declined to send (a query limit, the request interval, an open circuit).
/// </summary>
public sealed record IndexerQueryStatsItem(
    string IndexerId,
    string IndexerName,
    long TotalQueries,
    long SearchQueries,
    long RssQueries,
    long AuthQueries,
    long FailedQueries,
    double AverageResponseMilliseconds,
    long CandidatesReturned,
    DateTimeOffset? LastQueryUtc = null,
    long HeldBackQueries = 0);

/// <summary>
/// Dispatch/grab telemetry for one indexer. Grabs are filed under the indexer's
/// id (#1077), so a renamed indexer keeps its history; a dispatch whose source
/// matched no indexer has no id and is grouped by the name it carries.
/// </summary>
public sealed record IndexerGrabStatsItem(
    string IndexerName,
    long TotalGrabs,
    long SuccessfulGrabs,
    string? IndexerId = null);

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
