using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series twin of <c>V0009MovieMetadataAttemptTracking</c>: records when
/// metadata was last attempted, so an entry the provider cannot match does not
/// stay permanently stale and get re-selected by every backfill pass.
/// </summary>
public sealed class V0009SeriesMetadataAttemptTracking : SqliteSqlMigration
{
    public override int Version => 9;

    public override string Name => "series_metadata_attempt_tracking";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN metadata_attempted_utc TEXT NULL;
        """;
}
