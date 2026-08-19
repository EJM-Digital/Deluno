using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Records when metadata was last <em>attempted</em>, as distinct from when it
/// last <em>succeeded</em>.
///
/// Staleness was measured purely on <c>metadata_updated_utc</c>, which is only
/// written on a successful provider match. An entry the provider cannot match
/// therefore stayed stale forever and was re-selected by every backfill pass.
/// At the old 30-per-6-hours allocation that was slow enough to hide; with a
/// continuously topped-up queue it becomes a hot loop against the provider,
/// which is the one thing outbound traffic must never do.
/// </summary>
public sealed class V0009MovieMetadataAttemptTracking : SqliteSqlMigration
{
    public override int Version => 9;

    public override string Name => "movie_metadata_attempt_tracking";

    protected override string Sql =>
        """
        ALTER TABLE movie_entries ADD COLUMN metadata_attempted_utc TEXT NULL;
        """;
}
