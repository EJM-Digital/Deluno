using System.Data.Common;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>
/// #1260: rows written in the same instant used to be ordered by their random v7 GUID ids, so "the newest" or "the first" could be
/// the wrong one. Each test writes two rows in one instant, the older holding the HIGHER id, and asks a query that picks or lists
/// by time. The row written last is the newest.
/// </summary>
public sealed class SameInstantRowsAreOrderedByInsertionTests : IDisposable
{
    private const string HighId = "ffffffffffffffffffffffffffffffff";
    private const string LowId = "00000000000000000000000000000001";
    private const string Instant = "2026-09-30T12:00:00.0000000+00:00";

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
    private readonly TestStorage _storage = TestStorage.Create();

    public void Dispose() => _storage.Dispose();

    private async Task MigrateAsync() => await MigratedDatabaseTemplate.CopyIntoAsync(_storage.DataRoot);

    /// <summary>Inserts a row, filling every required column the test does not care about from its declared type.</summary>
    private async Task InsertAsync(string database, string table, Dictionary<string, object?> values)
    {
        await using var connection = await _storage.Factory.OpenConnectionAsync(database);
        using (var pragma = connection.CreateCommand())
        {
            // Only this connection: the rows here stand alone, without the catalogue entries they would point at.
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync();
        }

        var columns = new List<(string Name, string Type, bool Required, bool HasDefault)>();
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, !reader.IsDBNull(4)));
            }
        }

        var row = new Dictionary<string, object?>(values);
        foreach (var column in columns.Where(c => c.Required && !c.HasDefault && !row.ContainsKey(c.Name)))
        {
            row[column.Name] = column.Type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? 0 : string.Empty;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Keys.Select(k => "@" + k))});";
        foreach (var (key, value) in row)
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "@" + key;
            parameter.Value = value ?? DBNull.Value;
            insert.Parameters.Add(parameter);
        }

        await insert.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_latest_import_run_for_a_library_is_the_one_written_last()
    {
        await MigrateAsync();
        foreach (var (id, status) in new[] { (HighId, "completed"), (LowId, "failed") })
        {
            await InsertAsync(DelunoDatabaseNames.Platform, "library_import_runs", new()
            {
                ["id"] = id, ["library_id"] = "lib", ["library_name"] = "L", ["media_type"] = "movies", ["status"] = status,
                ["created_utc"] = Instant, ["updated_utc"] = Instant
            });
        }

        var latest = await new SqliteLibraryImportRunsRepository(_storage.Factory, Clock).GetLatestForLibraryAsync("lib", CancellationToken.None);

        Assert.Equal(LowId, latest!.Id);
    }

    [Fact]
    public async Task Import_issues_of_a_run_list_in_the_order_they_were_written()
    {
        await MigrateAsync();
        foreach (var (id, detail) in new[] { (HighId, "first"), (LowId, "second") })
        {
            await InsertAsync(DelunoDatabaseNames.Platform, "library_import_issues", new()
            {
                ["id"] = id, ["run_id"] = "run", ["library_id"] = "lib", ["source_path"] = detail, ["detail"] = detail, ["created_utc"] = Instant
            });
        }

        var issues = await new SqliteLibraryImportRunsRepository(_storage.Factory, Clock).ListIssuesAsync("run", 10, CancellationToken.None);

        Assert.Equal(["first", "second"], issues.Select(issue => issue.Detail).ToArray());
    }

    [Fact]
    public async Task Exclusions_created_in_one_instant_list_newest_written_first()
    {
        await MigrateAsync();
        foreach (var (id, title) in new[] { (LowId, "first"), (HighId, "second") })
        {
            await InsertAsync(DelunoDatabaseNames.Platform, "media_exclusions", new()
            {
                ["id"] = id, ["media_type"] = "movies", ["source_kind"] = "list", ["source_id"] = "s", ["entry_key"] = id, ["title"] = title,
                ["created_utc"] = Instant, ["updated_utc"] = Instant
            });
        }

        var listed = await new SqliteUnifiedExclusionRepository(_storage.Factory, Clock).ListActiveAsync(null, null, null, CancellationToken.None);

        Assert.Equal(["second", "first"], listed.Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task Retrying_failed_jobs_promotes_the_newest_written_of_the_same_work_not_the_higher_id()
    {
        await MigrateAsync();
        foreach (var id in new[] { HighId, LowId })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "job_queue", new()
            {
                ["id"] = id, ["job_type"] = "x", ["source"] = "s", ["status"] = "dead-letter", ["attempts"] = 1, ["dedupe_key"] = "same-work",
                ["created_utc"] = Instant, ["scheduled_utc"] = Instant, ["completed_utc"] = Instant
            });
        }

        var store = new SqliteJobStore(_storage.Factory, Clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        Assert.Equal(1, await store.RetryFailedAsync(CancellationToken.None));

        await using var connection = await _storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM job_queue WHERE status = 'queued';";
        Assert.Equal(LowId, (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_latest_preference_evaluation_is_the_one_written_last()
    {
        await MigrateAsync();
        var table = MediaTableMap.For(MediaKind.Movie).PreferenceEvaluationTable;
        foreach (var (id, plan) in new[] { (HighId, "plan-first"), (LowId, "plan-second") })
        {
            await InsertAsync(DelunoDatabaseNames.Movies, table, new()
            {
                ["id"] = id, ["media_id"] = "m1", ["library_id"] = "lib", ["file_identity"] = "f", ["plan_id"] = plan, ["plan_hash"] = plan,
                ["facts_json"] = "[]", ["evaluation_json"] = "{}", ["matched_rule_ids_json"] = "[]", ["evaluated_utc"] = Instant, ["created_utc"] = Instant, ["updated_utc"] = Instant
            });
        }

        var latest = await new SqliteMediaStateRepository(_storage.Factory, Clock)
            .GetLatestPreferenceEvaluationSnapshotAsync(MediaKind.Movie, "m1", "lib", "f", CancellationToken.None);

        Assert.Equal("plan-second", latest!.PlanId);
    }

    // ---- paged lists: the page token must walk rows written in one instant in the order they were written, losing and repeating none.

    private static async Task<List<T>> WalkAsync<T>(Func<string?, Task<Page<T>>> page)
    {
        var all = new List<T>();
        string? token = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var next = await page(token);
            all.AddRange(next.Items);
            if (next.NextPageToken is null)
            {
                return all;
            }

            token = next.NextPageToken;
        }

        throw new InvalidOperationException("The pages never ended.");
    }

    [Fact]
    public async Task Job_pages_walk_rows_of_one_instant_newest_written_first_with_none_lost_or_repeated()
    {
        await MigrateAsync();
        foreach (var id in new[] { HighId, "7" + HighId[1..], LowId })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "job_queue", new()
            {
                ["id"] = id, ["job_type"] = "x", ["source"] = "s", ["status"] = "queued", ["created_utc"] = Instant, ["scheduled_utc"] = Instant
            });
        }

        var store = new SqliteJobStore(_storage.Factory, Clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var jobs = await WalkAsync(token => store.ListPageAsync(new PageRequest(1, token), false, CancellationToken.None));

        Assert.Equal([LowId, "7" + HighId[1..], HighId], jobs.Select(job => job.Id).ToArray());
    }

    [Fact]
    public async Task Activity_pages_walk_rows_of_one_instant_newest_written_first_with_none_lost_or_repeated()
    {
        await MigrateAsync();
        foreach (var (id, message) in new[] { (HighId, "first"), ("7" + HighId[1..], "second"), (LowId, "third") })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "activity_events", new()
            {
                ["id"] = id, ["category"] = "c", ["message"] = message, ["created_utc"] = Instant
            });
        }

        var store = new SqliteJobStore(_storage.Factory, Clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var events = await WalkAsync(token => store.ListActivityPageAsync(new PageRequest(1, token), new ActivityQuery(), CancellationToken.None));

        Assert.Equal(["third", "second", "first"], events.Select(item => item.Message).ToArray());
    }

    [Fact]
    public async Task Search_cycle_run_pages_walk_rows_of_one_instant_newest_written_first_with_none_lost_or_repeated()
    {
        await MigrateAsync();
        foreach (var id in new[] { HighId, "7" + HighId[1..], LowId })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "search_cycle_runs", new()
            {
                ["id"] = id, ["library_id"] = "lib", ["started_utc"] = Instant
            });
        }

        var store = new SqliteJobStore(_storage.Factory, Clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var runs = await WalkAsync(token => store.ListSearchCycleRunsPageAsync(new PageRequest(1, token), null, CancellationToken.None));

        Assert.Equal([LowId, "7" + HighId[1..], HighId], runs.Select(run => run.Id).ToArray());
    }

    [Fact]
    public async Task Diagnostic_pages_walk_rows_of_one_instant_newest_written_first_with_none_lost_or_repeated()
    {
        await MigrateAsync();
        foreach (var (id, message) in new[] { (HighId, "first"), ("7" + HighId[1..], "second"), (LowId, "third") })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "activity_events", new()
            {
                ["id"] = id, ["category"] = "c", ["message"] = message, ["created_utc"] = Instant
            });
        }

        var service = new Deluno.Api.Monitoring.MonitoringService(
            null!, null!, null!, null!, null!, null!, _storage.Factory,
            Microsoft.Extensions.Options.Options.Create(new StoragePathOptions { DataRoot = _storage.DataRoot }),
            null!, null!, Clock, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<Deluno.Api.Monitoring.MonitoringService>.Instance);
        var items = await WalkAsync(token => service.SearchDiagnosticsAsync(
            new Deluno.Api.Monitoring.MonitoringDiagnosticsQuery(null, null, null, null, new PageRequest(1, token)), CancellationToken.None));

        Assert.Equal(["third", "second", "first"], items.Select(item => item.Message).ToArray());
    }

    [Fact]
    public async Task Download_dispatch_pages_walk_rows_of_one_instant_newest_written_first_with_none_lost_or_repeated()
    {
        await MigrateAsync();
        foreach (var (id, release) in new[] { (HighId, "first"), ("7" + HighId[1..], "second"), (LowId, "third") })
        {
            await InsertAsync(DelunoDatabaseNames.Jobs, "download_dispatches", new()
            {
                ["id"] = id, ["release_name"] = release, ["status"] = "queued", ["created_utc"] = Instant
            });
        }

        var repository = new SqliteDownloadDispatchesRepository(_storage.Factory, Clock);
        var seen = new List<string>();
        string? token = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var (items, next) = await repository.QueryDispatchesAsync(new DispatchQueryFilter(), new DispatchPaginationOptions { PageSize = 1, PageToken = token }, CancellationToken.None);
            seen.AddRange(items.Select(item => item.ReleaseName));
            if (next is null) break;
            token = next;
        }

        Assert.Equal(["third", "second", "first"], seen.ToArray());
    }
}
