using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Drops the stored "write .nfo files" setting (#1030). Nothing reads
/// <c>metadata.nfoEnabled</c> any more -- Deluno never writes .nfo files
/// (sheet row G230, #1025) -- and no screen ever showed it, so the row is
/// just leftover state on an install that once saved the settings form.
/// </summary>
public sealed class V0115RemoveDeadNfoSetting : SqliteSqlMigration
{
    public override int Version => 115;

    public override string Name => "remove_dead_nfo_setting";

    protected override string Sql =>
        """
        DELETE FROM system_settings WHERE setting_key = 'metadata.nfoEnabled';
        """;
}
