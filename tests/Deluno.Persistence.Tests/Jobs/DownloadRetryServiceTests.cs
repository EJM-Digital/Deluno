using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class DownloadRetryServiceTests
{
    private static async Task InsertFailedDispatchAsync(
        IDelunoDatabaseConnectionFactory connectionFactory,
        string dispatchId,
        string libraryId,
        string entityId,
        string releaseName,
        string failureCode,
        DateTimeOffset createdUtc)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                grab_status, grab_failure_code, grab_attempted_utc, created_utc,
                attempt_count, next_retry_eligible_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial',
                'failed', @failureCode, @grabAttemptedUtc, @createdUtc, 1, @nextRetryEligible
            )
            """;

        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = dispatchId;
        command.Parameters.Add(idParam);

        var libParam = command.CreateParameter();
        libParam.ParameterName = "@libraryId";
        libParam.Value = libraryId;
        command.Parameters.Add(libParam);

        var entityParam = command.CreateParameter();
        entityParam.ParameterName = "@entityId";
        entityParam.Value = entityId;
        command.Parameters.Add(entityParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@releaseName";
        nameParam.Value = releaseName;
        command.Parameters.Add(nameParam);

        var failureParam = command.CreateParameter();
        failureParam.ParameterName = "@failureCode";
        failureParam.Value = failureCode;
        command.Parameters.Add(failureParam);

        var grabTimeParam = command.CreateParameter();
        grabTimeParam.ParameterName = "@grabAttemptedUtc";
        grabTimeParam.Value = createdUtc.ToString("O");
        command.Parameters.Add(grabTimeParam);

        var createdParam = command.CreateParameter();
        createdParam.ParameterName = "@createdUtc";
        createdParam.Value = createdUtc.ToString("O");
        command.Parameters.Add(createdParam);

        var nextRetryParam = command.CreateParameter();
        nextRetryParam.ParameterName = "@nextRetryEligible";
        nextRetryParam.Value = createdUtc.ToString("O");
        command.Parameters.Add(nextRetryParam);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunRetryPass_queues_retry_for_eligible_failed_dispatch_with_exponential_backoff()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T04:00:00Z");
        var timeProvider = new FixedTimeProvider(now);

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var dispatchesRepository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobScheduler = new InMemoryJobScheduler();
        var retryService = new DownloadRetryService(
            dispatchesRepository, jobScheduler, timeProvider, NullLogger<DownloadRetryService>.Instance);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        var createdTime = now.Subtract(TimeSpan.FromHours(1));

        await InsertFailedDispatchAsync(
            storage.Factory,
            dispatchId,
            "movies-main",
            "123",
            "Test.Movie.2024.1080p",
            "grab-timeout",
            createdTime);

        // Run retry pass when dispatch should be retried
        var result = await retryService.RunRetryPassAsync(CancellationToken.None);

        Assert.Equal(1, result.RetriedCount);
        Assert.Single(jobScheduler.EnqueuedJobs);

        var enqueuedJob = jobScheduler.EnqueuedJobs[0];
        Assert.Equal("library.search", enqueuedJob.JobType);
        Assert.Equal("DownloadRetryService", enqueuedJob.Source);
        Assert.NotNull(enqueuedJob.ScheduledUtc);

        // Verify exponential backoff: grab-timeout has InitialDelay of 30 minutes, BackoffMultiplier of 2.0
        // For attempt #2: 30 minutes * 2^(2-1) = 60 minutes = 1 hour
        var expectedNextRetry = now.AddHours(1);
        Assert.InRange(enqueuedJob.ScheduledUtc.Value, expectedNextRetry.Subtract(TimeSpan.FromSeconds(5)), expectedNextRetry.Add(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunRetryPass_skips_dispatches_not_yet_eligible_for_retry()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T04:00:00Z");
        var timeProvider = new FixedTimeProvider(now);

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var dispatchesRepository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobScheduler = new InMemoryJobScheduler();
        var retryService = new DownloadRetryService(
            dispatchesRepository, jobScheduler, timeProvider, NullLogger<DownloadRetryService>.Instance);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        var createdTime = now.Subtract(TimeSpan.FromHours(1));
        var nextRetryEligible = now.Add(TimeSpan.FromHours(1)); // Not yet eligible

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                grab_status, grab_failure_code, grab_attempted_utc, created_utc,
                attempt_count, next_retry_eligible_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial',
                'failed', 'grab-timeout', @grabAttemptedUtc, @createdUtc, 1, @nextRetryEligible
            )
            """;

        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = dispatchId;
        command.Parameters.Add(idParam);

        var libParam = command.CreateParameter();
        libParam.ParameterName = "@libraryId";
        libParam.Value = "movies-main";
        command.Parameters.Add(libParam);

        var entityParam = command.CreateParameter();
        entityParam.ParameterName = "@entityId";
        entityParam.Value = "123";
        command.Parameters.Add(entityParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@releaseName";
        nameParam.Value = "Test.Movie.2024.1080p";
        command.Parameters.Add(nameParam);

        var grabTimeParam = command.CreateParameter();
        grabTimeParam.ParameterName = "@grabAttemptedUtc";
        grabTimeParam.Value = createdTime.ToString("O");
        command.Parameters.Add(grabTimeParam);

        var createdParam = command.CreateParameter();
        createdParam.ParameterName = "@createdUtc";
        createdParam.Value = createdTime.ToString("O");
        command.Parameters.Add(createdParam);

        var nextRetryParam = command.CreateParameter();
        nextRetryParam.ParameterName = "@nextRetryEligible";
        nextRetryParam.Value = nextRetryEligible.ToString("O");
        command.Parameters.Add(nextRetryParam);

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        // Run retry pass when dispatch is NOT yet eligible
        var result = await retryService.RunRetryPassAsync(CancellationToken.None);

        // When a dispatch is not yet eligible for retry, it won't be found by the query at all
        Assert.Equal(0, result.RetriedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Empty(jobScheduler.EnqueuedJobs);
    }

    [Fact]
    public async Task RunRetryPass_skips_dispatches_exceeding_max_retries()
    {
        using var storage = TestStorage.Create();
        var now = DateTimeOffset.Parse("2026-04-29T04:00:00Z");
        var timeProvider = new FixedTimeProvider(now);

        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var dispatchesRepository = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobScheduler = new InMemoryJobScheduler();
        var retryService = new DownloadRetryService(
            dispatchesRepository, jobScheduler, timeProvider, NullLogger<DownloadRetryService>.Instance);

        var dispatchId = Guid.CreateVersion7().ToString("N");
        var createdTime = now.Subtract(TimeSpan.FromHours(6));

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs, CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                grab_status, grab_failure_code, grab_attempted_utc, created_utc,
                attempt_count, next_retry_eligible_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial',
                'failed', 'grab-timeout', @grabAttemptedUtc, @createdUtc, 4, @nextRetryEligible
            )
            """;

        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = dispatchId;
        command.Parameters.Add(idParam);

        var libParam = command.CreateParameter();
        libParam.ParameterName = "@libraryId";
        libParam.Value = "movies-main";
        command.Parameters.Add(libParam);

        var entityParam = command.CreateParameter();
        entityParam.ParameterName = "@entityId";
        entityParam.Value = "123";
        command.Parameters.Add(entityParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@releaseName";
        nameParam.Value = "Test.Movie.2024.1080p";
        command.Parameters.Add(nameParam);

        var grabTimeParam = command.CreateParameter();
        grabTimeParam.ParameterName = "@grabAttemptedUtc";
        grabTimeParam.Value = createdTime.ToString("O");
        command.Parameters.Add(grabTimeParam);

        var createdParam = command.CreateParameter();
        createdParam.ParameterName = "@createdUtc";
        createdParam.Value = createdTime.ToString("O");
        command.Parameters.Add(createdParam);

        var nextRetryParam = command.CreateParameter();
        nextRetryParam.ParameterName = "@nextRetryEligible";
        nextRetryParam.Value = now.Subtract(TimeSpan.FromMinutes(1)).ToString("O");
        command.Parameters.Add(nextRetryParam);

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        // Run retry pass with dispatch already at max retries
        var result = await retryService.RunRetryPassAsync(CancellationToken.None);

        // grab-timeout has MaxRetries of 3, so 4 attempts exceeds it
        Assert.Equal(0, result.RetriedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Empty(jobScheduler.EnqueuedJobs);
    }
}

// Test helper: in-memory job scheduler for testing
internal class InMemoryJobScheduler : IJobScheduler
{
    public List<EnqueueJobRequest> EnqueuedJobs { get; } = new();

    public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
    {
        EnqueuedJobs.Add(request);
        return Task.FromResult(new JobQueueItem(
            Id: Guid.CreateVersion7().ToString("N"),
            JobType: request.JobType,
            Source: request.Source,
            Status: "queued",
            PayloadJson: request.PayloadJson,
            Attempts: 0,
            CreatedUtc: DateTimeOffset.UtcNow,
            ScheduledUtc: request.ScheduledUtc ?? DateTimeOffset.UtcNow,
            StartedUtc: null,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: request.RelatedEntityType,
            RelatedEntityId: request.RelatedEntityId,
            IdempotencyKey: request.IdempotencyKey,
            DedupeKey: request.DedupeKey,
            MaxAttempts: request.MaxAttempts ?? 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null));
    }
}
