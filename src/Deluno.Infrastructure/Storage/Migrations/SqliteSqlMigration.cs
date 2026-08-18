using System.Data.Common;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

namespace Deluno.Infrastructure.Storage.Migrations;

public abstract class SqliteSqlMigration : IDelunoDatabaseMigration
{
    private readonly Lazy<string> _checksum;
    private readonly Lazy<string> _legacyChecksum;

    protected SqliteSqlMigration()
    {
        // Hashing the raw literal made the checksum depend on how the file was
        // checked out: git rewriting LF to CRLF changed the hash of an unchanged
        // migration, and the migrator then refused to start against a database
        // that was perfectly valid. Normalise line endings and trailing
        // whitespace so the hash follows the SQL, not the working tree.
        _checksum = new Lazy<string>(() => Hash($"{Version}|{Name}|{Normalize(Sql)}"));
        _legacyChecksum = new Lazy<string>(() => Hash($"{Version}|{Name}|{Sql}"));
    }

    private static string Normalize(string sql) =>
        string.Join('\n', sql.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.TrimEnd())).Trim();

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public abstract int Version { get; }

    public abstract string Name { get; }

    public string Checksum => _checksum.Value;

    public string LegacyChecksum => _legacyChecksum.Value;

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
