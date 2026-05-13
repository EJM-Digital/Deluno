using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class JobStoreTests
{
    [Fact]
    public async Task Enqueue_lease_complete_and_retry_failed_jobs_preserve_expected_lifecycle()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var queued = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "library.search",
                Source: "test",
                PayloadJson: """{"libraryId":"movies-main","libraryName":"Movies","mediaType":"movie"}""",
                ScheduledUtc: null,
                RelatedEntityType: "library",
                RelatedEntityId: "movies-main"),
            CancellationToken.None);

        var leased = await store.LeaseNextAsync(
            workerId: "worker-a",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);

        Assert.NotNull(leased);
        Assert.Equal(queued.Id, leased.Id);
        Assert.Equal("running", leased.Status);
        Assert.Equal(1, leased.Attempts);
        Assert.Equal("worker-a", leased.WorkerId);

        var noDuplicateLease = await store.LeaseNextAsync(
            workerId: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);
        Assert.Null(noDuplicateLease);

        await store.FailAsync(queued.Id, "worker-a", "Indexer timed out.", CancellationToken.None);
        Assert.Equal(1, await store.RetryFailedAsync(CancellationToken.None));

        var retried = await store.LeaseNextAsync(
            workerId: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            jobTypes: ["library.search"],
            CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(queued.Id, retried.Id);
        Assert.Equal(2, retried.Attempts);

        await store.CompleteAsync(queued.Id, "worker-b", "Library search completed.", CancellationToken.None);

        var stored = Assert.Single(await store.ListAsync(10, CancellationToken.None));
        Assert.Equal("completed", stored.Status);
        Assert.NotNull(stored.CompletedUtc);
        Assert.Null(stored.LeasedUntilUtc);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task EnqueueAsync_reuses_active_job_for_duplicate_idempotency_key()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var request = new EnqueueJobRequest(
            JobType: "movies.catalog.refresh",
            Source: "movies",
            PayloadJson: """{"movieId":"movie-1"}""",
            RelatedEntityType: "movie",
            RelatedEntityId: "movie-1",
            IdempotencyKey: "movie-1:refresh",
            DedupeKey: "movie:movie-1:refresh");

        var first = await store.EnqueueAsync(request, CancellationToken.None);
        var second = await store.EnqueueAsync(request, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("queued", second.Status);
        Assert.Single(await store.ListAsync(20, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_jobs_retry_after_backoff_and_dead_letter_at_retry_limit()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var queued = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "library.search",
                Source: "test",
                PayloadJson: """{"libraryId":"movies-main"}""",
                RelatedEntityType: "library",
                RelatedEntityId: "movies-main",
                MaxAttempts: 2),
            CancellationToken.None);

        var firstLease = await store.LeaseNextAsync("worker-a", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None);
        Assert.NotNull(firstLease);
        await store.FailAsync(queued.Id, "worker-a", "Indexer timed out.", CancellationToken.None);

        Assert.Null(await store.LeaseNextAsync("worker-b", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromSeconds(31));
        var retryLease = await store.LeaseNextAsync("worker-b", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None);
        Assert.NotNull(retryLease);
        Assert.Equal(2, retryLease.Attempts);

        await store.FailAsync(queued.Id, "worker-b", "Indexer still timed out.", CancellationToken.None);

        var stored = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Equal("dead-letter", stored.Status);
        Assert.Contains("retry limit", stored.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.LeaseNextAsync("worker-c", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None));
    }

    [Fact]
    public async Task Expired_running_leases_are_recovered_before_new_work_is_leased()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var queued = await store.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "library.search",
                Source: "test",
                PayloadJson: """{"libraryId":"series-main"}""",
                RelatedEntityType: "library",
                RelatedEntityId: "series-main",
                MaxAttempts: 2),
            CancellationToken.None);

        var firstLease = await store.LeaseNextAsync("worker-a", TimeSpan.FromMinutes(1), ["library.search"], CancellationToken.None);
        Assert.NotNull(firstLease);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.LeaseNextAsync("worker-b", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None));

        var recovered = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Equal(queued.Id, recovered.Id);
        Assert.Equal("failed", recovered.Status);
        Assert.Contains("lease expired", recovered.LastError, StringComparison.OrdinalIgnoreCase);

        timeProvider.Advance(TimeSpan.FromSeconds(31));
        var retryLease = await store.LeaseNextAsync("worker-b", TimeSpan.FromMinutes(5), ["library.search"], CancellationToken.None);
        Assert.NotNull(retryLease);
        Assert.Equal(queued.Id, retryLease.Id);
        Assert.Equal(2, retryLease.Attempts);
    }

    [Fact]
    public async Task RecordDownloadDispatchAsync_extracts_structured_decision_telemetry_from_notes_json()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-13T05:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var dispatchId = await store.RecordDownloadDispatchAsync(
            libraryId: "movies-main",
            mediaType: "movies",
            entityType: "movie",
            entityId: "movie-1",
            releaseName: "Movie.2026.1080p.WEB-DL-GROUP",
            indexerName: "Indexer One",
            downloadClientId: "qb-1",
            downloadClientName: "qBittorrent",
            status: "sent",
            notesJson:
                """
                {
                  "searchPlan": {
                    "bestCandidate": {
                      "releaseName": "Movie.2026.1080p.WEB-DL-GROUP",
                      "quality": "WEB 1080p",
                      "score": 410,
                      "meetsCutoff": true,
                      "decisionStatus": "preferred",
                      "qualityDelta": 1,
                      "customFormatScore": 125,
                      "seederScore": 120,
                      "sizeScore": 80,
                      "releaseGroup": "GROUP",
                      "estimatedBitrateMbps": 8.4,
                      "sizeBytes": 8000000000,
                      "seeders": 20,
                      "policyVersion": "policy-v1",
                      "matchedCustomFormats": [{ "formatId": "cf-1", "formatName": "Preferred Group", "score": 125 }],
                      "decisionReasons": ["Matched preferred group"],
                      "riskFlags": []
                    }
                  }
                }
                """,
            cancellationToken: CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                decision_quality,
                decision_score,
                decision_meets_cutoff,
                decision_status,
                decision_custom_format_score,
                decision_release_group,
                decision_size_bytes,
                decision_seeders,
                decision_policy_version
            FROM download_dispatches
            WHERE id = @id
            LIMIT 1;
            """;
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = dispatchId;
        command.Parameters.Add(idParameter);

        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("WEB 1080p", reader.GetString(0));
        Assert.Equal(410, reader.GetInt32(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal("preferred", reader.GetString(3));
        Assert.Equal(125, reader.GetInt32(4));
        Assert.Equal("GROUP", reader.GetString(5));
        Assert.Equal(8000000000L, reader.GetInt64(6));
        Assert.Equal(20, reader.GetInt32(7));
        Assert.Equal("policy-v1", reader.GetString(8));
    }

    [Fact]
    public async Task SkipLibrarySearchCycleAsync_clears_requested_state_and_removes_queued_search_job()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        await InitializeJobsAsync(storage, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());

        var library = new LibraryAutomationPlanItem(
            LibraryId: "movies-main",
            LibraryName: "Movies",
            MediaType: "movies",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalHours: 6,
            RetryDelayHours: 24,
            MaxItemsPerRun: 25,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null);

        Assert.True(await store.RequestLibrarySearchAsync(library, CancellationToken.None));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Single(await store.ListAsync(20, CancellationToken.None));

        Assert.True(await store.SkipLibrarySearchCycleAsync(library, CancellationToken.None));

        Assert.Empty(await store.ListAsync(20, CancellationToken.None));
        var state = Assert.Single((await store.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values);
        Assert.False(state.SearchRequested);
        Assert.Equal("idle", state.Status);
        Assert.NotNull(state.NextSearchUtc);
        Assert.Equal(timeProvider.GetUtcNow().AddHours(6), state.NextSearchUtc!.Value);
    }

    private static async Task InitializeJobsAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }
}
