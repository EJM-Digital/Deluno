using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Gives every quality profile its own answer to "when do I stop upgrading".
///
/// <para>#394: one policy governed every profile, so a shelf you want left
/// alone once it is good enough and a shelf you want chased forever could not
/// disagree. Both columns default to the behaviour every profile already had,
/// so nothing changes until somebody says otherwise.</para>
/// </summary>
public sealed class V0054ProfileUpgradeStop : SqliteSqlMigration
{
    public override int Version => 54;

    public override string Name => "profile_upgrade_stop";

    protected override string Sql =>
        """
        ALTER TABLE quality_profiles ADD COLUMN stop_when_cutoff_met INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE quality_profiles ADD COLUMN require_format_gain_for_same_quality INTEGER NOT NULL DEFAULT 1;
        """;
}
