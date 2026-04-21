using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Movies.Data;

public sealed class MoviesSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<MoviesSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS movie_entries (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                release_year INTEGER NULL,
                imdb_id TEXT NULL,
                monitored INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_movie_entries_imdb_id
                ON movie_entries (imdb_id)
                WHERE imdb_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS movie_import_recovery_cases (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                failure_kind TEXT NOT NULL,
                summary TEXT NOT NULL,
                recommended_action TEXT NOT NULL,
                detected_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS movie_wanted_state (
                movie_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                minimum_availability TEXT NULL,
                has_file INTEGER NOT NULL DEFAULT 0,
                quality_cutoff_met INTEGER NOT NULL DEFAULT 0,
                missing_since_utc TEXT NULL,
                last_search_utc TEXT NULL,
                next_eligible_search_utc TEXT NULL,
                last_search_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (movie_id, library_id),
                FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_movie_wanted_state_library_status
                ON movie_wanted_state (library_id, wanted_status, next_eligible_search_utc);

            CREATE TABLE IF NOT EXISTS movie_search_history (
                id TEXT PRIMARY KEY,
                movie_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                trigger_kind TEXT NOT NULL,
                outcome TEXT NOT NULL,
                release_name TEXT NULL,
                indexer_name TEXT NULL,
                details_json TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_movie_search_history_movie_created
                ON movie_search_history (movie_id, created_utc DESC);

            CREATE TABLE IF NOT EXISTS movie_import_recovery_events (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                event_kind TEXT NOT NULL,
                message TEXT NOT NULL,
                metadata_json TEXT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY (case_id) REFERENCES movie_import_recovery_cases(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_movie_import_recovery_events_case_created
                ON movie_import_recovery_events (case_id, created_utc DESC);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await MigrateWantedStateAsync(connection, cancellationToken);

        logger.LogInformation(
            "Movies schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Movies));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task MigrateWantedStateAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info(movie_wanted_state);";

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
            ALTER TABLE movie_wanted_state RENAME TO movie_wanted_state_legacy;

            CREATE TABLE movie_wanted_state (
                movie_id TEXT NOT NULL,
                library_id TEXT NOT NULL,
                wanted_status TEXT NOT NULL,
                wanted_reason TEXT NOT NULL,
                minimum_availability TEXT NULL,
                has_file INTEGER NOT NULL DEFAULT 0,
                quality_cutoff_met INTEGER NOT NULL DEFAULT 0,
                missing_since_utc TEXT NULL,
                last_search_utc TEXT NULL,
                next_eligible_search_utc TEXT NULL,
                last_search_result TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (movie_id, library_id),
                FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
            );

            INSERT INTO movie_wanted_state (
                movie_id, library_id, wanted_status, wanted_reason, minimum_availability,
                has_file, quality_cutoff_met, missing_since_utc, last_search_utc,
                next_eligible_search_utc, last_search_result, updated_utc
            )
            SELECT
                movie_id, library_id, wanted_status, wanted_reason, minimum_availability,
                has_file, quality_cutoff_met, missing_since_utc, last_search_utc,
                next_eligible_search_utc, last_search_result, updated_utc
            FROM movie_wanted_state_legacy;

            DROP TABLE movie_wanted_state_legacy;

            CREATE INDEX IF NOT EXISTS ix_movie_wanted_state_library_status
                ON movie_wanted_state (library_id, wanted_status, next_eligible_search_utc);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
