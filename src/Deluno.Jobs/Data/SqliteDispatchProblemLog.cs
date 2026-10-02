using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

/// <summary>
/// <see cref="IDispatchProblemLog"/> on each download's own timeline. Three
/// bookkeeping rows per problem kind (<c>problem-raised:</c>,
/// <c>problem-acknowledged:</c>, <c>problem-settled:</c>); the latest of them
/// says where the problem stands. Nothing is written to the old
/// <c>dispatch_alerts</c> table, which is left in place and only read, once per
/// start, so a problem that table still held open on an upgraded install is not
/// reported a second time.
/// </summary>
public sealed class SqliteDispatchProblemLog(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDispatchProblemLog
{
    public const string RaisedPrefix = "problem-raised:";
    public const string AcknowledgedPrefix = "problem-acknowledged:";
    public const string SettledPrefix = "problem-settled:";

    /// <summary>Every bookkeeping row starts with this; the timeline reads leave them out.</summary>
    public const string BookkeepingPrefix = "problem-";

    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private bool _legacyChecked;

    public async Task<bool> IsRecordedAsync(string dispatchId, string kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_type FROM download_dispatch_timeline
            WHERE dispatch_id = @dispatchId AND event_type IN (@raised, @settled)
            ORDER BY timestamp DESC, rowid DESC
            LIMIT 1
            """;
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@raised", RaisedPrefix + kind);
        AddParameter(command, "@settled", SettledPrefix + kind);

        return await command.ExecuteScalarAsync(cancellationToken) is string latest
            && string.Equals(latest, RaisedPrefix + kind, StringComparison.Ordinal);
    }

    public async Task RecordAsync(string dispatchId, string kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await InsertAsync(connection, null, dispatchId, RaisedPrefix + kind, timeProvider.GetUtcNow().ToString("O"), cancellationToken);
    }

    public async Task<int> AcknowledgeForDispatchAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var open = await ReadOpenAsync(connection, "AND t.dispatch_id = @dispatchId", [("@dispatchId", dispatchId)], cancellationToken);
        return await AcknowledgeAsync(connection, open, cancellationToken);
    }

    public async Task<int> AcknowledgeForReleaseAsync(string downloadClientId, string releaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var open = await ReadOpenAsync(
            connection,
            """
            AND t.dispatch_id IN (
                SELECT id FROM download_dispatches
                WHERE download_client_id = @clientId AND release_name = @releaseName
            )
            """,
            [("@clientId", downloadClientId), ("@releaseName", releaseName)],
            cancellationToken);
        return await AcknowledgeAsync(connection, open, cancellationToken);
    }

    /// <summary>
    /// The reasons run from the most final to the most specific, so a problem on
    /// a download that was both imported and archived says "archived" once
    /// rather than being judged by the stage it complained about. Each kind is
    /// otherwise released by the step it was waiting for: a grab timeout by the
    /// client picking the download up, a detection timeout by the import
    /// starting, an import timeout by the import no longer waiting, and an
    /// import failure by a retry that got further.
    /// </summary>
    public async Task<IReadOnlyList<SettledDispatchProblem>> SettleMovedOnAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var open = await ReadOpenAsync(connection, string.Empty, [], cancellationToken);
        var settled = new List<SettledDispatchProblem>();

        foreach (var (dispatchId, kind) in open)
        {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT status, import_status, detected_utc FROM download_dispatches WHERE id = @id";
            AddParameter(read, "@id", dispatchId);

            string? status;
            string? importStatus;
            bool detected;
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    continue;
                }

                status = reader.IsDBNull(0) ? null : reader.GetString(0);
                importStatus = reader.IsDBNull(1) ? null : reader.GetString(1);
                detected = !reader.IsDBNull(2);
            }

            var reason = MovedOnReason(kind, status, importStatus, detected);
            if (reason is null)
            {
                continue;
            }

            await InsertAsync(connection, null, dispatchId, SettledPrefix + kind, timeProvider.GetUtcNow().ToString("O"), cancellationToken);
            settled.Add(new SettledDispatchProblem(dispatchId, kind, reason));
        }

        return settled;
    }

    public async Task<int> CountOpenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (await ReadOpenAsync(connection, string.Empty, [], cancellationToken)).Count;
    }

    /// <summary>Why a problem no longer applies, or null when the download is still stuck on it.</summary>
    internal static string? MovedOnReason(string kind, string? status, string? importStatus, bool detected)
    {
        if (string.Equals(status, "archived", StringComparison.Ordinal))
        {
            return "The download was archived.";
        }

        if (string.Equals(importStatus, "imported", StringComparison.Ordinal))
        {
            return "The download was imported.";
        }

        return kind switch
        {
            "grab-timeout" when detected => "The download client picked it up.",
            "detection-timeout" when importStatus is not null => "The import started.",
            "import-timeout" when !string.Equals(importStatus ?? string.Empty, "pending", StringComparison.Ordinal) => "The import stopped waiting.",
            "import-failed" when !string.Equals(importStatus ?? string.Empty, "failed", StringComparison.Ordinal) => "The import was retried.",
            _ => null
        };
    }

    /// <summary>
    /// Problems waiting on the owner: raised, and neither dismissed nor released
    /// since. Order is by time, then by the order rows were written, so a raise
    /// and a release stamped in the same instant still read the right way round.
    /// </summary>
    private static async Task<List<(string DispatchId, string Kind)>> ReadOpenAsync(
        DbConnection connection,
        string extraWhere,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT t.dispatch_id, substr(t.event_type, {RaisedPrefix.Length + 1})
            FROM download_dispatch_timeline t
            WHERE t.event_type LIKE 'problem-raised:%'
              {extraWhere}
              AND NOT EXISTS (
                  SELECT 1 FROM download_dispatch_timeline n
                  WHERE n.dispatch_id = t.dispatch_id
                    AND n.event_type IN (
                        '{AcknowledgedPrefix}' || substr(t.event_type, {RaisedPrefix.Length + 1}),
                        '{SettledPrefix}' || substr(t.event_type, {RaisedPrefix.Length + 1}))
                    AND (n.timestamp > t.timestamp OR (n.timestamp = t.timestamp AND n.rowid > t.rowid)))
            """;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        var open = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            open.Add((reader.GetString(0), reader.GetString(1)));
        }

        return open;
    }

    private async Task<int> AcknowledgeAsync(
        DbConnection connection,
        IReadOnlyList<(string DispatchId, string Kind)> open,
        CancellationToken cancellationToken)
    {
        foreach (var (dispatchId, kind) in open)
        {
            await InsertAsync(connection, null, dispatchId, AcknowledgedPrefix + kind, timeProvider.GetUtcNow().ToString("O"), cancellationToken);
        }

        return open.Count;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        try
        {
            await CarryOverLegacyAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Once per start: a problem the old <c>dispatch_alerts</c> table still held
    /// unresolved is carried onto the timeline, acknowledged or not, so a stuck
    /// download whose issue was dismissed before the upgrade is not reported
    /// again. Reads the old table; never writes to it.
    /// </summary>
    private async Task CarryOverLegacyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_legacyChecked)
        {
            return;
        }

        await _legacyGate.WaitAsync(cancellationToken);
        try
        {
            if (_legacyChecked)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            using (var raised = connection.CreateCommand())
            {
                raised.Transaction = transaction;
                raised.CommandText =
                    $"""
                    INSERT INTO download_dispatch_timeline (id, dispatch_id, event_type, timestamp, details_json, created_utc)
                    SELECT lower(hex(randomblob(16))), a.dispatch_id, '{RaisedPrefix}' || a.alert_kind, MIN(a.detected_utc), NULL, MIN(a.detected_utc)
                    FROM dispatch_alerts a
                    WHERE a.resolved_utc IS NULL
                      AND EXISTS (SELECT 1 FROM download_dispatches d WHERE d.id = a.dispatch_id)
                      AND NOT EXISTS (
                          SELECT 1 FROM download_dispatch_timeline m
                          WHERE m.dispatch_id = a.dispatch_id AND m.event_type = '{RaisedPrefix}' || a.alert_kind)
                    GROUP BY a.dispatch_id, a.alert_kind
                    """;
                await raised.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var acknowledged = connection.CreateCommand())
            {
                acknowledged.Transaction = transaction;
                acknowledged.CommandText =
                    $"""
                    INSERT INTO download_dispatch_timeline (id, dispatch_id, event_type, timestamp, details_json, created_utc)
                    SELECT lower(hex(randomblob(16))), a.dispatch_id, '{AcknowledgedPrefix}' || a.alert_kind,
                           MAX(COALESCE(a.acknowledged_utc, a.detected_utc)), NULL, MAX(COALESCE(a.acknowledged_utc, a.detected_utc))
                    FROM dispatch_alerts a
                    WHERE a.resolved_utc IS NULL
                      AND a.acknowledged = 1
                      AND EXISTS (
                          SELECT 1 FROM download_dispatch_timeline m
                          WHERE m.dispatch_id = a.dispatch_id AND m.event_type = '{RaisedPrefix}' || a.alert_kind)
                      AND NOT EXISTS (
                          SELECT 1 FROM download_dispatch_timeline m
                          WHERE m.dispatch_id = a.dispatch_id AND m.event_type = '{AcknowledgedPrefix}' || a.alert_kind)
                    GROUP BY a.dispatch_id, a.alert_kind
                    """;
                await acknowledged.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _legacyChecked = true;
        }
        finally
        {
            _legacyGate.Release();
        }
    }

    private static async Task InsertAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string dispatchId,
        string eventType,
        string timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO download_dispatch_timeline (id, dispatch_id, event_type, timestamp, details_json, created_utc)
            VALUES (@id, @dispatchId, @eventType, @timestamp, NULL, @timestamp)
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@eventType", eventType);
        AddParameter(command, "@timestamp", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
