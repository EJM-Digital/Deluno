using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// Records that somebody asked for this entry's metadata to be refreshed, as
/// distinct from when it was last refreshed or last attempted.
///
/// "Refresh everything" previously meant loading the catalogue, taking the first
/// few hundred, and queueing a job each — which silently covered a few percent
/// of a large library and said nothing about the rest. Marking the request
/// instead lets the backfill work through the whole library at its own pace, and
/// does it without destroying the record of when each entry was genuinely last
/// refreshed.
/// </summary>
public sealed class V0010SeriesMetadataRefreshRequests : SqliteSqlMigration
{
    public override int Version => 10;

    public override string Name => "series_metadata_refresh_requests";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN metadata_refresh_requested_utc TEXT NULL;
        """;
}
