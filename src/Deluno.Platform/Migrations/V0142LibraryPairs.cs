using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1411: "Both" when adding a library makes two ordinary libraries, one Movies and one TV, and links them. The link is
/// a row per library naming the pair it belongs to, so deleting a library takes its row with it and an install that never
/// uses "Both" simply has no rows.
/// </summary>
public sealed class V0142LibraryPairs : SqliteSqlMigration
{
    public override int Version => 142;

    public override string Name => "library_pairs";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS library_pairs (
            library_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL,
            FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_library_pairs_pair
            ON library_pairs (pair_id);
        """;
}
