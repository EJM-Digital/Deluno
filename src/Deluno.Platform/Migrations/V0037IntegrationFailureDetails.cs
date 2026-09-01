using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Keeps the typed failure that produced the latest integration health result.
/// The legacy category and message columns remain in place for compatibility;
/// this JSON column is the forward-compatible contract for Health and Activity.
/// </summary>
public sealed class V0037IntegrationFailureDetails : SqliteSqlMigration
{
    public override int Version => 37;

    public override string Name => "integration_failure_details";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN last_health_failure_json TEXT NULL;
        ALTER TABLE download_clients ADD COLUMN last_health_failure_json TEXT NULL;
        """;
}
