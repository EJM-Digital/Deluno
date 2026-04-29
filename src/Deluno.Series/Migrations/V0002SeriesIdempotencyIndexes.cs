using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

public sealed class V0002SeriesIdempotencyIndexes : SqliteSqlMigration
{
    public override int Version => 2;

    public override string Name => "series_idempotency_indexes";

    protected override string Sql =>
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_series_entries_metadata_provider_id
            ON series_entries (metadata_provider, metadata_provider_id)
            WHERE metadata_provider IS NOT NULL
              AND metadata_provider_id IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ix_series_entries_title_year
            ON series_entries (lower(title), COALESCE(start_year, -1));
        """;
}
