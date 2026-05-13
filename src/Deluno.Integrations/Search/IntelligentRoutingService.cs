using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Integrations.Search;

public sealed class IntelligentRoutingService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IIntelligentRoutingService
{
    private readonly object _cacheLock = new();
    private IntelligentRoutingSnapshot? _cached;
    private DateTimeOffset _cachedUntilUtc;

    public async Task<IntelligentRoutingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_cacheLock)
        {
            if (_cached is not null && _cachedUntilUtc > now)
            {
                return _cached;
            }
        }

        var computed = await ComputeSnapshotAsync(cancellationToken);
        lock (_cacheLock)
        {
            _cached = computed;
            _cachedUntilUtc = now.AddMinutes(10);
        }

        return computed;
    }

    public async Task<IReadOnlyList<IntelligentRoutingAnomaly>> DetectAnomaliesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var alerts = new List<IntelligentRoutingAnomaly>();
        var last24h = await QueryFailureRateAsync(now.AddHours(-24), now, cancellationToken);
        var prior7d = await QueryFailureRateAsync(now.AddDays(-8), now.AddHours(-24), cancellationToken);

        if (last24h.Total >= 10 && prior7d.Total >= 20 && prior7d.FailureRate > 0)
        {
            var multiplier = last24h.FailureRate / prior7d.FailureRate;
            if (multiplier >= 2.0)
            {
                alerts.Add(new IntelligentRoutingAnomaly(
                    Code: "grab-failure-spike",
                    Severity: "warning",
                    Summary: "Grab failure rate spike detected.",
                    Details: $"Last 24h failure rate {last24h.FailureRate.ToString("P1", CultureInfo.InvariantCulture)} vs prior baseline {prior7d.FailureRate.ToString("P1", CultureInfo.InvariantCulture)}.",
                    DetectedUtc: now));
            }
        }

        var downgradeRate = await QueryDowngradeRateAsync(now.AddHours(-24), now, cancellationToken);
        if (downgradeRate.Total >= 8 && downgradeRate.Rate >= 0.15)
        {
            alerts.Add(new IntelligentRoutingAnomaly(
                Code: "quality-downgrade-pattern",
                Severity: "warning",
                Summary: "Unusual downgrade pattern detected.",
                Details: $"{downgradeRate.Downgrades}/{downgradeRate.Total} recent dispatches had negative quality delta.",
                DetectedUtc: now));
        }

        return alerts;
    }

    public async Task<double?> GetDownloadClientSuccessRateAsync(
        string? downloadClientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadClientId))
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.DownloadClientSuccessRates.TryGetValue(downloadClientId, out var rate)
            ? rate
            : null;
    }

    private async Task<IntelligentRoutingSnapshot> ComputeSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var preferences = await QueryPreferencesAsync(connection, cancellationToken);
        var indexers = await QuerySuccessRatesAsync(connection, "indexer_name", cancellationToken);
        var clients = await QuerySuccessRatesAsync(connection, "download_client_id", cancellationToken);

        return new IntelligentRoutingSnapshot(now, preferences, indexers, clients);
    }

    private static async Task<IntelligentRoutingPreferences> QueryPreferencesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var preferredQuality = await QuerySingleTextAsync(
            connection,
            """
            SELECT decision_quality
            FROM download_dispatches
            WHERE (import_status = 'imported' OR import_status = 'completed')
              AND decision_quality IS NOT NULL
            GROUP BY decision_quality
            ORDER BY COUNT(*) DESC
            LIMIT 1;
            """,
            cancellationToken);

        var avgCustomFormat = await QuerySingleDoubleAsync(
            connection,
            """
            SELECT AVG(CAST(decision_custom_format_score AS REAL))
            FROM download_dispatches
            WHERE (import_status = 'imported' OR import_status = 'completed')
              AND decision_custom_format_score IS NOT NULL;
            """,
            cancellationToken) ?? 0;

        var groups = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT decision_release_group
                FROM download_dispatches
                WHERE (import_status = 'imported' OR import_status = 'completed')
                  AND decision_release_group IS NOT NULL
                GROUP BY decision_release_group
                ORDER BY COUNT(*) DESC
                LIMIT 5;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                groups.Add(reader.GetString(0));
            }
        }

        return new IntelligentRoutingPreferences(
            PreferredQuality: preferredQuality,
            AverageCustomFormatScore: Math.Round(avgCustomFormat, 2),
            PreferredReleaseGroups: groups);
    }

    private static async Task<IReadOnlyDictionary<string, double>> QuerySuccessRatesAsync(
        DbConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {columnName},
                COUNT(*) AS total_count,
                SUM(CASE WHEN import_status IN ('imported', 'completed') THEN 1 ELSE 0 END) AS success_count
            FROM download_dispatches
            WHERE {columnName} IS NOT NULL
              AND decision_score IS NOT NULL
            GROUP BY {columnName}
            HAVING total_count >= 3;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var total = reader.GetInt64(1);
            var success = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            rates[key] = total == 0 ? 0 : Math.Round((double)success / total, 4);
        }

        return rates;
    }

    private async Task<(int Total, double FailureRate)> QueryFailureRateAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*),
                SUM(CASE WHEN grab_status = 'failed' THEN 1 ELSE 0 END)
            FROM download_dispatches
            WHERE grab_attempted_utc IS NOT NULL
              AND grab_attempted_utc >= @startUtc
              AND grab_attempted_utc < @endUtc
              AND status != 'archived';
            """;
        AddParameter(command, "@startUtc", startUtc.ToString("O"));
        AddParameter(command, "@endUtc", endUtc.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        var total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
        var failed = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        return (total, total == 0 ? 0 : (double)failed / total);
    }

    private async Task<(int Total, int Downgrades, double Rate)> QueryDowngradeRateAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*),
                SUM(CASE WHEN decision_quality_delta < 0 THEN 1 ELSE 0 END)
            FROM download_dispatches
            WHERE decision_score IS NOT NULL
              AND created_utc >= @startUtc
              AND created_utc < @endUtc
              AND status != 'archived';
            """;
        AddParameter(command, "@startUtc", startUtc.ToString("O"));
        AddParameter(command, "@endUtc", endUtc.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0);
        }

        var total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
        var downgrades = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        return (total, downgrades, total == 0 ? 0 : (double)downgrades / total);
    }

    private static async Task<string?> QuerySingleTextAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    private static async Task<double?> QuerySingleDoubleAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is DBNull or null)
        {
            return null;
        }

        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
