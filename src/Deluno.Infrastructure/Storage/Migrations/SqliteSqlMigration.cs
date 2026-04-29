using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Deluno.Infrastructure.Storage.Migrations;

public abstract class SqliteSqlMigration : IDelunoDatabaseMigration
{
    private readonly Lazy<string> _checksum;

    protected SqliteSqlMigration()
    {
        _checksum = new Lazy<string>(() =>
        {
            var input = $"{GetType().FullName}|{Version}|{Name}|{Sql}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        });
    }

    public abstract int Version { get; }

    public abstract string Name { get; }

    public string Checksum => _checksum.Value;

    protected abstract string Sql { get; }

    public async Task UpAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
