using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a profile say how it wants a release fetched, without inventing a tag.
///
/// <para>#394: acquisition rules were keyed by tag, so protocol preference and
/// delays sat apart from the seven answers they belong beside, and a profile
/// could not want usenet for anime and torrents for films without a tag to say
/// so. Empty means the profile has no acquisition opinion, which is what every
/// profile had before it could hold one.</para>
/// </summary>
public sealed class V0056ProfileAcquisitionRules : SqliteSqlMigration
{
    public override int Version => 56;

    public override string Name => "profile_acquisition_rules";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN acquisition_json TEXT NOT NULL DEFAULT '';";
}
