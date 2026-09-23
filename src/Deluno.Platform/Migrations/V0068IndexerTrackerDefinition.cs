using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Which built-in tracker a native tracker indexer runs, and what the user
/// filled in for it (#606). All null for every Newznab, Torznab and RSS
/// indexer. The settings are plain JSON the screen shows back; the secrets
/// (password, cookie, key) are JSON encrypted like an API key, and only their
/// names ever leave Deluno.
/// </summary>
public sealed class V0068IndexerTrackerDefinition : SqliteSqlMigration
{
    public override int Version => 68;

    public override string Name => "indexer_tracker_definition";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN definition_id TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN definition_settings_json TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN definition_secrets TEXT NULL;
        """;
}
