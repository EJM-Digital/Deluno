using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Drops the stored "artwork" setting (#1031). Nothing reads
/// <c>metadata.artworkEnabled</c> any more -- Deluno writes no .nfo or
/// artwork files (#1025) -- so the row is just leftover state on an install
/// that once saved the settings form. The artwork twin of
/// <see cref="V0115RemoveDeadNfoSetting"/>.
/// </summary>
public sealed class V0120DropMetadataArtworkEnabled : SqliteSqlMigration
{
    public override int Version => 120;

    public override string Name => "drop_metadata_artwork_enabled";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'metadata.artworkEnabled';
        """;
}
