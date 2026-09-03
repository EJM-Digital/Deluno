using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Gives every quality profile its own size answers.
///
/// <para>#394: size lived on the tier, so a Low Storage profile and a Premium 4K
/// profile that both allowed WEB 1080p got the same range for it, and changing
/// one changed the other silently. Anime at 1080p and a film at 1080p are not
/// the same number of gigabytes.</para>
///
/// <para>Empty by default, which means "this profile has no size opinion about
/// that tier" rather than "refuse everything". Existing profiles are seeded from
/// the shared model on first read rather than here, because the shared model is
/// a JSON blob in <c>system_settings</c> and unpicking it in SQL would put a
/// second parser for it in the schema.</para>
/// </summary>
public sealed class V0053ProfileSizeRules : SqliteSqlMigration
{
    public override int Version => 53;

    public override string Name => "profile_size_rules";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN size_rules_json TEXT NOT NULL DEFAULT '';";
}
