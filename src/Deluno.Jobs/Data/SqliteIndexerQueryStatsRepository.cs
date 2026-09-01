using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// SQLite store for indexer query telemetry. A search plan writes one bounded
/// multi-row statement, keeping telemetry useful without turning an outbound
/// fan-out into a matching number of SQLite transactions.
/// </summary>
public sealed class SqliteIndexerQueryStatsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory)
    : IIndexerQueryStatsRepository
{
    private const int MaxBatchSize = 100;

    public async Task RecordBatchAsync(
        IReadOnlyList<IndexerQueryLogEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var batch = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IndexerId))
            .Take(MaxBatchSize)
            .ToArray();
        if (batch.Length == 0)
        {
            return;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        var values = new List<string>(batch.Length);
        for (var index = 0; index < batch.Length; index++)
        {
            var entry = batch[index];
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            values.Add($"(@id{suffix}, @indexerId{suffix}, @indexerName{suffix}, @queryText{suffix}, @categories{suffix}, @mediaType{suffix}, @queryKind{suffix}, @outcome{suffix}, @elapsed{suffix}, @candidateCount{suffix}, @error{suffix}, @failureJson{suffix}, @created{suffix})");

            AddParameter(command, $"@id{suffix}", Guid.CreateVersion7().ToString("N"));
            AddParameter(command, $"@indexerId{suffix}", entry.IndexerId.Trim());
            AddParameter(command, $"@indexerName{suffix}", Trim(entry.IndexerName, 200));
            AddParameter(command, $"@queryText{suffix}", Trim(entry.QueryText, 500));
            AddParameter(command, $"@categories{suffix}", Trim(entry.Categories, 500));
            AddParameter(command, $"@mediaType{suffix}", Trim(entry.MediaType, 32));
            AddParameter(command, $"@queryKind{suffix}", NormalizeQueryKind(entry.QueryKind));
            AddParameter(command, $"@outcome{suffix}", NormalizeOutcome(entry.Outcome));
            AddParameter(command, $"@elapsed{suffix}", Math.Max(0, entry.ElapsedMilliseconds));
            AddParameter(command, $"@candidateCount{suffix}", Math.Max(0, entry.CandidateCount));
            AddParameter(command, $"@error{suffix}", TrimNullable(entry.ErrorMessage, 500));
            AddParameter(command, $"@failureJson{suffix}", entry.Failure is null ? null : JsonSerializer.Serialize(entry.Failure));
            AddParameter(command, $"@created{suffix}", entry.CreatedUtc.ToUniversalTime().ToString("O"));
        }

        command.CommandText = $"""
            INSERT INTO indexer_query_events (
                id, indexer_id, indexer_name, query_text, categories, media_type,
                query_kind, outcome, elapsed_ms, candidate_count, error_message, failure_json, created_utc
            ) VALUES {string.Join(",\n", values)};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexerScoreboardSnapshot> GetScoreboardAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var queryStats = new List<IndexerQueryStatsItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    indexer_id,
                    MAX(indexer_name),
                    COUNT(*),
                    SUM(CASE WHEN query_kind = 'search' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN query_kind = 'rss' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN query_kind = 'auth' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN outcome IN ('failed', 'invalid_url', 'circuit_open') THEN 1 ELSE 0 END),
                    AVG(elapsed_ms),
                    SUM(candidate_count)
                FROM indexer_query_events
                WHERE created_utc >= @fromUtc AND created_utc < @toUtc
                GROUP BY indexer_id
                ORDER BY COUNT(*) DESC, MAX(indexer_name) COLLATE NOCASE;
                """;
            AddParameter(command, "@fromUtc", fromUtc.ToUniversalTime().ToString("O"));
            AddParameter(command, "@toUtc", toUtc.ToUniversalTime().ToString("O"));

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                queryStats.Add(new IndexerQueryStatsItem(
                    IndexerId: reader.GetString(0),
                    IndexerName: reader.GetString(1),
                    TotalQueries: reader.GetInt64(2),
                    SearchQueries: reader.GetInt64(3),
                    RssQueries: reader.GetInt64(4),
                    AuthQueries: reader.GetInt64(5),
                    FailedQueries: reader.GetInt64(6),
                    AverageResponseMilliseconds: reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                    CandidatesReturned: reader.GetInt64(8)));
            }
        }

        var grabStats = new List<IndexerGrabStatsItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    indexer_name,
                    COUNT(*),
                    SUM(CASE WHEN grab_status = 'succeeded' THEN 1 ELSE 0 END)
                FROM download_dispatches
                WHERE created_utc >= @fromUtc
                  AND created_utc < @toUtc
                  AND status != 'archived'
                GROUP BY indexer_name
                ORDER BY COUNT(*) DESC, indexer_name COLLATE NOCASE;
                """;
            AddParameter(command, "@fromUtc", fromUtc.ToUniversalTime().ToString("O"));
            AddParameter(command, "@toUtc", toUtc.ToUniversalTime().ToString("O"));

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                grabStats.Add(new IndexerGrabStatsItem(
                    IndexerName: reader.GetString(0),
                    TotalGrabs: reader.GetInt64(1),
                    SuccessfulGrabs: reader.GetInt64(2)));
            }
        }

        return new IndexerScoreboardSnapshot(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            TotalQueries: queryStats.Sum(item => item.TotalQueries),
            TotalGrabs: grabStats.Sum(item => item.TotalGrabs),
            SuccessfulGrabs: grabStats.Sum(item => item.SuccessfulGrabs),
            QueryStats: queryStats,
            GrabStats: grabStats);
    }

    public async Task<int> PruneAsync(
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM indexer_query_events WHERE created_utc < @beforeUtc;";
        AddParameter(command, "@beforeUtc", beforeUtc.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeQueryKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "rss" => "rss",
            "auth" => "auth",
            _ => "search"
        };

    private static string NormalizeOutcome(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "matched" => "matched",
            "no_results" => "no_results",
            "throttled" => "throttled",
            "circuit_open" => "circuit_open",
            "invalid_url" => "invalid_url",
            _ => "failed"
        };

    private static string Trim(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? TrimNullable(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : Trim(value, maxLength);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
