using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Turns an approved Seerr request into a real Deluno acquisition (#851) and
/// reports back once it imports (#852).
///
/// - seerr_servers gains a library/quality-profile mapping per media type, so
///   an approval lands in the right place the same way a manual add does.
/// - seerr_acquisitions is the log both features share: #851 writes a row
///   when it adds and searches for an approval, and #852 reads it back by
///   Deluno's own movie/series id to know which Seerr server and media id to
///   report availability to. It also gives the Seerr settings card something
///   to show, so the automatic action is visible rather than silent.
///
/// Every existing server gets NULL mappings, which the resolver falls back
/// from to "the first library of that media type" — nothing is retroactively
/// turned on for an existing connection.
/// </summary>
public sealed class V0106SeerrAcquisitions : SqliteSqlMigration
{
    public override int Version => 106;

    public override string Name => "seerr_acquisitions";

    protected override string Sql =>
        """
        ALTER TABLE seerr_servers ADD COLUMN movie_library_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN movie_quality_profile_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN series_library_id TEXT NULL;
        ALTER TABLE seerr_servers ADD COLUMN series_quality_profile_id TEXT NULL;

        CREATE TABLE IF NOT EXISTS seerr_acquisitions (
            id TEXT PRIMARY KEY,
            connection_id TEXT NOT NULL,
            connection_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            title TEXT NOT NULL,
            seerr_media_id INTEGER NOT NULL,
            status TEXT NOT NULL,
            message TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_seerr_acquisitions_entity ON seerr_acquisitions(media_type, entity_id);
        CREATE INDEX IF NOT EXISTS idx_seerr_acquisitions_connection ON seerr_acquisitions(connection_id, created_utc DESC);
        """;
}
