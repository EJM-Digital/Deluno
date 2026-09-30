using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1076: an indexer proxy's name is worked out from what it is ("FlareSolverr on RIG",
/// "SOCKS5 proxy on 192.168.1.9"), never typed. This gives every existing proxy the name it
/// would be given today. Indexers point at a proxy by id (indexer_sources.proxy_id), so
/// the rename touches nothing that depends on the old name.
/// </summary>
public sealed class V0135IndexerProxyNamesFollowTheirAddress : IDelunoDatabaseMigration
{
    public int Version => 135;

    public string Name => "indexer_proxy_names_follow_their_address";

    public string Checksum => Hash($"{Version}|{Name}|v1");

    public string LegacyChecksum => Checksum;

    public async Task UpAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Name, int? Port)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, proxy_type, address FROM indexer_proxies ORDER BY created_utc, id";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var (name, port) = ProxyTypes.AutomaticName(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                rows.Add((reader.GetString(0), name, port));
            }
        }

        foreach (var (id, name) in ConnectionNames.AssignAll(rows))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE indexer_proxies SET name = @name WHERE id = @id AND name <> @name;";
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

    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
