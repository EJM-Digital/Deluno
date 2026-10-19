using System.Data.Common;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

/// <summary>
/// The log #851 writes to when it turns an approval into an add+search, and
/// #852 reads back by Deluno's own movie/series id to know which Seerr
/// server and media id to report availability to.
/// </summary>
public interface ISeerrAcquisitionsRepository
{
    /// <param name="ruleId">
    /// The destination rule that placed this request (#1033), or null when
    /// the connection's fallback library was used instead, or on a failure.
    /// </param>
    Task<SeerrAcquisitionItem> RecordAsync(
        string connectionId,
        string connectionName,
        string mediaType,
        string entityId,
        string title,
        int seerrMediaId,
        string status,
        string? message,
        string? requestedBy,
        CancellationToken cancellationToken,
        string? ruleId = null,
        string? ruleName = null);

    /// <summary>The last few acquisitions for one Seerr server, newest first, for its settings card.</summary>
    Task<IReadOnlyList<SeerrAcquisitionItem>> ListRecentAsync(string connectionId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent "added" row for a title Deluno just imported (#852),
    /// so the import pipeline knows which Seerr server and media id to call
    /// back — or null when this title never came from a Seerr approval.
    /// </summary>
    Task<SeerrAcquisitionItem?> FindLatestAddedAsync(string mediaType, string entityId, CancellationToken cancellationToken);

    Task UpdateStatusAsync(string id, string status, string? message, CancellationToken cancellationToken);
}

public sealed class SqliteSeerrAcquisitionsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    Deluno.Contracts.IConnectionUseReporter? connectionUses = null)
    : ISeerrAcquisitionsRepository
{
    private const string Columns =
        "id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc, requested_by, rule_id, rule_name";

    public async Task<SeerrAcquisitionItem> RecordAsync(
        string connectionId,
        string connectionName,
        string mediaType,
        string entityId,
        string title,
        int seerrMediaId,
        string status,
        string? message,
        string? requestedBy,
        CancellationToken cancellationToken,
        string? ruleId = null,
        string? ruleName = null)
    {
        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7().ToString("N");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO seerr_acquisitions (
                id, connection_id, connection_name, media_type, entity_id, title, seerr_media_id, status, message, created_utc, updated_utc, requested_by, rule_id, rule_name
            ) VALUES (
                @id, @connectionId, @connectionName, @mediaType, @entityId, @title, @seerrMediaId, @status, @message, @now, @now, @requestedBy, @ruleId, @ruleName
            );
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@connectionName", connectionName);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@entityId", entityId);
        AddParameter(command, "@title", title);
        AddParameter(command, "@seerrMediaId", seerrMediaId);
        AddParameter(command, "@status", status);
        AddParameter(command, "@message", message);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@requestedBy", string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim());
        AddParameter(command, "@ruleId", string.IsNullOrWhiteSpace(ruleId) ? null : ruleId.Trim());
        AddParameter(command, "@ruleName", string.IsNullOrWhiteSpace(ruleName) ? null : ruleName.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);

        // The request is saved: light the Seerr tile on Live and move its count (#1098).
        if (connectionUses is not null)
        {
            await connectionUses.ReportAsync(Deluno.Contracts.ConnectionKinds.Requests, connectionId, connectionName, cancellationToken);
        }

        return new SeerrAcquisitionItem(
            id, connectionId, connectionName, mediaType, entityId, title, seerrMediaId, status, message, now, now,
            string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim(),
            string.IsNullOrWhiteSpace(ruleId) ? null : ruleId.Trim(),
            string.IsNullOrWhiteSpace(ruleName) ? null : ruleName.Trim());
    }

    public async Task<IReadOnlyList<SeerrAcquisitionItem>> ListRecentAsync(string connectionId, int take, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM seerr_acquisitions WHERE connection_id = @connectionId ORDER BY created_utc DESC LIMIT @take;";
        AddParameter(command, "@connectionId", connectionId);
        AddParameter(command, "@take", Math.Clamp(take, 1, 100));
        var items = new List<SeerrAcquisitionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<SeerrAcquisitionItem?> FindLatestAddedAsync(string mediaType, string entityId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {Columns} FROM seerr_acquisitions
             WHERE media_type = @mediaType AND entity_id = @entityId AND status IN ('added', 'available-failed')
             ORDER BY created_utc DESC
             LIMIT 1;
             """;
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@entityId", entityId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpdateStatusAsync(string id, string status, string? message, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE seerr_acquisitions SET status = @status, message = @message, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@status", status);
        AddParameter(command, "@message", message);
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SeerrAcquisitionItem Read(DbDataReader reader)
        => new(
            Id: reader.GetString(0),
            ConnectionId: reader.GetString(1),
            ConnectionName: reader.GetString(2),
            MediaType: reader.GetString(3),
            EntityId: reader.GetString(4),
            Title: reader.GetString(5),
            SeerrMediaId: reader.GetInt32(6),
            Status: reader.GetString(7),
            Message: reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(9)),
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(10)),
            RequestedBy: reader.IsDBNull(11) ? null : reader.GetString(11),
            RuleId: reader.IsDBNull(12) ? null : reader.GetString(12),
            RuleName: reader.IsDBNull(13) ? null : reader.GetString(13));
}
