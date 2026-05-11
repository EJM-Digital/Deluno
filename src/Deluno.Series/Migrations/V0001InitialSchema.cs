using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

public static class SeriesDatabaseMigrations
{
    public static readonly IReadOnlyList<IDelunoDatabaseMigration> All =
    [
        new V0001InitialSchema(),
        new V0002SeriesIdempotencyIndexes(),
        new V0003SeriesTrackedFiles(),
        new V0004SeriesEpisodeQualityTracking(),
        new V0005SeriesImportRecoveryStatus()
    ];

    private sealed class V0001InitialSchema : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "initial_schema";

        protected override string Sql =>
            """
            CREATE TABLE IF NOT EXISTS series_entries (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                start_year INTEGER NULL,
                imdb_id TEXT NULL,
                monitored INTEGER NOT NULL,
                metadata_provider TEXT NULL,
                metadata_provider_id TEXT NULL,
                original_title TEXT NULL,
                overview TEXT NULL,
                poster_url TEXT NULL,
                backdrop_url TEXT NULL,
                rating REAL NULL,
                genres TEXT NULL,
                external_url TEXT NULL,
                metadata_json TEXT NULL,
                metadata_updated_utc TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_series_entries_imdb_id
                ON series_entries (imdb_id)
                WHERE imdb_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS series_import_recovery_cases (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                failure_kind TEXT NOT NULL,
                summary TEXT NOT NULL,
                recommended_action TEXT NOT NULL,
                details_json TEXT NULL,
                detected_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS series_wanted_state (
                series_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                has_file INTEGER NOT NULL DEFAULT 0,
                current_quality TEXT NULL,
                target_quality TEXT NULL,
                quality_cutoff_met INTEGER NOT NULL DEFAULT 0,
                missing_since_utc TEXT NULL,
                last_search_utc TEXT NULL,
                next_eligible_search_utc TEXT NULL,
                last_search_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (series_id, library_id),
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_series_wanted_state_library_status
                ON series_wanted_state (library_id, wanted_status, next_eligible_search_utc);

            CREATE TABLE IF NOT EXISTS season_entries (
                id TEXT PRIMARY KEY,
                series_id TEXT NOT NULL,
                season_number INTEGER NOT NULL,
                monitored INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_season_entries_series_number
                ON season_entries (series_id, season_number);

            CREATE TABLE IF NOT EXISTS episode_entries (
                id TEXT PRIMARY KEY,
                series_id TEXT NOT NULL,
                season_id TEXT NULL,
                season_number INTEGER NOT NULL,
                episode_number INTEGER NOT NULL,
                title TEXT NULL,
                air_date_utc TEXT NULL,
                monitored INTEGER NOT NULL DEFAULT 1,
                has_file INTEGER NOT NULL DEFAULT 0,
                quality_cutoff_met INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (season_id) REFERENCES season_entries(id) ON DELETE SET NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_episode_entries_series_season_episode
                ON episode_entries (series_id, season_number, episode_number);

            CREATE TABLE IF NOT EXISTS episode_wanted_state (
                episode_id TEXT PRIMARY KEY,
                series_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                last_search_utc TEXT NULL,
                next_eligible_search_utc TEXT NULL,
                last_search_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_episode_wanted_state_library_status
                ON episode_wanted_state (library_id, wanted_status, next_eligible_search_utc);

            CREATE TABLE IF NOT EXISTS series_search_history (
                id TEXT PRIMARY KEY,
                series_id TEXT NOT NULL,
                episode_id TEXT NULL,
                library_id TEXT NOT NULL,
                trigger_kind TEXT NOT NULL,
                outcome TEXT NOT NULL,
                release_name TEXT NULL,
                indexer_name TEXT NULL,
                details_json TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_series_search_history_series_created
                ON series_search_history (series_id, created_utc DESC);

            CREATE TABLE IF NOT EXISTS series_import_recovery_events (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                event_kind TEXT NOT NULL,
                message TEXT NOT NULL,
                metadata_json TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY (case_id) REFERENCES series_import_recovery_cases(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_series_import_recovery_events_case_created
                ON series_import_recovery_events (case_id, created_utc DESC);
            
            """;
    }
}
