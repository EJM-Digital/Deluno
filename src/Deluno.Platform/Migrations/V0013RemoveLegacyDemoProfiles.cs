using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>Removes the four profiles that older builds silently generated for an empty install.</summary>
public sealed class V0013RemoveLegacyDemoProfiles : SqliteSqlMigration
{
    public override int Version => 13;

    public override string Name => "remove_legacy_demo_profiles";

    protected override string Sql =>
        """
        DELETE FROM quality_profiles
        WHERE preset_id IS NULL
          AND created_utc = updated_utc
          AND name IN (
              'Movies / Standard',
              'Movies / Premium 4K',
              'TV Shows / Standard',
              'TV Shows / Premium 4K'
          )
          AND NOT EXISTS (
              SELECT 1
              FROM libraries
              WHERE libraries.quality_profile_id = quality_profiles.id
          )
          AND 4 = (
              SELECT COUNT(*)
              FROM quality_profiles AS legacy_group
              WHERE legacy_group.preset_id IS NULL
                AND legacy_group.created_utc = quality_profiles.created_utc
                AND legacy_group.updated_utc = quality_profiles.updated_utc
                AND legacy_group.name IN (
                    'Movies / Standard',
                    'Movies / Premium 4K',
                    'TV Shows / Standard',
                    'TV Shows / Premium 4K'
                )
          );
        """;
}
