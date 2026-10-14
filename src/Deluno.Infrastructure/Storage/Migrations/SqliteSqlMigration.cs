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

    public IReadOnlyCollection<string> SupersededChecksums =>
        SupersededSql.Select(sql => Hash($"{Version}|{Name}|{Normalize(sql)}")).Concat(SupersededChecksumValues).ToArray();

    /// <summary>
    /// Earlier SQL for this migration, replaced before any release shipped it.
    /// Only for a correction made the same day on an unreleased migration.
    /// </summary>
    protected virtual IEnumerable<string> SupersededSql => [];

    /// <summary>
    /// Checksums of earlier definitions, for when the earlier SQL can no longer be kept
    /// in the source (a migration emptied after the data it converted stopped existing).
    /// </summary>
    protected virtual IEnumerable<string> SupersededChecksumValues => [];

    protected abstract string Sql { get; }

    /// <summary>
    /// SQL run in the same transaction just before <see cref="Sql"/>. Not part of the checksum: it
    /// lets a recorded migration open on data its own SQL cannot read, without touching that SQL.
    /// </summary>
    protected virtual string? BeforeSql => null;

    /// <summary>SQL run in the same transaction just after <see cref="Sql"/>. Not part of the checksum.</summary>
    protected virtual string? AfterSql => null;

    /// <summary>See <see cref="IDelunoDatabaseMigration.CompactsDatabaseAfterApply"/>.</summary>
    public virtual bool CompactsDatabaseAfterApply => false;

    public async Task UpAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var text in new[] { BeforeSql, Sql, AfterSql })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = text;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
