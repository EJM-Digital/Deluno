using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #992: a release profile can skip its own acquisition delay once a release
/// is already the best quality its title's quality profile allows - the same
/// rule as Radarr's delay profile "Bypass if Highest Quality" (checked in
/// Radarr's own source, not its wiki). Defaults off, so every release profile
/// that existed before this column did keeps waiting out its configured
/// delay exactly as it does today (#936).
/// </summary>
public sealed class V0118ReleaseProfileBypassIfHighestQuality : SqliteSqlMigration
{
    public override int Version => 118;

    public override string Name => "release_profile_bypass_if_highest_quality";

    protected override string Sql =>
        "ALTER TABLE release_profiles ADD COLUMN bypass_if_highest_quality INTEGER NOT NULL DEFAULT 0;";
}
