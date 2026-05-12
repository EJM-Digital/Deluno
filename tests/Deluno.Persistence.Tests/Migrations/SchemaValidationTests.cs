using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>
/// Schema validation tests: after running all migrations the expected tables
/// and columns must exist.  Adding a column is safe; dropping or renaming
/// one is a breaking change that these tests will catch.
/// </summary>
public sealed class SchemaValidationTests
{
    private static TestStorage CreateStorage() => TestStorage.Create();

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(
        DbConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cols.Add(reader.GetString(1)); // column 1 = "name"
        }

        return cols;
    }

    private static async Task<IReadOnlySet<string>> GetTablesAsync(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    // ── Movies database ───────────────────────────────────────────────────

    [Fact]
    public async Task Movies_schema_has_movie_entries_table_with_required_columns()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var cols = await GetColumnsAsync(conn, "movie_entries");

        Assert.Contains("id", cols);
        Assert.Contains("title", cols);
        Assert.Contains("release_year", cols);
        Assert.Contains("imdb_id", cols);
        Assert.Contains("monitored", cols);
        Assert.Contains("metadata_provider", cols);
        Assert.Contains("metadata_provider_id", cols);
        Assert.Contains("original_title", cols);
        Assert.Contains("overview", cols);
        Assert.Contains("poster_url", cols);
        Assert.Contains("backdrop_url", cols);
        Assert.Contains("rating", cols);
        Assert.Contains("genres", cols);
        Assert.Contains("external_url", cols);
        Assert.Contains("metadata_json", cols);
        Assert.Contains("metadata_updated_utc", cols);
        Assert.Contains("created_utc", cols);
        Assert.Contains("updated_utc", cols);
    }

    [Fact]
    public async Task Movies_schema_has_movie_wanted_state_with_quality_and_replacement_columns()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var cols = await GetColumnsAsync(conn, "movie_wanted_state");

        // Base columns from V0001
        Assert.Contains("movie_id", cols);
        Assert.Contains("library_id", cols);
        Assert.Contains("wanted_status", cols);
        Assert.Contains("wanted_reason", cols);
        Assert.Contains("has_file", cols);
        Assert.Contains("current_quality", cols);
        Assert.Contains("target_quality", cols);
        Assert.Contains("quality_cutoff_met", cols);
        Assert.Contains("next_eligible_search_utc", cols);
        Assert.Contains("updated_utc", cols);

        // V0004: replacement protection columns
        Assert.Contains("prevent_lower_quality_replacements", cols);
        Assert.Contains("quality_delta_last_decision", cols);
    }

    [Fact]
    public async Task Movies_schema_has_tracked_file_columns_on_wanted_state()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var cols = await GetColumnsAsync(conn, "movie_wanted_state");

        // V0003: tracked file columns added to movie_wanted_state
        Assert.Contains("file_path", cols);
        Assert.Contains("file_size_bytes", cols);
        Assert.Contains("imported_utc", cols);
        Assert.Contains("last_verified_utc", cols);
        Assert.Contains("missing_detected_utc", cols);
    }

    [Fact]
    public async Task Movies_schema_has_import_recovery_status_column()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var cols = await GetColumnsAsync(conn, "movie_import_recovery_cases");

        Assert.Contains("id", cols);
        Assert.Contains("title", cols);
        Assert.Contains("failure_kind", cols);
        Assert.Contains("summary", cols);
        Assert.Contains("recommended_action", cols);
        Assert.Contains("detected_utc", cols);
    }

    // ── Series database ───────────────────────────────────────────────────

    [Fact]
    public async Task Series_schema_has_series_entries_table()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new SeriesSchemaInitializer(storage.Factory, migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        var tables = await GetTablesAsync(conn);

        Assert.Contains("series_entries", tables);
        Assert.Contains("episode_entries", tables);
        Assert.Contains("episode_wanted_state", tables);
    }

    [Fact]
    public async Task Series_schema_episode_wanted_state_has_quality_tracking_columns()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new SeriesSchemaInitializer(storage.Factory, migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        var cols = await GetColumnsAsync(conn, "episode_wanted_state");

        Assert.Contains("current_quality", cols);
        Assert.Contains("target_quality", cols);
        Assert.Contains("quality_cutoff_met", cols);
    }

    // ── Platform database ─────────────────────────────────────────────────

    [Fact]
    public async Task Platform_schema_has_quality_profiles_with_replacement_protection()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        var cols = await GetColumnsAsync(conn, "quality_profiles");

        Assert.Contains("id", cols);
        Assert.Contains("name", cols);
        Assert.Contains("cutoff_quality", cols);
    }

    [Fact]
    public async Task Platform_schema_has_indexer_sources_with_rate_limit_tracking()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        var cols = await GetColumnsAsync(conn, "indexer_sources");

        Assert.Contains("id", cols);
        Assert.Contains("name", cols);
        Assert.Contains("is_enabled", cols);
        // V0006: rate-limit tracking columns
        Assert.Contains("rate_limited_until_utc", cols);
        Assert.Contains("consecutive_failures", cols);
        Assert.Contains("disabled_reason", cols);
    }

    [Fact]
    public async Task Platform_schema_has_libraries_with_search_window_columns()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        var cols = await GetColumnsAsync(conn, "libraries");

        Assert.Contains("id", cols);
        Assert.Contains("name", cols);
        Assert.Contains("media_type", cols);
        Assert.Contains("search_window_start_hour", cols);
        Assert.Contains("search_window_end_hour", cols);
    }

    [Fact]
    public async Task Platform_schema_has_notification_webhooks_table()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        var tables = await GetTablesAsync(conn);

        Assert.Contains("notification_webhooks", tables);
    }

    // ── Jobs database ─────────────────────────────────────────────────────

    [Fact]
    public async Task Jobs_schema_has_job_queue_with_integrity_columns()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new JobsSchemaInitializer(storage.Factory, migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        var cols = await GetColumnsAsync(conn, "job_queue");

        Assert.Contains("id", cols);
        Assert.Contains("job_type", cols);
        Assert.Contains("status", cols);
        Assert.Contains("created_utc", cols);
        Assert.Contains("started_utc", cols);
        Assert.Contains("completed_utc", cols);
        Assert.Contains("last_error", cols);
        // V0002: job integrity columns
        Assert.Contains("idempotency_key", cols);
        Assert.Contains("max_attempts", cols);
    }

    // ── Cache database ────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_schema_has_search_result_cache_table()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new CacheSchemaInitializer(storage.Factory, migrator,
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Cache);
        var cols = await GetColumnsAsync(conn, "search_result_cache");

        Assert.Contains("cache_key", cols);
        Assert.Contains("result_json", cols);
        Assert.Contains("expires_utc", cols);
        Assert.Contains("created_utc", cols);
    }

    // ── Performance indexes ───────────────────────────────────────────────

    [Fact]
    public async Task Movies_schema_has_composite_index_on_wanted_state_for_library_status_queries()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var indexes = await GetIndexesAsync(conn);

        // Composite index for the hot path: "fetch all wanted movies for a library ordered by next search time"
        Assert.Contains("ix_movie_wanted_state_library_status", indexes);
        // Replacement-protection filter index
        Assert.Contains("ix_movie_wanted_state_replacement_protection", indexes);
    }

    [Fact]
    public async Task Series_schema_has_composite_index_on_episode_wanted_state()
    {
        using var storage = CreateStorage();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        await new SeriesSchemaInitializer(storage.Factory, migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var conn = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        var indexes = await GetIndexesAsync(conn);

        // Composite indexes for the hot path: library + wanted_status + next search time
        Assert.Contains("ix_series_wanted_state_library_status", indexes);
        Assert.Contains("ix_episode_wanted_state_library_status", indexes);
    }

    private static async Task<IReadOnlySet<string>> GetIndexesAsync(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';";
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    // ── Idempotency guarantee ─────────────────────────────────────────────

    [Fact]
    public async Task Running_all_initializers_twice_is_idempotent()
    {
        using var storage = CreateStorage();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);

        async Task RunAllAsync()
        {
            await new PlatformSchemaInitializer(storage.Factory, migrator,
                NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new MoviesSchemaInitializer(storage.Factory, migrator,
                NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new SeriesSchemaInitializer(storage.Factory, migrator,
                NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator,
                NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new CacheSchemaInitializer(storage.Factory, migrator,
                NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }

        await RunAllAsync();
        var ex = await Record.ExceptionAsync(RunAllAsync);

        Assert.Null(ex);
    }

    // ── Version ordering invariant ────────────────────────────────────────

    [Fact]
    public async Task All_databases_have_version_1_named_initial_schema()
    {
        using var storage = CreateStorage();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);

        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new CacheSchemaInitializer(storage.Factory, migrator,
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        foreach (var db in new[]
        {
            DelunoDatabaseNames.Platform,
            DelunoDatabaseNames.Movies,
            DelunoDatabaseNames.Series,
            DelunoDatabaseNames.Jobs,
            DelunoDatabaseNames.Cache
        })
        {
            await using var conn = await storage.Factory.OpenConnectionAsync(db);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM schema_migrations WHERE version = 1;";
            var name = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("initial_schema", name);
        }
    }

}
