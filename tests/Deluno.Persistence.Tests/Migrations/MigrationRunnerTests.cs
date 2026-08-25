using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Migrations;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task ApplyAsync_applies_pending_migrations_once_and_records_history()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);

        Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT apply_count FROM migration_probe WHERE id = 1;"));
    }

    [Fact]
    public async Task ApplyAsync_rejects_previously_applied_migration_when_definition_changes()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migrator.ApplyAsync(
                DelunoDatabaseNames.Platform,
                [new ChangedProbeTableMigration()],
                CancellationToken.None));

        Assert.Contains("different definition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_initializers_record_initial_migration_for_each_database()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);

        await new PlatformSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new CacheSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        foreach (var databaseName in new[]
                 {
                     DelunoDatabaseNames.Cache
                 })
        {
            await using var connection = await storage.Factory.OpenConnectionAsync(databaseName);
            Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal("initial_schema", await ReadScalarAsync<string>(connection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        }

        await using var moviesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        Assert.Equal(12, await ReadScalarAsync<int>(moviesConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("movie_idempotency_indexes", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));
        Assert.Equal("movie_tracked_files", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 3;"));
        Assert.Equal("movie_quality_and_replacement", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 4;"));
        Assert.Equal("movie_import_recovery_status", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 5;"));
        Assert.Equal("movie_skip_next_automation_search", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 6;"));
        Assert.Equal("movie_release_dates", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 7;"));
        Assert.Equal("movie_catalogue_list_index", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.Equal("movie_metadata_attempt_tracking", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("movie_metadata_refresh_requests", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("movie_catalogue_sort_indexes", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 11;"));
        Assert.Equal("movie_media_facts", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 12;"));

        await using var seriesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        Assert.Equal(12, await ReadScalarAsync<int>(seriesConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("series_idempotency_indexes", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));
        Assert.Equal("series_tracked_files", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 3;"));
        Assert.Equal("series_episode_quality_tracking", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 4;"));
        Assert.Equal("series_import_recovery_status", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 5;"));
        Assert.Equal("series_skip_next_automation_search", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 6;"));
        Assert.Equal("series_episode_catalogue", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 7;"));
        Assert.Equal("series_catalogue_list_index", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.Equal("series_metadata_attempt_tracking", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("series_metadata_refresh_requests", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("series_catalogue_sort_indexes", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 11;"));
        Assert.Equal("series_media_facts", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 12;"));

        await using var platformConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        Assert.Equal(24, await ReadScalarAsync<int>(platformConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("user_security_stamp", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));
        Assert.Equal("integration_health", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 3;"));
        Assert.Equal("quality_profile_replacement_protection", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 4;"));
        Assert.Equal("quality_profile_preset_tracking", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 5;"));
        Assert.Equal("indexer_rate_limit_tracking", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 6;"));
        Assert.Equal("library_search_windows", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 7;"));
        Assert.Equal("notification_webhooks", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.Equal("custom_format_trash_ids", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("intake_source_sync_config", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("migration_audit_reports", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 11;"));
        Assert.Equal("processor_handoffs", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 12;"));
        Assert.Equal("remove_legacy_demo_profiles", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 13;"));
        Assert.Equal("intake_list_exclusions", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 14;"));
        Assert.Equal("processor_connections", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 15;"));
        Assert.Equal("intake_title_origins", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 16;"));
        Assert.Equal("download_client_path_mappings", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 17;"));
        Assert.Equal("library_media_plans", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 18;"));
        Assert.Equal("library_import_runs", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 19;"));
        Assert.Equal("indexer_request_interval", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 20;"));
        Assert.Equal("repair_quality_profile_tier_names", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 21;"));
        Assert.Equal("library_download_client_categories", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 22;"));
        Assert.Equal("library_view_library_filter", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 23;"));
        Assert.Equal("library_workflow_cleanup", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 24;"));

        await using var jobsConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(13, await ReadScalarAsync<int>(jobsConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("job_integrity", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));
        Assert.Equal("download_outcome_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 3;"));
        Assert.Equal("import_resolutions", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 4;"));
        Assert.Equal("dispatch_alerts", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 5;"));
        Assert.Equal("download_retry_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 6;"));
        Assert.Equal("integration_circuit_state", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 7;"));
        Assert.Equal("download_retry_window_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.Equal("decision_telemetry_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("archived_dispatch_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("worker_schedule_state", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 11;"));
        Assert.Equal("independent_library_search_schedules", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 12;"));
        Assert.Equal("download_throughput_samples", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 13;"));
    }

    [Fact]
    public async Task Quality_profile_tier_repair_maps_aliases_deduplicates_in_order_and_leaves_clean_profiles_untouched()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        var migrationsBeforeRepair = PlatformDatabaseMigrations.All
            .Where(migration => migration.Version < 21)
            .ToArray();

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            migrationsBeforeRepair,
            CancellationToken.None);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO quality_profiles (
                    id, name, media_type, sort_order, cutoff_quality, allowed_qualities,
                    custom_format_ids, upgrade_until_cutoff, upgrade_unknown_items,
                    created_utc, updated_utc
                ) VALUES
                    (
                        'repair-me', 'Repair me', 'movies', 0, 'WEB-DL 1080p',
                        'webdl-1080p, WEB-DL 1080p, Bluray 1080p, WEB 1080p, Bluray 1080p, Remux 4K, Remux 2160p',
                        '', 1, 0, '2026-04-29T05:00:00Z', '2026-04-29T05:00:00Z'
                    ),
                    (
                        'leave-me', 'Leave me', 'movies', 1, 'WEB 1080p',
                        'WEB 1080p, Bluray 1080p',
                        '', 1, 0, '2026-04-29T05:00:00Z', '2026-04-29T05:00:00Z'
                    );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            PlatformDatabaseMigrations.All,
            CancellationToken.None);

        await using var repairedConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var repairedCommand = repairedConnection.CreateCommand();
        repairedCommand.CommandText =
            """
            SELECT allowed_qualities, cutoff_quality
            FROM quality_profiles
            WHERE id = @id;
            """;
        var idParameter = repairedCommand.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = "repair-me";
        repairedCommand.Parameters.Add(idParameter);
        using (var repairedReader = await repairedCommand.ExecuteReaderAsync())
        {
            Assert.True(await repairedReader.ReadAsync());
            Assert.Equal("WEB 1080p, Bluray 1080p, Remux 2160p", repairedReader.GetString(0));
            Assert.Equal("WEB 1080p", repairedReader.GetString(1));
        }

        repairedCommand.Parameters["@id"].Value = "leave-me";
        using var cleanReader = await repairedCommand.ExecuteReaderAsync();
        Assert.True(await cleanReader.ReadAsync());
        Assert.Equal("WEB 1080p, Bluray 1080p", cleanReader.GetString(0));
        Assert.Equal("WEB 1080p", cleanReader.GetString(1));
    }

    private static async Task<T> ReadScalarAsync<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private sealed class CreateProbeTableMigration : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "create_probe";

        protected override string Sql =>
            """
            CREATE TABLE migration_probe (
                id INTEGER PRIMARY KEY,
                apply_count INTEGER NOT NULL
            );

            INSERT INTO migration_probe (id, apply_count)
            VALUES (1, 1);
            """;
    }

    private sealed class ChangedProbeTableMigration : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "create_probe";

        protected override string Sql =>
            """
            CREATE TABLE migration_probe_changed (
                id INTEGER PRIMARY KEY
            );
            """;
    }
}
