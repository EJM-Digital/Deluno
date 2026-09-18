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
        var whereClause = $"WHERE {OpenPredicate}";
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

    public async Task<int> AcknowledgeAlertsForDispatchAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE dispatch_alerts
            SET acknowledged = 1, acknowledged_utc = @acknowledgedUtc
            WHERE dispatch_id = @dispatchId AND {OpenPredicate}
            """;
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@acknowledgedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> AcknowledgeAlertsForReleaseAsync(string downloadClientId, string releaseName, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE dispatch_alerts
            SET acknowledged = 1, acknowledged_utc = @acknowledgedUtc
            WHERE {OpenPredicate}
              AND dispatch_id IN (
                  SELECT id FROM download_dispatches
                  WHERE download_client_id = @clientId AND release_name = @releaseName
              )
            """;
        AddParameter(command, "@clientId", downloadClientId);
        AddParameter(command, "@releaseName", releaseName);
        AddParameter(command, "@acknowledgedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetOpenAlertCountBySeverityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM dispatch_alerts WHERE {OpenPredicate}";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    public async Task<bool> HasUnresolvedAlertAsync(string dispatchId, string alertKind, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT EXISTS (
                SELECT 1 FROM dispatch_alerts
                WHERE dispatch_id = @dispatchId AND alert_kind = @alertKind AND resolved_utc IS NULL
            )
            """;
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@alertKind", alertKind);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long exists && exists == 1;
    }

    /// <summary>
    /// Closes open alerts that no longer describe a live problem.
    ///
    /// <para>The reasons are ordered from the most final to the most specific,
    /// so an alert on a download that was both imported and archived says
    /// "archived" once rather than being judged by the stage it complained
    /// about. Each alert kind is otherwise closed by the step it was waiting
    /// for: a grab timeout by the client picking the download up, a detection
    /// timeout by the import starting, an import timeout by the import no
    /// longer waiting, and an import failure by a retry that got further.</para>
    /// </summary>
    public async Task<int> ResolveSettledAlertsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE dispatch_alerts
            SET resolved_utc = @now,
                resolved_reason = (
                    SELECT CASE
                        WHEN d.id IS NULL THEN 'The download no longer exists.'
                        WHEN d.status = 'archived' THEN 'The download was archived.'
                        WHEN d.import_status = 'imported' THEN 'The download was imported.'
                        WHEN dispatch_alerts.alert_kind = 'grab-timeout' THEN 'The download client picked it up.'
                        WHEN dispatch_alerts.alert_kind = 'detection-timeout' THEN 'The import started.'
                        WHEN dispatch_alerts.alert_kind = 'import-timeout' THEN 'The import stopped waiting.'
                        ELSE 'The import was retried.'
                    END
                    FROM (SELECT 1) AS anchor
                    LEFT JOIN download_dispatches d ON d.id = dispatch_alerts.dispatch_id
                )
            WHERE {OpenPredicate}
              AND NOT EXISTS (
                  SELECT 1 FROM download_dispatches d
                  WHERE d.id = dispatch_alerts.dispatch_id
                    AND COALESCE(d.status, '') <> 'archived'
                    AND COALESCE(d.import_status, '') <> 'imported'
                    AND NOT (dispatch_alerts.alert_kind = 'grab-timeout' AND d.detected_utc IS NOT NULL)
                    AND NOT (dispatch_alerts.alert_kind = 'detection-timeout' AND d.import_status IS NOT NULL)
                    AND NOT (dispatch_alerts.alert_kind = 'import-timeout' AND COALESCE(d.import_status, '') <> 'pending')
                    AND NOT (dispatch_alerts.alert_kind = 'import-failed' AND COALESCE(d.import_status, '') <> 'failed')
              )
            """;
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Open means nobody has acknowledged it and the download has not moved on.
    /// </summary>
    private const string OpenPredicate = "acknowledged = 0 AND resolved_utc IS NULL";

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
