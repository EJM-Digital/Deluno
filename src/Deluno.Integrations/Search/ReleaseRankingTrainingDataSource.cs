using System.Data.Common;
using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Integrations.Search;

public sealed record ReleaseRankingTrainingRow(
    int? Seeders,
    long? SizeBytes,
    int? QualityDelta,
    int? CustomFormatScore,
    int? SeederScore,
    int? SizeScore,
    int? DecisionScore,
    string? DecisionStatus,
    string? DecisionQuality,
    string? ReleaseGroup,
    double? EstimatedBitrateMbps,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? GrabAttemptedUtc,
    bool OverrideUsed,
    bool Label);

public interface IReleaseRankingTrainingDataSource
{
    Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(
        int maxRows,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken);
}

public sealed class SqliteReleaseRankingTrainingDataSource(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory)
    : IReleaseRankingTrainingDataSource
{
    public async Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(
        int maxRows,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(maxRows, 100, 50000);
        var rows = new List<ReleaseRankingTrainingRow>(Math.Min(take, 2000));

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                decision_seeders,
                decision_size_bytes,
                decision_quality_delta,
                decision_custom_format_score,
                decision_seeder_score,
                decision_size_score,
                decision_score,
                decision_status,
                decision_quality,
                decision_release_group,
                decision_estimated_bitrate_mbps,
                created_utc,
                grab_attempted_utc,
                decision_override_used,
                grab_status,
                import_status
            FROM download_dispatches
            WHERE decision_score IS NOT NULL
              AND status != 'archived'
              AND (@sinceUtc IS NULL OR created_utc >= @sinceUtc)
            ORDER BY created_utc DESC
            LIMIT @take;
            """;
        AddParameter(command, "@sinceUtc", sinceUtc?.ToString("O"));
        AddParameter(command, "@take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var grabStatus = reader.IsDBNull(14) ? null : reader.GetString(14);
            var importStatus = reader.IsDBNull(15) ? null : reader.GetString(15);

            var label = ResolveLabel(grabStatus, importStatus);
            if (label is null)
            {
                continue;
            }

            rows.Add(new ReleaseRankingTrainingRow(
                Seeders: reader.IsDBNull(0) ? null : reader.GetInt32(0),
                SizeBytes: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                QualityDelta: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                CustomFormatScore: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                SeederScore: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                SizeScore: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                DecisionScore: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                DecisionStatus: reader.IsDBNull(7) ? null : reader.GetString(7),
                DecisionQuality: reader.IsDBNull(8) ? null : reader.GetString(8),
                ReleaseGroup: reader.IsDBNull(9) ? null : reader.GetString(9),
                EstimatedBitrateMbps: reader.IsDBNull(10) ? null : reader.GetDouble(10),
                CreatedUtc: reader.IsDBNull(11)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                GrabAttemptedUtc: reader.IsDBNull(12)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OverrideUsed: !reader.IsDBNull(13) && reader.GetInt64(13) == 1,
                Label: label.Value));
        }

        return rows;
    }

    private static bool? ResolveLabel(string? grabStatus, string? importStatus)
    {
        if (string.Equals(importStatus, "imported", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(importStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(grabStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(importStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
