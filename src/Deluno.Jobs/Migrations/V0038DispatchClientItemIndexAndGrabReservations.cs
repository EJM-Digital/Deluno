using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1062: deciding which client items are Deluno's own asks about exact ids, so
/// it needs an index on (client, id, ignoring case) rather than a scan of every
/// dispatch ever made. The primary key is left alone.
///
/// <para>Also the table a grab is reserved in before it is sent: the release's
/// info-hash is known then, and a second Deluno grab of the same release (a film
/// in two libraries, two search jobs at once) must see Deluno's own record of
/// it, so it follows the copy instead of blocklisting a release Deluno itself
/// sent. Ids are kept lower-case.</para>
/// </summary>
public sealed class V0038DispatchClientItemIndexAndGrabReservations : SqliteSqlMigration
{
    public override int Version => 38;

    public override string Name => "dispatch_client_item_index_and_grab_reservations";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_download_dispatches_client_item_id
            ON download_dispatches (download_client_id, torrent_hash_or_item_id COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS download_grab_reservations (
            id TEXT NOT NULL PRIMARY KEY,
            download_client_id TEXT NOT NULL,
            client_item_id TEXT NOT NULL,
            release_name TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_download_grab_reservations_client_item
            ON download_grab_reservations (download_client_id, client_item_id);
        """;
}
