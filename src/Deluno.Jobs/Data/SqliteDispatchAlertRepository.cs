using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public sealed class SqliteDispatchAlertRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDispatchAlertRepository
{
    public async Task<DispatchAlert> CreateAlertAsync(
        string dispatchId,
        string title,
        string summary,
        string alertKind,
        string severity,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var alertId = Guid.CreateVersion7().ToString("N");
        var now = timeProvider.GetUtcNow();
        var metadataJson = metadata is not null ? System.Text.Json.JsonSerializer.Serialize(metadata) : null;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dispatch_alerts (
                id, dispatch_id, title, summary, alert_kind, severity, metadata_json,
                detected_utc, acknowledged, acknowledged_utc
            ) VALUES (
                @id, @dispatchId, @title, @summary, @alertKind, @severity, @metadataJson,
                @detectedUtc, 0, NULL
            )
            """;

        AddParameter(command, "@id", alertId);
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@title", title);
        AddParameter(command, "@summary", summary);
        AddParameter(command, "@alertKind", alertKind);
        AddParameter(command, "@severity", severity);
        AddParameter(command, "@metadataJson", metadataJson);
        AddParameter(command, "@detectedUtc", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return new DispatchAlert(
            Id: alertId,
            DispatchId: dispatchId,
            Title: title,
            Summary: summary,
            AlertKind: alertKind,
            Severity: severity,
            Metadata: metadata,
            DetectedUtc: now,
            Acknowledged: false,
            AcknowledgedUtc: null);
    }

    public async Task<IReadOnlyList<DispatchAlert>> GetOpenAlertsAsync(
        string? severity,
        int limit,
        CancellationToken cancellationToken)
    {
        var alerts = new List<DispatchAlert>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        var whereClause = "WHERE acknowledged = 0";
        if (!string.IsNullOrEmpty(severity))
        {
            whereClause += " AND severity = @severity";
        }

        command.CommandText =
            $$"""
            SELECT id, dispatch_id, title, summary, alert_kind, severity, metadata_json,
                   detected_utc, acknowledged, acknowledged_utc
            FROM dispatch_alerts
            {{whereClause}}
            ORDER BY detected_utc DESC
            LIMIT @limit
            """;

        if (!string.IsNullOrEmpty(severity))
        {
            AddParameter(command, "@severity", severity);
        }
        AddParameter(command, "@limit", limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(ReadAlert(reader));
        }

        return alerts;
    }

    public async Task<bool> AcknowledgeAlertAsync(
        string alertId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE dispatch_alerts
            SET acknowledged = 1, acknowledged_utc = @acknowledgedUtc
            WHERE id = @id
            """;

        AddParameter(command, "@id", alertId);
        AddParameter(command, "@acknowledgedUtc", now.ToString("O"));

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM dispatch_alerts WHERE acknowledged = 0
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    private static DispatchAlert ReadAlert(DbDataReader reader)
    {
        var metadataJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        var metadata = metadataJson is not null
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)
            : null;

        return new DispatchAlert(
            Id: reader.GetString(0),
            DispatchId: reader.GetString(1),
            Title: reader.GetString(2),
            Summary: reader.GetString(3),
            AlertKind: reader.GetString(4),
            Severity: reader.GetString(5),
            Metadata: metadata,
            DetectedUtc: DateTimeOffset.Parse(reader.GetString(7)),
            Acknowledged: reader.GetBoolean(8),
            AcknowledgedUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
