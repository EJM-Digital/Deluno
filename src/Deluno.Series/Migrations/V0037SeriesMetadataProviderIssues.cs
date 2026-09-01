using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>Stores dismissible, title-scoped evidence that a linked provider record is missing.</summary>
public sealed class V0037SeriesMetadataProviderIssues : SqliteSqlMigration
{
    public override int Version => 37;

    public override string Name => "series_metadata_provider_issues";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS series_metadata_provider_issue (
            series_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            provider TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            evidence_key TEXT NOT NULL,
            detected_utc TEXT NOT NULL,
            acknowledged_utc TEXT NULL,
            FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE
        );
        """;
}
