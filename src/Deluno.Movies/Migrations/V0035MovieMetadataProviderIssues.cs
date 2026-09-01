using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>Stores dismissible, title-scoped evidence that a linked provider record is missing.</summary>
public sealed class V0035MovieMetadataProviderIssues : SqliteSqlMigration
{
    public override int Version => 35;

    public override string Name => "movie_metadata_provider_issues";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS movie_metadata_provider_issue (
            movie_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            provider TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            evidence_key TEXT NOT NULL,
            detected_utc TEXT NOT NULL,
            acknowledged_utc TEXT NULL,
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );
        """;
}
