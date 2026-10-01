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
    public async Task ApplyAsync_accepts_a_database_that_ran_a_superseded_definition_and_restamps_it()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        var corrected = new CorrectedProbeTableMigration();
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [corrected], CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        Assert.Equal(corrected.Checksum, await ReadScalarAsync<string>(connection, "SELECT checksum FROM schema_migrations WHERE version = 1;"));
        Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT apply_count FROM migration_probe WHERE id = 1;"));
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
            Assert.Equal(2, await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal("initial_schema", await ReadScalarAsync<string>(connection, "SELECT name FROM schema_migrations WHERE version = 1;"));
            Assert.Equal("artwork_cache_index", await ReadScalarAsync<string>(connection, "SELECT name FROM schema_migrations WHERE version = 2;"));
        }

        await using var moviesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        await AssertVersionsAreContiguousAsync(moviesConnection);
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
        Assert.Equal("movie_quality_profile_override", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 13;"));
        Assert.Equal("movie_wanted_status_vocabulary", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 14;"));
        Assert.Equal("movie_subtitle_state", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 15;"));
        Assert.Equal("movie_sortable_file_facts", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 16;"));
        Assert.Equal("movie_subtitle_attempts", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 17;"));
        Assert.Equal("movie_subtitle_match", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 18;"));
        Assert.Equal("movie_preference_evaluations", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 33;"));
        Assert.Equal("movie_subtitle_failure_details", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 34;"));
        Assert.Equal("movie_release_name_evidence", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 36;"));

        await using var seriesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        await AssertVersionsAreContiguousAsync(seriesConnection);
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
        Assert.Equal("series_quality_profile_override", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 13;"));
        Assert.Equal("series_episode_progress_index", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 14;"));
        Assert.Equal("series_wanted_status_vocabulary", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 15;"));
        Assert.Equal("series_subtitle_state", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 16;"));
        Assert.Equal("series_sortable_file_facts", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 17;"));
        Assert.Equal("series_subtitle_attempts", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 18;"));
        Assert.Equal("series_subtitle_match", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 19;"));
        Assert.Equal("series_progress_facts", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 20;"));
        Assert.Equal("series_tv_sort_indexes", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 32;"));
        Assert.Equal("media_tags", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 33;"));
        Assert.Equal("series_preference_evaluations", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 34;"));
        Assert.Equal("series_numbering", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 35;"));
        Assert.Equal("series_subtitle_failure_details", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 36;"));
        Assert.Equal("series_release_name_evidence", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 39;"));

        await using var platformConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        await AssertVersionsAreContiguousAsync(platformConnection);
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
        Assert.Equal("indexer_sharing_policy", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 25;"));
        Assert.Equal("release_preference_plans", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 34;"));
        Assert.Equal("automation_idempotency", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 35;"));
        Assert.Equal("notification_webhook_deliveries", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 36;"));
        Assert.Equal("integration_failure_details", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 37;"));
        Assert.Equal("media_plan_versions", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 38;"));
        Assert.Equal("playback_goals", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 39;"));
        Assert.Equal("migration_backup_evidence", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 40;"));
        Assert.Equal("guide_package_versions", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 41;"));
        Assert.Equal("subtitle_provider_failure_details", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 42;"));
        Assert.Equal("playback_goal_forbidden_traits", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 43;"));
        Assert.Equal("media_plan_automation_intent", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 44;"));
        Assert.Equal("quality_profile_plan_reference", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 45;"));
        Assert.Equal("notification_webhook_failure_details", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 46;"));

        await using var jobsConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(43, await ReadScalarAsync<int>(jobsConnection, "SELECT COUNT(*) FROM schema_migrations;"));
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
        Assert.Equal("throughput_upload_samples", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 14;"));
        Assert.Equal("machine_telemetry_samples", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 15;"));
        Assert.Equal("dispatch_import_status_vocabulary", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 16;"));
        // 17 was counted but never named, so the count was the only thing
        // holding it — and a count on its own cannot tell a renamed migration
        // from a new one.
        Assert.Equal("library_search_per_media_type_job_types", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 17;"));
        Assert.Equal("library_subtitle_schedule", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 18;"));
        Assert.Equal("indexer_query_stats", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 19;"));
        Assert.Equal("system_task_run_state", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 20;"));
        Assert.Equal("indexer_query_failure_details", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 21;"));
        Assert.Equal("download_dispatch_import_failure_details", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 22;"));
        Assert.Equal("download_dispatch_grab_failure_details", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 23;"));
        Assert.Equal("download_dispatch_replacement_authority", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 24;"));
        Assert.Equal("download_dispatch_replacement_manifest", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 25;"));
        Assert.Equal("blocked_releases", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 26;"));
        Assert.Equal("blocked_release_cleanup", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 27;"));
        Assert.Equal("import_failure_rules", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 28;"));
        Assert.Equal("proposed_refusals", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 29;"));
        Assert.Equal("schedule_interval_used", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 30;"));
        Assert.Equal("dispatch_alert_resolution", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 31;"));
        Assert.Equal("dispatch_client_completed", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 32;"));
        Assert.Equal("orphan_grab_retry_jobs", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 33;"));
        Assert.Equal("integration_circuit_escalation_level", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 34;"));
        Assert.Equal("blocked_release_origin", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 35;"));
        Assert.Equal("decision_language_tracking", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 36;"));
        Assert.Equal("dispatch_indexer_id", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 37;"));
        Assert.Equal("dispatch_client_item_index_and_grab_reservations", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 38;"));
        Assert.Equal("screen_query_indexes", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 39;"));
        Assert.Equal("trim_oversized_grab_notes", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 40;"));
        Assert.Equal("collapse_repeated_activity", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 41;"));
        Assert.Equal("turned_down_releases", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 42;"));
        Assert.Equal("dispatch_started_by", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 43;"));
    }

    /// <summary>
    /// #554. Only the client's completion webhook ever wrote detected_utc, so on
    /// a database from before V0032 each one is a completion and carries across.
    /// </summary>
    [Fact]
    public async Task Existing_download_records_keep_their_detection_as_the_completion()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z")));
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Jobs,
            Deluno.Jobs.Migrations.JobsDatabaseMigrations.All.Where(migration => migration.Version < 32).ToArray(),
            CancellationToken.None);

        await using (var before = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await using var insert = before.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO download_dispatches (
                    id, library_id, media_type, entity_type, entity_id, release_name, indexer_name,
                    download_client_id, download_client_name, status, created_utc, grab_status, grab_attempted_utc, detected_utc
                ) VALUES
                    ('hooked', 'movies', 'movies', 'movie', 'movie-1', 'A.2010.1080p', 'Fixture', 'client', 'qBittorrent', 'sent',
                     '2026-09-20T00:00:00Z', 'succeeded', '2026-09-20T00:00:00Z', '2026-09-20T01:00:00Z'),
                    ('unseen', 'movies', 'movies', 'movie', 'movie-2', 'B.2010.1080p', 'Fixture', 'client', 'qBittorrent', 'sent',
                     '2026-09-20T00:00:00Z', 'succeeded', '2026-09-20T00:00:00Z', NULL);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, Deluno.Jobs.Migrations.JobsDatabaseMigrations.All, CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal("2026-09-20T01:00:00Z", await ReadScalarAsync<string>(after, "SELECT client_completed_utc FROM download_dispatches WHERE id = 'hooked';"));
        Assert.Equal(0, await ReadScalarAsync<int>(after, "SELECT COUNT(*) FROM download_dispatches WHERE id = 'unseen' AND client_completed_utc IS NOT NULL;"));
    }

    /// <summary>
    /// #620: a grab retry job nothing would ever run is cleared, and every other
    /// job stays where it was.
    /// </summary>
    [Fact]
    public async Task Queued_grab_retries_no_lane_would_run_are_cleared()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-24T00:00:00Z")));
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Jobs,
            Deluno.Jobs.Migrations.JobsDatabaseMigrations.All.Where(migration => migration.Version < 33).ToArray(),
            CancellationToken.None);

        await using (var before = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            await using var insert = before.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO job_queue (id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc, dedupe_key)
                VALUES
                    ('orphan', 'download.grab.retry', 'download-client', 'queued', '{"downloadUrl":"https://indexer.invalid/get?apikey=secret"}', 0,
                     '2026-09-20T00:00:00Z', '2026-09-20T00:30:00Z', 'download.grab.retry:dispatch-1'),
                    ('search', 'movies.search', 'schedule', 'queued', NULL, 0,
                     '2026-09-20T00:00:00Z', '2026-09-20T00:00:00Z', NULL);

                INSERT INTO download_dispatches (
                    id, library_id, media_type, entity_type, entity_id, release_name, indexer_name,
                    download_client_id, download_client_name, status, created_utc, grab_status, grab_failure_json
                ) VALUES
                    ('timed-out', 'movies', 'movies', 'movie', 'movie-1', 'A.2010.1080p', 'Fixture', 'client', 'Deluge', 'failed',
                     '2026-09-20T00:00:00Z', 'failed',
                     '{"Kind":"Timeout","RetryState":"RetryScheduled","RetryAfterUtc":"2026-09-20T00:30:00+00:00","Message":"No answer."}'),
                    ('slowed', 'movies', 'movies', 'movie', 'movie-2', 'B.2010.1080p', 'Fixture', 'client', 'Deluge', 'failed',
                     '2026-09-20T00:00:00Z', 'failed',
                     '{"Kind":"RateLimit","RetryState":"RetryScheduled","RetryAfterUtc":"2026-09-20T00:10:00+00:00","Message":"Slow down."}'),
                    ('refused', 'movies', 'movies', 'movie', 'movie-3', 'C.2010.1080p', 'Fixture', 'client', 'Deluge', 'failed',
                     '2026-09-20T00:00:00Z', 'failed',
                     '{"Kind":"Authentication","RetryState":"ManualAction","RetryAfterUtc":null,"Message":"Wrong password."}');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, Deluno.Jobs.Migrations.JobsDatabaseMigrations.All, CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(0, await ReadScalarAsync<int>(after, "SELECT COUNT(*) FROM job_queue WHERE job_type = 'download.grab.retry';"));
        Assert.Equal(1, await ReadScalarAsync<int>(after, "SELECT COUNT(*) FROM job_queue WHERE id = 'search';"));

        // A grab that already failed stops saying a retry is coming.
        Assert.Equal("NotRetryable", await ReadScalarAsync<string>(after, "SELECT json_extract(grab_failure_json, '$.RetryState') FROM download_dispatches WHERE id = 'timed-out';"));
        Assert.Equal(1, await ReadScalarAsync<int>(after, "SELECT COUNT(*) FROM download_dispatches WHERE id = 'timed-out' AND json_extract(grab_failure_json, '$.RetryAfterUtc') IS NULL;"));
        Assert.Equal("NotRetryable", await ReadScalarAsync<string>(after, "SELECT json_extract(grab_failure_json, '$.RetryState') FROM download_dispatches WHERE id = 'slowed';"));
        Assert.Equal("2026-09-20T00:10:00+00:00", await ReadScalarAsync<string>(after, "SELECT json_extract(grab_failure_json, '$.RetryAfterUtc') FROM download_dispatches WHERE id = 'slowed';"));
        Assert.Equal("ManualAction", await ReadScalarAsync<string>(after, "SELECT json_extract(grab_failure_json, '$.RetryState') FROM download_dispatches WHERE id = 'refused';"));
    }

    /// <summary>
    /// #732: a circuit already open (or already recorded) on a database from
    /// before this migration gets the new column with its escalation reset to
    /// zero -- the same starting point as a circuit that has never opened --
    /// rather than failing to migrate or losing the row.
    /// </summary>
    [Fact]
    public async Task Existing_circuit_breaker_rows_gain_an_escalation_level_without_a_rebuild()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Jobs,
            Deluno.Jobs.Migrations.JobsDatabaseMigrations.All.Where(migration => migration.Version < 34).ToArray(),
            CancellationToken.None);

        await using (var before = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs))
        {
            Assert.Equal(0, await ReadScalarAsync<int>(before,
                "SELECT COUNT(*) FROM pragma_table_info('integration_circuit_states') WHERE name = 'escalation_level';"));

            await using var insert = before.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO integration_circuit_states (integration_key, circuit_open_until_utc, opened_utc, failure_count)
                VALUES ('indexer:pre-migration:search', '2026-09-25T00:05:00Z', '2026-09-25T00:00:00Z', 3);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.ApplyAsync(DelunoDatabaseNames.Jobs, Deluno.Jobs.Migrations.JobsDatabaseMigrations.All, CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(1, await ReadScalarAsync<int>(after,
            "SELECT COUNT(*) FROM pragma_table_info('integration_circuit_states') WHERE name = 'escalation_level';"));
        Assert.Equal(0, await ReadScalarAsync<int>(after,
            "SELECT escalation_level FROM integration_circuit_states WHERE integration_key = 'indexer:pre-migration:search';"));
    }

    [Fact]
    public async Task Existing_movie_and_series_databases_gain_release_name_evidence_without_a_rebuild()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-15T00:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Movies,
            Deluno.Movies.Migrations.MoviesDatabaseMigrations.All
                .Where(migration => migration.Version < 36)
                .ToArray(),
            CancellationToken.None);
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Series,
            Deluno.Series.Migrations.SeriesDatabaseMigrations.All
                .Where(migration => migration.Version < 39)
                .ToArray(),
            CancellationToken.None);

        await using (var moviesBefore = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies))
        {
            Assert.Equal(0, await ReadScalarAsync<int>(moviesBefore,
                "SELECT COUNT(*) FROM pragma_table_info('media_preference_evaluations') WHERE name = 'release_name_evidence';"));
        }
        await using (var seriesBefore = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series))
        {
            Assert.Equal(0, await ReadScalarAsync<int>(seriesBefore,
                "SELECT COUNT(*) FROM pragma_table_info('media_preference_evaluations') WHERE name = 'release_name_evidence';"));
        }

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Movies,
            Deluno.Movies.Migrations.MoviesDatabaseMigrations.All,
            CancellationToken.None);
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Series,
            Deluno.Series.Migrations.SeriesDatabaseMigrations.All,
            CancellationToken.None);

        await using var moviesAfter = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        await using var seriesAfter = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        Assert.Equal(1, await ReadScalarAsync<int>(moviesAfter,
            "SELECT COUNT(*) FROM pragma_table_info('media_preference_evaluations') WHERE name = 'release_name_evidence';"));
        Assert.Equal(1, await ReadScalarAsync<int>(seriesAfter,
            "SELECT COUNT(*) FROM pragma_table_info('media_preference_evaluations') WHERE name = 'release_name_evidence';"));
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

    /// <summary>
    /// Every version from 1 to the highest, exactly once.
    ///
    /// <para>This replaced three hand-maintained counts — <c>Assert.Equal(23,
    /// …)</c> — which had to be edited by whoever added a migration and said
    /// nothing about whether the numbering was sound. It is also strictly
    /// stronger: a duplicated version number, or a gap left by a migration
    /// registered out of order, keeps the count right and breaks the schema.
    /// The per-version name assertions below stay, because those pin something
    /// a count cannot — that version 12 never quietly becomes a different
    /// migration on a database that has already run it.</para>
    /// </summary>
    private static async Task AssertVersionsAreContiguousAsync(System.Data.Common.DbConnection connection)
    {
        var count = await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;");
        var highest = await ReadScalarAsync<int>(connection, "SELECT MAX(version) FROM schema_migrations;");
        var distinct = await ReadScalarAsync<int>(connection, "SELECT COUNT(DISTINCT version) FROM schema_migrations;");

        Assert.Equal(count, distinct);
        Assert.Equal(count, highest);
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

    private sealed class CorrectedProbeTableMigration : SqliteSqlMigration
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
            VALUES (1, 2);
            """;

        protected override IEnumerable<string> SupersededSql =>
        [
            """
            CREATE TABLE migration_probe (
                id INTEGER PRIMARY KEY,
                apply_count INTEGER NOT NULL
            );

            INSERT INTO migration_probe (id, apply_count)
            VALUES (1, 1);
            """
        ];
    }
}
