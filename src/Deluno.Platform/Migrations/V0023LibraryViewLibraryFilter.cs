using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0023LibraryViewLibraryFilter : SqliteSqlMigration
{
    public override int Version => 23;

    public override string Name => "library_view_library_filter";

    protected override string Sql =>
        """
        ALTER TABLE library_views ADD COLUMN library_id TEXT NULL;
        CREATE INDEX IF NOT EXISTS ix_library_views_user_variant_library
            ON library_views (user_id, variant, library_id, name);
        """;
}
