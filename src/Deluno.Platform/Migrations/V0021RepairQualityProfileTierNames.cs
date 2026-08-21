using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Converts the TRaSH-style quality aliases that older setup flows persisted
/// into the names used by Deluno's authoritative quality model.
/// </summary>
public sealed class V0021RepairQualityProfileTierNames : SqliteSqlMigration
{
    public override int Version => 21;

    public override string Name => "repair_quality_profile_tier_names";

    protected override string Sql =>
        """
        WITH RECURSIVE
        tier_alias(alias, tier_name) AS (
            VALUES
                ('webdl-720p', 'WEB 720p'),
                ('webrip-720p', 'WEB 720p'),
                ('WEB-DL 720p', 'WEB 720p'),
                ('WEBRip 720p', 'WEB 720p'),
                ('webdl-1080p', 'WEB 1080p'),
                ('webrip-1080p', 'WEB 1080p'),
                ('WEB-DL 1080p', 'WEB 1080p'),
                ('WEBRip 1080p', 'WEB 1080p'),
                ('webdl-2160p', 'WEB 2160p'),
                ('webrip-2160p', 'WEB 2160p'),
                ('WEB-DL 4K', 'WEB 2160p'),
                ('WEB-DL 2160p', 'WEB 2160p'),
                ('WEBRip 4K', 'WEB 2160p'),
                ('WEBRip 2160p', 'WEB 2160p'),
                ('hdtv-720p', 'HDTV 720p'),
                ('hdtv-1080p', 'HDTV 1080p'),
                ('hdtv-2160p', 'HDTV 2160p'),
                ('HDTV 4K', 'HDTV 2160p'),
                ('bluray-720p', 'Bluray 720p'),
                ('bluray-1080p', 'Bluray 1080p'),
                ('bluray-2160p', 'Bluray 2160p'),
                ('Bluray 4K', 'Bluray 2160p'),
                ('remux-1080p', 'Remux 1080p'),
                ('remux-2160p', 'Remux 2160p'),
                ('Remux 4K', 'Remux 2160p')
        ),
        split(profile_id, remainder, token, ordinal) AS (
            SELECT id, allowed_qualities || ',', NULL, 0
            FROM quality_profiles
            WHERE allowed_qualities <> ''

            UNION ALL

            SELECT profile_id,
                   substr(remainder, instr(remainder, ',') + 1),
                   trim(substr(remainder, 1, instr(remainder, ',') - 1)),
                   ordinal + 1
            FROM split
            WHERE remainder <> ''
        ),
        mapped AS (
            SELECT s.profile_id,
                   COALESCE(
                       (
                           SELECT a.tier_name
                           FROM tier_alias AS a
                           WHERE lower(a.alias) = lower(trim(s.token))
                       ),
                       trim(s.token)
                   ) AS tier_name,
                   s.ordinal,
                   EXISTS (
                       SELECT 1
                       FROM tier_alias AS a
                       WHERE lower(a.alias) = lower(trim(s.token))
                   ) AS is_alias
            FROM split AS s
            WHERE s.token IS NOT NULL
              AND trim(s.token) <> ''
        ),
        deduped AS (
            SELECT current_token.profile_id, current_token.tier_name, current_token.ordinal
            FROM mapped AS current_token
            WHERE NOT EXISTS (
                SELECT 1
                FROM mapped AS earlier_token
                WHERE earlier_token.profile_id = current_token.profile_id
                  AND lower(earlier_token.tier_name) = lower(current_token.tier_name)
                  AND earlier_token.ordinal < current_token.ordinal
            )
        ),
        repaired AS (
            SELECT profile_id,
                   (
                       SELECT group_concat(ordered.tier_name, ', ')
                       FROM (
                           SELECT tier_name
                           FROM deduped
                           WHERE deduped.profile_id = grouped.profile_id
                           ORDER BY ordinal
                       ) AS ordered
                   ) AS allowed_qualities
            FROM deduped AS grouped
            WHERE EXISTS (
                SELECT 1
                FROM mapped
                WHERE mapped.profile_id = grouped.profile_id
                  AND mapped.is_alias = 1
            )
            GROUP BY profile_id
        )
        UPDATE quality_profiles
        SET allowed_qualities = (
            SELECT repaired.allowed_qualities
            FROM repaired
            WHERE repaired.profile_id = quality_profiles.id
        )
        WHERE id IN (SELECT profile_id FROM repaired);

        WITH tier_alias(alias, tier_name) AS (
            VALUES
                ('webdl-720p', 'WEB 720p'),
                ('webrip-720p', 'WEB 720p'),
                ('WEB-DL 720p', 'WEB 720p'),
                ('WEBRip 720p', 'WEB 720p'),
                ('webdl-1080p', 'WEB 1080p'),
                ('webrip-1080p', 'WEB 1080p'),
                ('WEB-DL 1080p', 'WEB 1080p'),
                ('WEBRip 1080p', 'WEB 1080p'),
                ('webdl-2160p', 'WEB 2160p'),
                ('webrip-2160p', 'WEB 2160p'),
                ('WEB-DL 4K', 'WEB 2160p'),
                ('WEB-DL 2160p', 'WEB 2160p'),
                ('WEBRip 4K', 'WEB 2160p'),
                ('WEBRip 2160p', 'WEB 2160p'),
                ('hdtv-720p', 'HDTV 720p'),
                ('hdtv-1080p', 'HDTV 1080p'),
                ('hdtv-2160p', 'HDTV 2160p'),
                ('HDTV 4K', 'HDTV 2160p'),
                ('bluray-720p', 'Bluray 720p'),
                ('bluray-1080p', 'Bluray 1080p'),
                ('bluray-2160p', 'Bluray 2160p'),
                ('Bluray 4K', 'Bluray 2160p'),
                ('remux-1080p', 'Remux 1080p'),
                ('remux-2160p', 'Remux 2160p'),
                ('Remux 4K', 'Remux 2160p')
        )
        UPDATE quality_profiles
        SET cutoff_quality = (
            SELECT tier_alias.tier_name
            FROM tier_alias
            WHERE lower(tier_alias.alias) = lower(trim(quality_profiles.cutoff_quality))
        )
        WHERE EXISTS (
            SELECT 1
            FROM tier_alias
            WHERE lower(tier_alias.alias) = lower(trim(quality_profiles.cutoff_quality))
        );
        """;
}
