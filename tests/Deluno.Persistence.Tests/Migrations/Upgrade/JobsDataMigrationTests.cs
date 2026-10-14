using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. The jobs database's migrations that rewrite or delete data. V0040, V0041 and V0050 are proved in
/// <c>SearchHistoryAndActivityRepeatTests</c> and <c>HistoryHoldsEarlierTurnedDownTests</c>.
/// </summary>
public sealed class JobsDataMigrationTests
{
    private const string Db = DelunoDatabaseNames.Jobs;

    [Fact]
    public async Task V0002_lets_a_queued_or_failed_job_run_when_it_was_scheduled_and_leaves_finished_jobs_without_a_next_attempt()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 2);
        foreach (var (id, status) in new[] { ("j-queued", "queued"), ("j-failed", "failed"), ("j-running", "running"), ("j-done", "completed") })
        {
            await upgrade.InsertAsync("job_queue", ("id", id), ("job_type", "library.search"), ("status", status), ("attempts", 2), ("scheduled_utc", $"2026-05-01T0{id.Length % 9}:00:00.0000000+00:00"));
        }

        var scheduled = (await upgrade.RowsAsync("SELECT id, scheduled_utc FROM job_queue;")).ToDictionary(row => (string)row["id"]!, row => (string)row["scheduled_utc"]!);

        await upgrade.MigrateAsync();

        var jobs = (await upgrade.RowsAsync("SELECT id, status, attempts, max_attempts, next_attempt_utc, idempotency_key, dedupe_key FROM job_queue;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(scheduled["j-queued"], jobs["j-queued"]["next_attempt_utc"]);
        Assert.Equal(scheduled["j-failed"], jobs["j-failed"]["next_attempt_utc"]);
        Assert.Null(jobs["j-running"]["next_attempt_utc"]);
        Assert.Null(jobs["j-done"]["next_attempt_utc"]);
        Assert.All(jobs.Values, job => Assert.Equal(3L, job["max_attempts"]));
        Assert.All(jobs.Values, job => Assert.Equal(2L, job["attempts"]));
        Assert.All(jobs.Values, job => Assert.Null(job["dedupe_key"]));
        Assert.Equal("completed", jobs["j-done"]["status"]);
    }

    [Fact]
    public async Task V0016_renames_the_completed_import_status_to_imported_and_leaves_the_others()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 16);
        foreach (var (id, status) in new[] { ("d-completed", "completed"), ("d-imported", "imported"), ("d-failed", "failed"), ("d-none", null) })
        {
            await upgrade.InsertAsync("download_dispatches", ("id", id), ("release_name", $"Release {id}"), ("import_status", status));
        }

        await upgrade.MigrateAsync();

        var dispatches = (await upgrade.RowsAsync("SELECT id, release_name, import_status FROM download_dispatches;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal("imported", dispatches["d-completed"]["import_status"]);
        Assert.Equal("imported", dispatches["d-imported"]["import_status"]);
        Assert.Equal("failed", dispatches["d-failed"]["import_status"]);
        Assert.Null(dispatches["d-none"]["import_status"]);
        Assert.Equal("Release d-completed", dispatches["d-completed"]["release_name"]);
    }

    [Theory]
    [InlineData("{\"mediaType\":\"tv\"}", "library.search.tv")]
    [InlineData("{\"mediaType\":\"Series\"}", "library.search.tv")]
    [InlineData("{\"mediaType\":\"television\"}", "library.search.tv")]
    [InlineData("{\"mediaType\":\"movies\"}", "library.search.movies")]
    [InlineData("{\"mediaType\":\"film\"}", "library.search.movies")]
    [InlineData("{\"libraryId\":\"l1\"}", "library.search.movies")]
    public async Task V0017_files_a_waiting_library_search_under_the_media_type_its_payload_names(string payload, string expectedType)
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 17);
        foreach (var status in new[] { "queued", "failed", "running" })
        {
            await upgrade.InsertAsync("job_queue", ("id", $"job-{status}"), ("job_type", "library.search"), ("status", status), ("payload_json", payload));
        }

        await upgrade.InsertAsync("job_queue", ("id", "job-completed"), ("job_type", "library.search"), ("status", "completed"), ("payload_json", payload));
        await upgrade.InsertAsync("job_queue", ("id", "job-other"), ("job_type", "metadata.refresh"), ("status", "queued"), ("payload_json", payload));

        await upgrade.MigrateAsync();

        var jobs = (await upgrade.RowsAsync("SELECT id, job_type, status, payload_json FROM job_queue;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal(expectedType, jobs["job-queued"]["job_type"]);
        Assert.Equal(expectedType, jobs["job-failed"]["job_type"]);
        Assert.Equal(expectedType, jobs["job-running"]["job_type"]);
        Assert.Equal("library.search", jobs["job-completed"]["job_type"]);
        Assert.Equal("metadata.refresh", jobs["job-other"]["job_type"]);
        Assert.All(jobs.Values, job => Assert.Equal(payload, job["payload_json"]));
    }

    [Fact]
    public async Task V0032_takes_the_time_the_client_was_seen_to_finish_from_the_detected_time_where_there_is_one()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 32);
        await upgrade.InsertAsync("download_dispatches", ("id", "seen"), ("detected_utc", "2026-06-01T10:00:00.0000000+00:00"), ("status", "completed"));
        await upgrade.InsertAsync("download_dispatches", ("id", "unseen"), ("status", "grabbed"));

        await upgrade.MigrateAsync();

        var dispatches = (await upgrade.RowsAsync("SELECT id, status, detected_utc, client_completed_utc FROM download_dispatches;")).ToDictionary(row => (string)row["id"]!);
        Assert.Equal("2026-06-01T10:00:00.0000000+00:00", dispatches["seen"]["client_completed_utc"]);
        Assert.Equal("2026-06-01T10:00:00.0000000+00:00", dispatches["seen"]["detected_utc"]);
        Assert.Null(dispatches["unseen"]["client_completed_utc"]);
        Assert.Equal("grabbed", dispatches["unseen"]["status"]);
    }

    [Fact]
    public async Task V0033_deletes_the_grab_retry_jobs_nothing_runs_any_more_and_stops_the_retries_it_had_promised()
    {
        await using var upgrade = await UpgradeCase.BeforeAsync(Db, 33);
        await upgrade.InsertAsync("job_queue", ("id", "retry-1"), ("job_type", "download.grab.retry"), ("status", "queued"));
        await upgrade.InsertAsync("job_queue", ("id", "retry-2"), ("job_type", "download.grab.retry"), ("status", "failed"));
        await upgrade.InsertAsync("job_queue", ("id", "search"), ("job_type", "library.search.movies"), ("status", "queued"));

        const string after = "2026-07-01T00:00:00.0000000+00:00";
        await upgrade.InsertAsync("download_dispatches", ("id", "rate-limited"), ("grab_failure_json", $"{{\"Kind\":\"RateLimit\",\"RetryState\":\"RetryScheduled\",\"RetryAfterUtc\":\"{after}\"}}"));
        await upgrade.InsertAsync("download_dispatches", ("id", "retrying"), ("grab_failure_json", $"{{\"Kind\":\"Timeout\",\"RetryState\":\"Retrying\",\"RetryAfterUtc\":\"{after}\"}}"));
        await upgrade.InsertAsync("download_dispatches", ("id", "final"), ("grab_failure_json", $"{{\"Kind\":\"Timeout\",\"RetryState\":\"NotRetryable\",\"RetryAfterUtc\":\"{after}\"}}"));
        await upgrade.InsertAsync("download_dispatches", ("id", "garbled"), ("grab_failure_json", "not json at all"));
        await upgrade.InsertAsync("download_dispatches", ("id", "none"));

        await upgrade.MigrateAsync();

        Assert.Equal(["search"], (await upgrade.RowsAsync("SELECT id FROM job_queue;")).Select(row => (string)row["id"]!));

        string Field(string id, string path) => upgrade.TextAsync($"SELECT json_extract(grab_failure_json, '{path}') FROM download_dispatches WHERE id = '{id}';").Result ?? string.Empty;
        Assert.Equal("NotRetryable", Field("rate-limited", "$.RetryState"));
        Assert.Equal(after, Field("rate-limited", "$.RetryAfterUtc"));
        Assert.Equal("NotRetryable", Field("retrying", "$.RetryState"));
        Assert.Equal("null", await upgrade.TextAsync("SELECT json_type(grab_failure_json, '$.RetryAfterUtc') FROM download_dispatches WHERE id = 'retrying';"));
        Assert.Equal("Timeout", Field("retrying", "$.Kind"));
        Assert.Equal(after, Field("final", "$.RetryAfterUtc"));
        Assert.Equal("not json at all", await upgrade.TextAsync("SELECT grab_failure_json FROM download_dispatches WHERE id = 'garbled';"));
        Assert.Null(await upgrade.ScalarAsync("SELECT grab_failure_json FROM download_dispatches WHERE id = 'none';"));
    }
}
