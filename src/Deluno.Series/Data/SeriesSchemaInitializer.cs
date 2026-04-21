using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Data;

public sealed class SeriesSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<SeriesSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS series_entries (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                start_year INTEGER NULL,
                imdb_id TEXT NULL,
                monitored INTEGER NOT NULL,
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
                detected_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS series_wanted_state (
                series_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                has_file INTEGER NOT NULL DEFAULT 0,
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

        await command.ExecuteNonQueryAsync(cancellationToken);
        await MigrateWantedStateAsync(connection, cancellationToken);

        logger.LogInformation(
            "Series schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Series));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task MigrateWantedStateAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info(series_wanted_state);";

        var keyColumns = 0;
        using (var reader = await tableInfo.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(5) && reader.GetInt32(5) > 0)
                {
                    keyColumns++;
                }
            }
        }

        if (keyColumns >= 2)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            ALTER TABLE series_wanted_state RENAME TO series_wanted_state_legacy;

            CREATE TABLE series_wanted_state (
                series_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                has_file INTEGER NOT NULL DEFAULT 0,
                quality_cutoff_met INTEGER NOT NULL DEFAULT 0,
                missing_since_utc TEXT NULL,
                last_search_utc TEXT NULL,
                next_eligible_search_utc TEXT NULL,
                last_search_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (series_id, library_id),
                FOREIGN KEY (series_id) REFERENCES series_entries(id) ON DELETE CASCADE
            );

            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            SELECT
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            FROM series_wanted_state_legacy;

            DROP TABLE series_wanted_state_legacy;

            CREATE INDEX IF NOT EXISTS ix_series_wanted_state_library_status
                ON series_wanted_state (library_id, wanted_status, next_eligible_search_utc);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
