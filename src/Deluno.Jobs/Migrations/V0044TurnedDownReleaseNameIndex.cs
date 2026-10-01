using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: a release is counted once per day, however many searches meet it again, so the recorder asks
/// "was this release already turned down today, and why" for every candidate. This index answers that by
/// name without reading the whole day.
/// </summary>
public sealed class V0044TurnedDownReleaseNameIndex : SqliteSqlMigration
{
    public override int Version => 44;

    public override string Name => "turned_down_release_name_index";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_turned_down_releases_name
            ON turned_down_releases (release_name COLLATE NOCASE, searched_utc DESC);
        """;
}
