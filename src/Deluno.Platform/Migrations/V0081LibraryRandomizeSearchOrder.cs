using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a library shuffle its automatic wanted-title search order instead of
/// always searching oldest-added first, so a title added recently is never
/// permanently stuck behind a long backlog (#831).
///
/// <para>Off by default, which keeps every existing library's oldest-first
/// order exactly as it was.</para>
/// </summary>
public sealed class V0081LibraryRandomizeSearchOrder : SqliteSqlMigration
{
    public override int Version => 81;

    public override string Name => "library_randomize_search_order";

    protected override string Sql =>
        "ALTER TABLE libraries ADD COLUMN randomize_search_order INTEGER NOT NULL DEFAULT 0;";
}
