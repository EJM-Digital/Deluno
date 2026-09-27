using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Radarr/Sonarr let a download client be tagged so a title with a matching
/// tag is routed only to clients sharing that tag, and a tagged client is
/// skipped for untagged titles (#808). Deluno had no Tags field on download
/// clients at all.
/// </summary>
public sealed class V0082DownloadClientTags : SqliteSqlMigration
{
    public override int Version => 82;

    public override string Name => "download_client_tags";

    protected override string Sql =>
        """
        ALTER TABLE download_clients ADD COLUMN tags TEXT NOT NULL DEFAULT '';
        """;
}
