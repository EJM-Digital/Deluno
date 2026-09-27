using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// TMDb episode-groups ordering (#863): which orderings TMDb actually
/// publishes for a title, and each episode's season/episode number under the
/// "DVD order" group when TMDb has one. Mirrors <see cref="V0035SeriesNumbering"/>'s
/// shape for the automatic-only, provider-sourced facts it adds.
/// </summary>
public sealed class V0045SeriesEpisodeGroupOrdering : SqliteSqlMigration
{
    public override int Version => 45;

    public override string Name => "series_episode_group_ordering";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN available_numbering_schemes TEXT NULL;
        ALTER TABLE series_entries ADD COLUMN other_numbering_scheme_names TEXT NULL;

        ALTER TABLE episode_entries ADD COLUMN dvd_season_number INTEGER NULL;
        ALTER TABLE episode_entries ADD COLUMN dvd_episode_number INTEGER NULL;

        CREATE INDEX IF NOT EXISTS ix_episode_entries_dvd_number
            ON episode_entries (series_id, dvd_season_number, dvd_episode_number);
        """;
}
