using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// Adds explicit TV numbering metadata without changing canonical episode
/// identity. Provider refreshes can update their own alternate facts while an
/// owner mapping remains protected by <c>numbering_source = 'owner'</c>.
/// </summary>
public sealed class V0035SeriesNumbering : SqliteSqlMigration
{
    public override int Version => 35;

    public override string Name => "series_numbering";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN series_type TEXT NOT NULL DEFAULT 'standard';
        ALTER TABLE series_entries ADD COLUMN numbering_scheme TEXT NOT NULL DEFAULT 'standard';
        ALTER TABLE series_entries ADD COLUMN numbering_source TEXT NOT NULL DEFAULT 'provider';
        ALTER TABLE series_entries ADD COLUMN numbering_updated_utc TEXT NULL;

        ALTER TABLE episode_entries ADD COLUMN absolute_number INTEGER NULL;
        ALTER TABLE episode_entries ADD COLUMN scene_season_number INTEGER NULL;
        ALTER TABLE episode_entries ADD COLUMN scene_episode_number INTEGER NULL;
        ALTER TABLE episode_entries ADD COLUMN airdate_key TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN numbering_source TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_episode_entries_absolute_number
            ON episode_entries (series_id, absolute_number);
        CREATE INDEX IF NOT EXISTS ix_episode_entries_scene_number
            ON episode_entries (series_id, scene_season_number, scene_episode_number);
        CREATE INDEX IF NOT EXISTS ix_episode_entries_airdate_key
            ON episode_entries (series_id, airdate_key);
        """;
}
