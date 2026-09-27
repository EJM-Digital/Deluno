using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr let a download client say "this host, but over SSL" and give
/// usenet clients a separate URL Base for a reverse proxy (#804). Deluno had
/// neither — the scheme was guessed from whether the host string happened to
/// start with "http".
/// </summary>
public sealed class V0071DownloadClientSslAndUrlBase : SqliteSqlMigration
{
    public override int Version => 71;

    public override string Name => "download_client_ssl_and_url_base";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN use_ssl INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN url_base TEXT NULL;
        """;
}
