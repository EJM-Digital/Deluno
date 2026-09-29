using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1076: a connection's name is worked out from what it is ("qBittorrent on RIG",
/// "1337x", "Trakt: my-list"), never typed. This gives every existing connection
/// the name it would be given today. Display only: nothing keys on a name (#1077).
///
/// <para>A name nobody else has is used as it is; two of one kind on one host take
/// their ports; what still clashes takes a short suffix. An indexer added from a
/// tracker definition keeps its name (the definitions are not readable here);
/// it is renamed the next time it is saved.</para>
/// </summary>
public sealed class V0133ConnectionNamesFollowTheirAddress : IDelunoDatabaseMigration
{
    public int Version => 133;

    public string Name => "connection_names_follow_their_address";

    public string Checksum => Hash($"{Version}|{Name}|v1");

    public string LegacyChecksum => Checksum;

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await RenameAsync(connection, transaction, "download_clients", "SELECT id, protocol, host, port, endpoint_url FROM download_clients ORDER BY created_utc, id",
            reader => (reader.GetString(0),
                ConnectionNames.AppOnHost(ConnectionNames.DownloadClientApp(Text(reader, 1)), string.IsNullOrWhiteSpace(Text(reader, 2)) ? Text(reader, 4) : Text(reader, 2)),
                reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3)),
            cancellationToken);

        await RenameAsync(connection, transaction, "indexer_sources", "SELECT id, base_url, definition_id, name FROM indexer_sources ORDER BY created_utc, id",
            reader => (reader.GetString(0),
                string.IsNullOrWhiteSpace(Text(reader, 2)) ? ConnectionNames.SiteName(Text(reader, 1)) : reader.GetString(3),
                ConnectionNames.PortOf(Text(reader, 1))),
            cancellationToken);

        await RenameAsync(connection, transaction, "media_servers", "SELECT id, kind, base_url FROM media_servers ORDER BY created_utc, id",
            reader => (reader.GetString(0),
                ConnectionNames.AppOnHost(Deluno.Connections.Contracts.MediaServerKinds.DisplayName(Text(reader, 1) ?? string.Empty), Text(reader, 2)),
                ConnectionNames.PortOf(Text(reader, 2))),
            cancellationToken);

        await RenameAsync(connection, transaction, "seerr_servers", "SELECT id, base_url FROM seerr_servers ORDER BY created_utc, id",
            reader => (reader.GetString(0), ConnectionNames.AppOnHost("Seerr", Text(reader, 1)), ConnectionNames.PortOf(Text(reader, 1))),
            cancellationToken);

        await RenameAsync(connection, transaction, "notification_webhooks", "SELECT id, provider, url FROM notification_webhooks ORDER BY created_utc, id",
            reader => (reader.GetString(0),
                ConnectionNames.AppOnHost(Deluno.Notifications.Contracts.NotificationProviders.DisplayName(Deluno.Notifications.Contracts.NotificationProviders.Normalize(Text(reader, 1))), Text(reader, 2)),
                ConnectionNames.PortOf(Text(reader, 2))),
            cancellationToken);

        await RenameAsync(connection, transaction, "processor_connections", "SELECT id, provider, submission_url FROM processor_connections ORDER BY created_utc, id",
            reader => (reader.GetString(0),
                ConnectionNames.AppOnHost(Text(reader, 1) == "fileflows-webhook" ? "FileFlows" : "Processor", Text(reader, 2)),
                ConnectionNames.PortOf(Text(reader, 2))),
            cancellationToken);

        await RenameAsync(connection, transaction, "intake_sources", "SELECT id, provider, feed_url FROM intake_sources ORDER BY created_utc, id",
            reader => (reader.GetString(0), ConnectionNames.ImportList(Text(reader, 1), Text(reader, 2)), null),
            cancellationToken);
    }

    private static async Task RenameAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string select,
        Func<DbDataReader, (string Id, string Name, int? Port)> read,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Name, int? Port)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = select;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(read(reader));
            }
        }

        foreach (var (id, name) in ConnectionNames.AssignAll(rows))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET name = @name WHERE id = @id AND name <> @name;";
            AddParameter(update, "@name", name);
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

    private static string? Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index));

    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
