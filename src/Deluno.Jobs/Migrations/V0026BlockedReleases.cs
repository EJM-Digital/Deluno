using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// The releases Deluno has decided not to use again (DESIGN-007 decisions 1
/// and 2).
///
/// <para>Keyed on the release name and the indexer that offered it, because
/// that is what a search candidate carries — an infohash only exists after a
/// grab, and by then the decision is already made. The hash is recorded when
/// known so a forced clear-out has something to hand the download client, but
/// it is never what a candidate is matched on.</para>
///
/// <para>The title is recorded for the person reading the list, not for
/// matching. A file with no video stream is a bad file whichever title it was
/// fetched for.</para>
/// </summary>
public sealed class V0026BlockedReleases : SqliteSqlMigration
{
    public override int Version => 26;

    public override string Name => "blocked_releases";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS blocked_releases (
            id TEXT PRIMARY KEY,
            release_key TEXT NOT NULL,
            release_name TEXT NOT NULL,
            indexer_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            entity_id TEXT NULL,
            title TEXT NULL,
            reason_code TEXT NOT NULL,
            reason TEXT NOT NULL,
            torrent_hash_or_item_id TEXT NULL,
            download_client_id TEXT NULL,
            download_client_name TEXT NULL,
            blocked_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_blocked_releases_key
            ON blocked_releases (release_key);

        CREATE INDEX IF NOT EXISTS ix_blocked_releases_entity
            ON blocked_releases (media_type, entity_id);
        """;
}
