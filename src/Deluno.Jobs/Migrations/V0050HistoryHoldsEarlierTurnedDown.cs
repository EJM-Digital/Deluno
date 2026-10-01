using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1100: since V0047 every release Deluno turns down is written to the Activity history as it happens. The ones
/// turned down before that were kept only in <c>turned_down_releases</c>, which Live counts from, so on the day of
/// the upgrade Live said "1,969 releases" while the history filtered to turned-down releases said nothing. This
/// writes each of those earlier rows into the history once, worded and grouped exactly as the recorder words them,
/// at the time it was turned down. Rows recorded after V0047 already have their history line and are left alone.
/// </summary>
public sealed class V0050HistoryHoldsEarlierTurnedDown : IDelunoDatabaseMigration
{
    public int Version => 50;

    public string Name => "history_holds_earlier_turned_down";

    public string Checksum => Hash($"{Version}|{Name}|v1");

    public string LegacyChecksum => Checksum;

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // Everything turned down before the recorder started writing history lines (V0047). On a database that is
        // upgraded straight past V0047 that is every row; on a new one there are none.
        string? since = null;
        using (var applied = connection.CreateCommand())
        {
            applied.Transaction = transaction;
            applied.CommandText = "SELECT applied_utc FROM schema_migrations WHERE version = 47;";
            since = await applied.ExecuteScalarAsync(cancellationToken) as string;
        }

        var rows = new List<(string At, string Release, string Group, string? Raw, string? Indexer, string? Title, string? EntityType, string? EntityId)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT searched_utc, release_name, reason_group, raw_reason, indexer_name, title, entity_type, entity_id
                FROM turned_down_releases
                WHERE @since IS NULL OR searched_utc < @since
                ORDER BY searched_utc;
                """;
            var parameter = select.CreateParameter();
            parameter.ParameterName = "@since";
            parameter.Value = (object?)since ?? DBNull.Value;
            select.Parameters.Add(parameter);
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(3), Text(4), Text(5), Text(6), Text(7)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO activity_events (
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc,
                title, file_path, source_name, reason_group
            )
            VALUES (
                @id, @category, @message, @details, NULL, @entityType, @entityId, @createdUtc,
                @title, NULL, @indexer, @group
            );
            """;
        var id = Add(insert, "@id");
        var category = Add(insert, "@category");
        var message = Add(insert, "@message");
        var details = Add(insert, "@details");
        var entityType = Add(insert, "@entityType");
        var entityId = Add(insert, "@entityId");
        var createdUtc = Add(insert, "@createdUtc");
        var title = Add(insert, "@title");
        var indexer = Add(insert, "@indexer");
        var group = Add(insert, "@group");
        category.Value = ActivityHistory.TurnedDownCategory;

        foreach (var row in rows)
        {
            // The group a screen reads, as the recorder works it out: a row written as "other" is worded from its own raw reason.
            var readGroup = row.Group == TurnedDownGroups.Other ? TurnedDownReasons.GroupFor(row.Raw) : row.Group;
            id.Value = Guid.CreateVersion7(ParseOrNow(row.At)).ToString("N");
            message.Value = $"Turned down {row.Release} · {TurnedDownGroups.Label(readGroup)}";
            details.Value = System.Text.Json.JsonSerializer.Serialize(new { release = row.Release, reason = row.Raw, indexerName = row.Indexer, title = row.Title });
            entityType.Value = (object?)row.EntityType ?? DBNull.Value;
            entityId.Value = (object?)row.EntityId ?? DBNull.Value;
            createdUtc.Value = row.At;
            title.Value = (object?)row.Title ?? DBNull.Value;
            indexer.Value = (object?)row.Indexer ?? DBNull.Value;
            group.Value = readGroup;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static DateTimeOffset ParseOrNow(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UtcNow;

    private static DbParameter Add(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
