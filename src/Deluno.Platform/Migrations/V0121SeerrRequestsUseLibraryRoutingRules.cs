using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1033: a Seerr acquisition is placed by the same library routing rules a
/// manual add uses, evaluated on the title's own metadata (genre, anime,
/// original language, "requested in 4K"), instead of a fixed per-connection
/// mapping. The Seerr card keeps only a fallback library per media type.
///
/// <list type="bullet">
/// <item>destination_rules gains an optional library_id: a rule that carries
/// one routes a title to that library, on top of (or instead of) overriding
/// its root folder.</item>
/// <item>A new "requested4k" match kind lets a rule say "this was a 4K
/// request" - the condition #985's per-connection 4K mapping becomes.</item>
/// <item>seerr_acquisitions gains rule_id/rule_name, so Activity and the
/// Seerr card's recent acquisitions can say which rule placed a request (or
/// that the connection's fallback library did, when none matched).</item>
/// <item>Every seerr_servers row that already had a 4K library mapped (#985)
/// gets an equivalent "requested in 4K" rule pointing at that library, so an
/// existing install keeps routing 4K requests exactly where they went
/// before. A connection with no 4K mapping gets no rule -- its 4K requests
/// now fall to the connection's plain fallback library instead of being
/// refused, the same way an unmatched non-4K request already does; the old
/// movie_4k_*/series_4k_*/fallback_4k_to_normal_library columns stay in the
/// table (harmless) but nothing reads them from here on.</item>
/// </list>
/// </summary>
public sealed class V0121SeerrRequestsUseLibraryRoutingRules : SqliteSqlMigration
{
    public override int Version => 121;

    public override string Name => "seerr_requests_use_library_routing_rules";

    protected override string Sql =>
        """
        ALTER TABLE destination_rules ADD COLUMN library_id TEXT NULL;

        ALTER TABLE seerr_acquisitions ADD COLUMN rule_id TEXT NULL;
        ALTER TABLE seerr_acquisitions ADD COLUMN rule_name TEXT NULL;

        INSERT INTO destination_rules (
            id, name, media_type, match_kind, match_value, root_path, folder_template,
            priority, is_enabled, created_utc, updated_utc, library_id
        )
        SELECT
            lower(hex(randomblob(16))),
            s.name || ' 4K (migrated from the connection''s 4K mapping)',
            'movies', 'requested4k', 'true', COALESCE(l.root_path, ''), NULL,
            10, 1, datetime('now'), datetime('now'), s.movie_4k_library_id
        FROM seerr_servers s
        JOIN libraries l ON l.id = s.movie_4k_library_id
        WHERE s.movie_4k_library_id IS NOT NULL AND length(trim(s.movie_4k_library_id)) > 0;

        INSERT INTO destination_rules (
            id, name, media_type, match_kind, match_value, root_path, folder_template,
            priority, is_enabled, created_utc, updated_utc, library_id
        )
        SELECT
            lower(hex(randomblob(16))),
            s.name || ' 4K (migrated from the connection''s 4K mapping)',
            'tv', 'requested4k', 'true', COALESCE(l.root_path, ''), NULL,
            10, 1, datetime('now'), datetime('now'), s.series_4k_library_id
        FROM seerr_servers s
        JOIN libraries l ON l.id = s.series_4k_library_id
        WHERE s.series_4k_library_id IS NOT NULL AND length(trim(s.series_4k_library_id)) > 0;
        """;
}
