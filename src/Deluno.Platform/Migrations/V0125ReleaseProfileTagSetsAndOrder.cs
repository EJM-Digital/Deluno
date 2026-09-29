using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #994 and #995: a release rule can apply to several tags, and the rules sit in
/// an order. Radarr and Sonarr link a delay profile to any number of tags and rank
/// the profiles (their DelayProfileService keeps an Order and treats the profile
/// with no tags as the default). Existing rules keep their single tag as a one-tag
/// set and are numbered in their old list order, and how several matching rules
/// are used stays "combine every matching rule" until the setting is changed, so
/// an upgraded install behaves exactly as before (#936).
/// </summary>
public sealed class V0125ReleaseProfileTagSetsAndOrder : SqliteSqlMigration
{
    public override int Version => 125;

    public override string Name => "release_profile_tag_sets_and_order";

    protected override string Sql =>
        """
        ALTER TABLE release_profiles ADD COLUMN tag_names TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_profiles ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
        UPDATE release_profiles SET tag_names = trim(tag_name);
        UPDATE release_profiles
        SET position = (
            SELECT COUNT(*) FROM release_profiles other
            WHERE (trim(other.tag_name) = '') < (trim(release_profiles.tag_name) = '')
               OR ((trim(other.tag_name) = '') = (trim(release_profiles.tag_name) = '')
                   AND (lower(trim(other.tag_name)) < lower(trim(release_profiles.tag_name))
                        OR (lower(trim(other.tag_name)) = lower(trim(release_profiles.tag_name))
                            AND other.id < release_profiles.id))));
        """;
}
