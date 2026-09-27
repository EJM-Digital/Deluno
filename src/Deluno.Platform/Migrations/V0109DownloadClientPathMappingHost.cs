using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A Host column for remote path mappings (#809), so a mapping added for one
/// client is reusable by any other client at the same host - matching how
/// Radarr/Sonarr keep one global Remote Path Mappings list keyed by Host,
/// Remote Path and Local Path instead of one list per client.
///
/// Existing rows are backfilled from the download client they were created
/// under, so every mapping already on a host today keeps applying to exactly
/// the client it always did, unchanged - host-sharing is additive, not a
/// replacement for the per-client scoping that came before it.
/// </summary>
public sealed class V0109DownloadClientPathMappingHost : SqliteSqlMigration
{
    public override int Version => 109;

    public override string Name => "download_client_path_mapping_host";

    protected override string Sql =>
        """
        ALTER TABLE download_client_path_mappings ADD COLUMN host TEXT NULL;

        UPDATE download_client_path_mappings
        SET host = (
            SELECT LOWER(TRIM(download_clients.host))
            FROM download_clients
            WHERE download_clients.id = download_client_path_mappings.download_client_id
              AND download_clients.host IS NOT NULL
              AND TRIM(download_clients.host) <> ''
        )
        WHERE EXISTS (
            SELECT 1 FROM download_clients
            WHERE download_clients.id = download_client_path_mappings.download_client_id
              AND download_clients.host IS NOT NULL
              AND TRIM(download_clients.host) <> ''
        );

        CREATE INDEX IF NOT EXISTS ix_download_client_path_mappings_host
            ON download_client_path_mappings (host);
        """;
}
