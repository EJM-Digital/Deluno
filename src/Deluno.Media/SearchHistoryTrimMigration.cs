using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Media;

/// <summary>
/// Trims the details every existing search-history row kept (#1083) to what the
/// screens show, using the same <see cref="SearchHistoryDetails"/> the writers use.
/// Rows are read and rewritten one at a time, since a single row can be 1.8 MB and
/// a table of them is gigabytes. Compacts the file afterwards, once, because
/// deleting the bulk gives the space back to the file, not to the disk.
/// </summary>
public abstract class SearchHistoryTrimMigration : IDelunoDatabaseMigration
{
    protected abstract string Table { get; }

    public abstract int Version { get; }

    public abstract string Name { get; }

    public string Checksum => Hash($"{Version}|{Name}|{Table}|v1");

    public string LegacyChecksum => Checksum;

    public bool CompactsDatabaseAfterApply => true;

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // Ids first (small), then each document on its own: never the whole table in memory.
        var ids = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT id FROM {Table} WHERE details_json IS NOT NULL AND length(details_json) > 4096;";
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
                read.CommandText = $"SELECT details_json FROM {Table} WHERE id = @id;";
                AddParameter(read, "@id", id);
                details = Convert.ToString(await read.ExecuteScalarAsync(cancellationToken));
            }

            var trimmed = SearchHistoryDetails.Trim(details);
            if (ReferenceEquals(trimmed, details) || trimmed == details)
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {Table} SET details_json = @details WHERE id = @id;";
            AddParameter(update, "@details", trimmed!);
            AddParameter(update, "@id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
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
