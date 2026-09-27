using System.Data.Common;

namespace Deluno.Infrastructure.Storage.Migrations;

public interface IDelunoDatabaseMigration
{
    int Version { get; }

    string Name { get; }

    /// <summary>Content hash of the migration, insensitive to line endings.</summary>
    string Checksum { get; }

    /// <summary>
    /// The hash this migration produced before checksums were normalised.
    /// A database stamped with it is not corrupt — it was written by an older
    /// build, or checked out with different line endings — so the migrator
    /// accepts it once and re-stamps the row.
    /// </summary>
    string LegacyChecksum { get; }

    /// <summary>
    /// Checksums of earlier definitions this migration replaced before any
    /// release shipped it. A database that already ran one of them is
    /// re-stamped rather than refused; it keeps what the earlier definition did.
    /// </summary>
    IReadOnlyCollection<string> SupersededChecksums => [];

    Task UpAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}
