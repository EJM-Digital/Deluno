using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1083: two things made Activity a fifth of the jobs database. A condition that held for many
/// check cycles was written on every one (127,269 "found its files" lines for 7 downloads in
/// 3 days), and each "sent to the download client" line carried a full copy of the search's
/// candidate list (150 KB). This keeps the earliest line of each distinct message per item for the
/// conditions that repeat, trims the copied candidate lists to what the screens show, adds the
/// index the writers use to ask for an item's newest line, and compacts the file once.
/// </summary>
public sealed class V0041CollapseRepeatedActivity : IDelunoDatabaseMigration
{
    private static readonly string[] RepeatingCategories =
    [
        "download.vanished.recovered",
        "processing.waiting",
        "download.sharing.reclaimed",
        "download.sharing.stopped",
        "download.sharing.reclaim-previewed"
    ];

    public int Version => 41;

    public string Name => "collapse_repeated_activity";

    public string Checksum => Hash($"{Version}|{Name}|v1");

    public string LegacyChecksum => Checksum;

    public bool CompactsDatabaseAfterApply => true;

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var categories = string.Join(", ", RepeatingCategories.Select(category => $"'{category}'"));
        await ExecuteAsync(connection, transaction,
            $"""
            DELETE FROM activity_events
            WHERE category IN ({categories})
              AND id NOT IN (
                    SELECT id FROM (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY category, related_entity_type, related_entity_id, message
                                   ORDER BY created_utc, id) AS position
                        FROM activity_events
                        WHERE category IN ({categories}))
                    WHERE position = 1);

            CREATE INDEX IF NOT EXISTS ix_activity_events_category_entity
                ON activity_events (category, related_entity_id, created_utc DESC);
            """,
            cancellationToken);

        // One row at a time: a copied candidate list is 150 KB and there are hundreds of them.
        var ids = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM activity_events WHERE category LIKE 'download.dispatch.%' AND details_json IS NOT NULL AND length(details_json) > 4096;";
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            string? details;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT details_json FROM activity_events WHERE id = @id;";
                AddParameter(read, "@id", id);
                details = Convert.ToString(await read.ExecuteScalarAsync(cancellationToken));
            }

            var trimmed = SearchHistoryDetails.Trim(details);
            if (trimmed is null || trimmed == details)
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE activity_events SET details_json = @details WHERE id = @id;";
            AddParameter(update, "@details", trimmed);
            AddParameter(update, "@id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
