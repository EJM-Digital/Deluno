using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// Room for a provider-sourced episode catalogue.
///
/// Episodes used to exist only where a file had been imported, so `title` and
/// `air_date_utc` were always written as NULL and nothing could say when an
/// episode aired — or that it existed at all before it was downloaded. The
/// catalogue sync fills those, adds the episode synopsis, and records where the
/// row came from so a synced episode is distinguishable from one inferred from
/// a filename.
/// </summary>
public sealed class V0007SeriesEpisodeCatalogue : SqliteSqlMigration
{
    public override int Version => 7;

    public override string Name => "series_episode_catalogue";

    protected override string Sql =>
        """
        ALTER TABLE episode_entries ADD COLUMN overview TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN catalogue_source TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN catalogue_synced_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_episode_entries_air_date
            ON episode_entries (air_date_utc);
        """;
}
