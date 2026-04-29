using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

public sealed class V0002MovieIdempotencyIndexes : SqliteSqlMigration
{
    public override int Version => 2;

    public override string Name => "movie_idempotency_indexes";

    protected override string Sql =>
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_movie_entries_metadata_provider_id
            ON movie_entries (metadata_provider, metadata_provider_id)
            WHERE metadata_provider IS NOT NULL
              AND metadata_provider_id IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ix_movie_entries_title_year
            ON movie_entries (lower(title), COALESCE(release_year, -1));
        """;
}
