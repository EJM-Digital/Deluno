using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a profile hold "Preferred language" (#769): which languages it
/// searches and downloads toward, and whether that is a hard requirement.
///
/// <para>Empty means the profile has no language opinion, which is what every
/// profile had before it could hold one.</para>
/// </summary>
public sealed class V0075ProfileLanguagePreference : SqliteSqlMigration
{
    public override int Version => 75;

    public override string Name => "profile_language_preference";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN language_preference_json TEXT NOT NULL DEFAULT '';";
}
