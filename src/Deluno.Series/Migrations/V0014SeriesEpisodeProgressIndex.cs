using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The index that lets a catalogue page ask how far through its aired episodes
/// each show is without touching a single episode row.
///
/// The page reads five numbers per show — total, aired, aired and held, aired
/// and upgradable, and the next air date — and every one of them is answered by
/// <c>air_date_utc</c>, <c>has_file</c> and <c>quality_cutoff_met</c>. The only
/// existing index that leads with <c>series_id</c> carries the season and
/// episode numbers instead, so each show cost one row lookup per episode. This
/// covers the question outright: for a page of fifty shows the whole pass stays
/// inside the index.
/// </summary>
public sealed class V0014SeriesEpisodeProgressIndex : SqliteSqlMigration
{
    public override int Version => 14;

    public override string Name => "series_episode_progress_index";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_episode_entries_series_progress
            ON episode_entries (series_id, air_date_utc, has_file, quality_cutoff_met);
        """;
}
