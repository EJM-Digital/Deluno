using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0009CustomFormatTrashIds : SqliteSqlMigration
{
    public override int Version => 9;

    public override string Name => "custom_format_trash_ids";

    protected override string Sql =>
        """
        ALTER TABLE custom_formats ADD COLUMN trash_id TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_custom_formats_media_trash
            ON custom_formats (media_type, trash_id)
            WHERE trash_id IS NOT NULL;
        """;
}
