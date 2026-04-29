using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Decisions;
using Deluno.Realtime;
using Microsoft.Data.Sqlite;

namespace Deluno.Jobs.Data;

public sealed class SqliteJobStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IRealtimeEventPublisher realtimeEventPublisher)
    : IJobScheduler, IJobQueueRepository, IActivityFeedRepository
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int DefaultMaxAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    public async Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var idempotencyKey = NormalizeJobKey(request.IdempotencyKey);
        var dedupeKey = NormalizeJobKey(request.DedupeKey) ?? BuildDefaultDedupeKey(request);
        var job = new JobQueueItem(
            Id: Guid.CreateVersion7().ToString("N"),
            JobType: request.JobType,
            Source: request.Source,
            Status: "queued",
            PayloadJson: request.PayloadJson,
            Attempts: 0,
            CreatedUtc: now,
            ScheduledUtc: request.ScheduledUtc ?? now,
            StartedUtc: null,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: request.RelatedEntityType,
            RelatedEntityId: request.RelatedEntityId,
            IdempotencyKey: idempotencyKey,
            DedupeKey: dedupeKey,
            MaxAttempts: NormalizeMaxAttempts(request.MaxAttempts),
            LastAttemptUtc: null,
            NextAttemptUtc: request.ScheduledUtc ?? now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var duplicate = await FindDuplicateActiveJobAsync(
            connection,
            transaction,
            idempotencyKey,
            dedupeKey,
            cancellationToken);
        if (duplicate is not null)
        {
            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.duplicate",
                message: $"Reused existing active {duplicate.JobType} job instead of creating duplicate work.",
                detailsJson: JsonSerializer.Serialize(new
                {
                    requestedJobType = request.JobType,
                    existingJobId = duplicate.Id,
                    idempotencyKey,
                    dedupeKey
                }),
                relatedJobId: duplicate.Id,
                relatedEntityType: duplicate.RelatedEntityType,
                relatedEntityId: duplicate.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        try
        {
            await InsertJobAsync(connection, transaction, job, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            duplicate = await FindDuplicateActiveJobAsync(
                connection,
                transaction,
                idempotencyKey,
                dedupeKey,
                cancellationToken);
            if (duplicate is null)
            {
                throw;
            }

            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.queued",
            message: FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson),
            detailsJson: job.PayloadJson,
            relatedJobId: job.Id,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishQueueItemAddedAsync(
            job.Id,
            FormatQueuedTitle(job.JobType, job.PayloadJson),
            job.RelatedEntityType ?? "job",
            job.Status,
            cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson),
            "job.queued",
            SeverityForCategory("job.queued"),
            now.ToString("O"),
            cancellationToken);
        DelunoObservability.JobsQueued.Add(1, new("job.type", job.JobType), new("source", job.Source));
        return job;
    }

    public async Task<IReadOnlyList<JobQueueItem>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var jobs = new List<JobQueueItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
            FROM job_queue
            ORDER BY created_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var failedJobs = new List<JobQueueItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                    idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
                FROM job_queue
                WHERE status IN ('failed', 'dead-letter')
                ORDER BY completed_utc DESC, created_utc DESC;
                """;

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                failedJobs.Add(ReadJob(reader));
            }
        }

        if (failedJobs.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE job_queue
                SET
                    status = 'queued',
                    attempts = CASE WHEN attempts >= max_attempts THEN 0 ELSE attempts END,
                    scheduled_utc = @scheduledUtc,
                    started_utc = NULL,
                    completed_utc = NULL,
                    leased_until_utc = NULL,
                    worker_id = NULL,
                    last_error = NULL,
                    next_attempt_utc = @scheduledUtc
                WHERE status IN ('failed', 'dead-letter');
                """;

            AddParameter(update, "@scheduledUtc", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.retry",
            message: $"{failedJobs.Count} failed job{(failedJobs.Count == 1 ? string.Empty : "s")} requeued.",
            detailsJson: null,
            relatedJobId: null,
            relatedEntityType: "job",
            relatedEntityId: null,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await InsertActivityAsync(
            connection,
            transaction,
            category: DecisionExplanationActivity.Category,
            message: $"job.retry: {failedJobs.Count} failed job{(failedJobs.Count == 1 ? string.Empty : "s")} were requeued by explicit retry.",
            detailsJson: JsonSerializer.Serialize(new DecisionExplanationPayload(
                Scope: "job.retry",
                Status: "requeued",
                Reason: $"{failedJobs.Count} failed job{(failedJobs.Count == 1 ? string.Empty : "s")} were selected from failed/dead-letter states and scheduled for another attempt.",
                Inputs: new Dictionary<string, string?>
                {
                    ["failedJobCount"] = failedJobs.Count.ToString(CultureInfo.InvariantCulture),
                    ["scheduledUtc"] = now.ToString("O")
                },
                Outcome: "Jobs were moved back to queued with a fresh scheduled time.",
                Alternatives: [])),
            relatedJobId: null,
            relatedEntityType: "job",
            relatedEntityId: null,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        foreach (var job in failedJobs)
        {
            await realtimeEventPublisher.PublishQueueItemAddedAsync(
                job.Id,
                FormatQueuedTitle(job.JobType, job.PayloadJson),
                job.RelatedEntityType ?? "job",
                "queued",
                cancellationToken);
        }

        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"{failedJobs.Count} failed job{(failedJobs.Count == 1 ? string.Empty : "s")} requeued.",
            "job.retry",
            SeverityForCategory("job.retry"),
            now.ToString("O"),
            cancellationToken);

        DelunoObservability.JobRetries.Add(failedJobs.Count);
        return failedJobs.Count;
    }

    public async Task<JobQueueItem?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? jobTypes,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RecoverExpiredLeasesAsync(connection, transaction, now, cancellationToken);

        JobQueueItem? candidate = null;

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            var jobTypeFilter = BuildJobTypeFilter(select, jobTypes);
            select.CommandText =
                $"""
                SELECT
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                    idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
                FROM job_queue
                WHERE status IN ('queued', 'failed')
                  AND scheduled_utc <= @now
                  AND attempts < max_attempts
                  {jobTypeFilter}
                ORDER BY scheduled_utc ASC, created_utc ASC
                LIMIT 1;
                """;

            AddParameter(select, "@now", now.ToString("O"));

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                candidate = ReadJob(reader);
            }
        }

        if (candidate is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var leasedJob = candidate with
        {
            Status = "running",
            Attempts = candidate.Attempts + 1,
            StartedUtc = candidate.StartedUtc ?? now,
            LeasedUntilUtc = leasedUntil,
            WorkerId = workerId,
            LastAttemptUtc = now,
            NextAttemptUtc = null
        };

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE job_queue
                SET
                    status = 'running',
                    attempts = attempts + 1,
                    started_utc = COALESCE(started_utc, @startedUtc),
                    leased_until_utc = @leasedUntilUtc,
                    worker_id = @workerId,
                    last_attempt_utc = @lastAttemptUtc,
                    next_attempt_utc = NULL,
                    last_error = NULL
                WHERE id = @id;
                """;

            AddParameter(update, "@id", leasedJob.Id);
            AddParameter(update, "@startedUtc", leasedJob.StartedUtc?.ToString("O"));
            AddParameter(update, "@leasedUntilUtc", leasedUntil.ToString("O"));
            AddParameter(update, "@workerId", workerId);
            AddParameter(update, "@lastAttemptUtc", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkAutomationStateRunningAsync(connection, transaction, leasedJob, now, cancellationToken);

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.started",
            message: FormatStartedMessage(leasedJob.JobType, leasedJob.PayloadJson),
            detailsJson: leasedJob.PayloadJson,
            relatedJobId: leasedJob.Id,
            relatedEntityType: leasedJob.RelatedEntityType,
            relatedEntityId: leasedJob.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return leasedJob;
    }

    private static string BuildJobTypeFilter(System.Data.Common.DbCommand command, IReadOnlyList<string>? jobTypes)
    {
        if (jobTypes is null || jobTypes.Count == 0)
        {
            return string.Empty;
        }

        var placeholders = new List<string>(jobTypes.Count);
        for (var i = 0; i < jobTypes.Count; i++)
        {
            var parameterName = $"@jobType{i}";
            placeholders.Add(parameterName);
            AddParameter(command, parameterName, jobTypes[i]);
        }

        return $"AND job_type IN ({string.Join(", ", placeholders)})";
    }

    public async Task CompleteAsync(
        string jobId,
        string workerId,
        string? completionMessage,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var job = await GetJobAsync(connection, transaction, jobId, cancellationToken);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE job_queue
                SET
                    status = 'completed',
                    completed_utc = @completedUtc,
                    leased_until_utc = NULL,
                    worker_id = @workerId,
                    last_error = NULL
                WHERE id = @id;
                """;

            AddParameter(update, "@id", jobId);
            AddParameter(update, "@completedUtc", now.ToString("O"));
            AddParameter(update, "@workerId", workerId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkAutomationStateCompletedAsync(connection, transaction, job, now, cancellationToken);

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.completed",
            message: completionMessage ?? $"{job.JobType} completed.",
            detailsJson: job.PayloadJson,
            relatedJobId: job.Id,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishQueueItemRemovedAsync(job.Id, cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            completionMessage ?? $"{job.JobType} completed.",
            "job.completed",
            SeverityForCategory("job.completed"),
            now.ToString("O"),
            cancellationToken);
        DelunoObservability.JobsCompleted.Add(1, new("job.type", job.JobType), new("source", job.Source));
    }

    public async Task FailAsync(string jobId, string workerId, string errorMessage, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var job = await GetJobAsync(connection, transaction, jobId, cancellationToken);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var attempts = Math.Max(job.Attempts, 1);
        var shouldDeadLetter = attempts >= job.MaxAttempts;
        var nextAttemptUtc = shouldDeadLetter ? (DateTimeOffset?)null : now.Add(CalculateRetryDelay(attempts));
        var nextStatus = shouldDeadLetter ? "dead-letter" : "failed";
        var storedError = shouldDeadLetter
            ? $"{errorMessage} The job reached its retry limit and moved to dead-letter."
            : errorMessage;

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE job_queue
                SET
                    status = @status,
                    completed_utc = CASE WHEN @status = 'dead-letter' THEN @completedUtc ELSE NULL END,
                    scheduled_utc = COALESCE(@nextAttemptUtc, scheduled_utc),
                    leased_until_utc = NULL,
                    worker_id = @workerId,
                    last_error = @errorMessage,
                    next_attempt_utc = @nextAttemptUtc
                WHERE id = @id;
                """;

            AddParameter(update, "@id", jobId);
            AddParameter(update, "@status", nextStatus);
            AddParameter(update, "@completedUtc", now.ToString("O"));
            AddParameter(update, "@nextAttemptUtc", nextAttemptUtc?.ToString("O"));
            AddParameter(update, "@workerId", workerId);
            AddParameter(update, "@errorMessage", storedError);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkAutomationStateFailedAsync(connection, transaction, job, errorMessage, now, cancellationToken);

        await InsertActivityAsync(
            connection,
            transaction,
            category: shouldDeadLetter ? "job.dead-letter" : "job.failed",
            message: shouldDeadLetter
                ? $"{errorMessage} The job reached its retry limit and moved to dead-letter."
                : $"{errorMessage} Deluno will retry after {nextAttemptUtc:O}.",
            detailsJson: job.PayloadJson,
            relatedJobId: job.Id,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await InsertActivityAsync(
            connection,
            transaction,
            category: DecisionExplanationActivity.Category,
            message: shouldDeadLetter
                ? $"job.failure: {job.JobType} moved to dead-letter after exhausting retries."
                : $"job.failure: {job.JobType} will retry after {nextAttemptUtc:O}.",
            detailsJson: JsonSerializer.Serialize(new DecisionExplanationPayload(
                Scope: "job.failure",
                Status: nextStatus,
                Reason: shouldDeadLetter
                    ? $"{job.JobType} reached the retry limit of {job.MaxAttempts} attempts and was moved to dead-letter."
                    : $"{job.JobType} failed on attempt {attempts}; Deluno scheduled the next attempt after exponential backoff.",
                Inputs: new Dictionary<string, string?>
                {
                    ["jobId"] = job.Id,
                    ["jobType"] = job.JobType,
                    ["attempts"] = attempts.ToString(CultureInfo.InvariantCulture),
                    ["maxAttempts"] = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
                    ["error"] = errorMessage,
                    ["nextAttemptUtc"] = nextAttemptUtc?.ToString("O")
                },
                Outcome: shouldDeadLetter
                    ? "No further automatic retries will run until the user retries failed jobs."
                    : $"Next automatic retry is scheduled for {nextAttemptUtc:O}.",
                Alternatives: [])),
            relatedJobId: job.Id,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishQueueItemRemovedAsync(job.Id, cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            shouldDeadLetter
                ? $"{errorMessage} The job reached its retry limit and moved to dead-letter."
                : $"{errorMessage} Deluno will retry after {nextAttemptUtc:O}.",
            shouldDeadLetter ? "job.dead-letter" : "job.failed",
            SeverityForCategory(shouldDeadLetter ? "job.dead-letter" : "job.failed"),
            now.ToString("O"),
            cancellationToken);
        DelunoObservability.JobsFailed.Add(1, new("job.type", job.JobType), new("status", nextStatus));
        if (!shouldDeadLetter)
        {
            DelunoObservability.JobRetries.Add(
                1,
                [new KeyValuePair<string, object?>("job.type", job.JobType)]);
        }
    }

    public async Task HeartbeatAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO worker_heartbeats (worker_id, last_seen_utc)
            VALUES (@workerId, @lastSeenUtc)
            ON CONFLICT(worker_id) DO UPDATE SET
                last_seen_utc = excluded.last_seen_utc;
            """;

        AddParameter(command, "@workerId", workerId);
        AddParameter(command, "@lastSeenUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, LibraryAutomationStateItem>> ListLibraryAutomationStatesAsync(CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, LibraryAutomationStateItem>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                library_id, library_name, media_type, status, search_requested, last_planned_utc,
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc
            FROM library_automation_state;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadLibraryAutomationState(reader);
            items[item.LibraryId] = item;
        }

        return items;
    }

    public async Task<IReadOnlyList<SearchCycleRunItem>> ListSearchCycleRunsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<SearchCycleRunItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, library_name, media_type, trigger_kind, status,
                planned_count, queued_count, skipped_count, notes_json, started_utc, completed_utc
            FROM search_cycle_runs
            WHERE (@libraryId IS NULL OR library_id = @libraryId)
            ORDER BY started_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", string.IsNullOrWhiteSpace(libraryId) ? null : libraryId);
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSearchCycleRun(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<SearchRetryWindowItem>> ListSearchRetryWindowsAsync(
        int take,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<SearchRetryWindowItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                entity_type, entity_id, library_id, media_type, action_kind,
                next_eligible_utc, last_attempt_utc, attempt_count, last_result, updated_utc
            FROM search_retry_windows
            WHERE (@libraryId IS NULL OR library_id = @libraryId)
            ORDER BY next_eligible_utc ASC, updated_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", string.IsNullOrWhiteSpace(libraryId) ? null : libraryId);
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSearchRetryWindow(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> ListDownloadDispatchesAsync(
        int take,
        string? mediaType,
        CancellationToken cancellationToken)
    {
        var items = new List<DownloadDispatchItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, notes_json, created_utc
            FROM download_dispatches
            WHERE (@mediaType IS NULL OR media_type = @mediaType)
            ORDER BY created_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@mediaType", string.IsNullOrWhiteSpace(mediaType) ? null : mediaType.Trim().ToLowerInvariant());
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DownloadDispatchItem(
                Id: reader.GetString(0),
                LibraryId: reader.GetString(1),
                MediaType: reader.GetString(2),
                EntityType: reader.GetString(3),
                EntityId: reader.GetString(4),
                ReleaseName: reader.GetString(5),
                IndexerName: reader.GetString(6),
                DownloadClientId: reader.GetString(7),
                DownloadClientName: reader.GetString(8),
                Status: reader.GetString(9),
                NotesJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedUtc: ParseTimestamp(reader.GetString(11))));
        }

        return items;
    }

    public async Task<bool> RequestLibrarySearchAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertLibraryAutomationStateAsync(connection, transaction, library, now, cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE library_automation_state
                SET
                    search_requested = 1,
                    status = CASE WHEN status = 'running' THEN status ELSE 'requested' END,
                    updated_utc = @updatedUtc
                WHERE library_id = @libraryId;
                """;

            AddParameter(command, "@libraryId", library.LibraryId);
            AddParameter(command, "@updatedUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "library.search.requested",
            message: $"Deluno will check {library.LibraryName} on the next pass.",
            detailsJson: null,
            relatedJobId: null,
            relatedEntityType: "library",
            relatedEntityId: library.LibraryId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Deluno will check {library.LibraryName} on the next pass.",
            "library.search.requested",
            SeverityForCategory("library.search.requested"),
            now.ToString("O"),
            cancellationToken);
        return true;
    }

    public async Task PlanLibrarySearchesAsync(
        IReadOnlyList<LibraryAutomationPlanItem> libraries,
        CancellationToken cancellationToken)
    {
        if (libraries.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var stateByLibraryId = await ReadLibraryAutomationStatesAsync(connection, transaction, cancellationToken);
        var pendingJobLibraries = await ReadPendingLibraryJobsAsync(connection, transaction, cancellationToken);

        foreach (var library in libraries)
        {
            await UpsertLibraryAutomationStateAsync(connection, transaction, library, now, cancellationToken);

            stateByLibraryId.TryGetValue(library.LibraryId, out var state);
            state ??= new LibraryAutomationStateItem(
                LibraryId: library.LibraryId,
                LibraryName: library.LibraryName,
                MediaType: library.MediaType,
                Status: "idle",
                SearchRequested: false,
                LastPlannedUtc: null,
                LastStartedUtc: null,
                LastCompletedUtc: null,
                NextSearchUtc: null,
                LastJobId: null,
                LastError: null,
                UpdatedUtc: now);

            if (!library.AutoSearchEnabled && !state.SearchRequested)
            {
                await UpdateAutomationIdleAsync(
                    connection,
                    transaction,
                    library.LibraryId,
                    library.LibraryName,
                    library.MediaType,
                    "paused",
                    nextSearchUtc: null,
                    searchRequested: false,
                    updatedUtc: now,
                    cancellationToken);

                continue;
            }

            var hasPendingJob = pendingJobLibraries.Contains(library.LibraryId);
            var dueForScheduledSearch = library.AutoSearchEnabled &&
                (state.NextSearchUtc is null || state.NextSearchUtc <= now);

            var shouldQueue = state.SearchRequested || dueForScheduledSearch;
            if (!shouldQueue)
            {
                continue;
            }

            if (hasPendingJob)
            {
                await UpdateAutomationIdleAsync(
                    connection,
                    transaction,
                    library.LibraryId,
                    library.LibraryName,
                    library.MediaType,
                    "queued",
                    nextSearchUtc: library.AutoSearchEnabled ? state.NextSearchUtc : null,
                    searchRequested: state.SearchRequested,
                    updatedUtc: now,
                    cancellationToken);

                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                libraryId = library.LibraryId,
                libraryName = library.LibraryName,
                mediaType = library.MediaType,
                checkMissing = library.MissingSearchEnabled,
                checkUpgrades = library.UpgradeSearchEnabled,
                maxItems = library.MaxItemsPerRun,
                retryDelayHours = library.RetryDelayHours,
                triggeredBy = state.SearchRequested ? "manual" : "schedule"
            });
            var idempotencyKey = $"library.search:{library.LibraryId}:{(state.SearchRequested ? "manual" : "schedule")}";
            var dedupeKey = $"library.search:{library.LibraryId}";

            var job = new JobQueueItem(
                Id: Guid.CreateVersion7().ToString("N"),
                JobType: "library.search",
                Source: library.MediaType,
                Status: "queued",
                PayloadJson: payload,
                Attempts: 0,
                CreatedUtc: now,
                ScheduledUtc: now,
                StartedUtc: null,
                CompletedUtc: null,
                LeasedUntilUtc: null,
                WorkerId: null,
                LastError: null,
                RelatedEntityType: "library",
                RelatedEntityId: library.LibraryId,
                IdempotencyKey: idempotencyKey,
                DedupeKey: dedupeKey,
                MaxAttempts: DefaultMaxAttempts,
                LastAttemptUtc: null,
                NextAttemptUtc: now);

            var duplicate = await FindDuplicateActiveJobAsync(
                connection,
                transaction,
                idempotencyKey,
                dedupeKey,
                cancellationToken);
            if (duplicate is not null)
            {
                await UpdateAutomationIdleAsync(
                    connection,
                    transaction,
                    library.LibraryId,
                    library.LibraryName,
                    library.MediaType,
                    "queued",
                    nextSearchUtc: library.AutoSearchEnabled ? now.AddHours(Math.Max(1, library.SearchIntervalHours)) : null,
                    searchRequested: false,
                    updatedUtc: now,
                    cancellationToken);
                continue;
            }

            await InsertJobAsync(connection, transaction, job, cancellationToken);

            DateTimeOffset? nextSearchUtc = library.AutoSearchEnabled
                ? now.AddHours(Math.Max(1, library.SearchIntervalHours))
                : null;

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE library_automation_state
                    SET
                        library_name = @libraryName,
                        media_type = @mediaType,
                        status = 'queued',
                        search_requested = 0,
                        last_planned_utc = @lastPlannedUtc,
                        next_search_utc = @nextSearchUtc,
                        last_job_id = @lastJobId,
                        last_error = NULL,
                        updated_utc = @updatedUtc
                    WHERE library_id = @libraryId;
                    """;

                AddParameter(update, "@libraryId", library.LibraryId);
                AddParameter(update, "@libraryName", library.LibraryName);
                AddParameter(update, "@mediaType", library.MediaType);
                AddParameter(update, "@lastPlannedUtc", now.ToString("O"));
                AddParameter(update, "@nextSearchUtc", nextSearchUtc?.ToString("O"));
                AddParameter(update, "@lastJobId", job.Id);
                AddParameter(update, "@updatedUtc", now.ToString("O"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.queued",
                message: FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson),
                detailsJson: job.PayloadJson,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordDownloadDispatchAsync(
        string libraryId,
        string mediaType,
        string entityType,
        string entityId,
        string releaseName,
        string indexerName,
        string downloadClientId,
        string downloadClientName,
        string status,
        string? notesJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dispatchId = Guid.CreateVersion7().ToString("N");

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO download_dispatches (
                    id, library_id, media_type, entity_type, entity_id, release_name,
                    indexer_name, download_client_id, download_client_name, status, notes_json, created_utc
                )
                VALUES (
                    @id, @libraryId, @mediaType, @entityType, @entityId, @releaseName,
                    @indexerName, @downloadClientId, @downloadClientName, @status, @notesJson, @createdUtc
                );
                """;

            AddParameter(command, "@id", dispatchId);
            AddParameter(command, "@libraryId", libraryId);
            AddParameter(command, "@mediaType", mediaType);
            AddParameter(command, "@entityType", entityType);
            AddParameter(command, "@entityId", entityId);
            AddParameter(command, "@releaseName", releaseName);
            AddParameter(command, "@indexerName", indexerName);
            AddParameter(command, "@downloadClientId", downloadClientId);
            AddParameter(command, "@downloadClientName", downloadClientName);
            AddParameter(command, "@status", status);
            AddParameter(command, "@notesJson", notesJson);
            AddParameter(command, "@createdUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "download.dispatch.recorded",
            message: $"Deluno sent {releaseName} to {downloadClientName}.",
            detailsJson: notesJson,
            relatedJobId: null,
            relatedEntityType: entityType,
            relatedEntityId: entityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishDownloadProgressAsync(
            dispatchId,
            releaseName,
            0,
            0,
            null,
            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "downloading",
            cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Deluno sent {releaseName} to {downloadClientName}.",
            "download.dispatch.recorded",
            SeverityForCategory("download.dispatch.recorded"),
            now.ToString("O"),
            cancellationToken);
    }

    public async Task RecordSearchCycleRunAsync(
        RecordSearchCycleRunRequest request,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7().ToString("N");

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO search_cycle_runs (
                    id, library_id, library_name, media_type, trigger_kind, status,
                    planned_count, queued_count, skipped_count, notes_json, started_utc, completed_utc
                )
                VALUES (
                    @id, @libraryId, @libraryName, @mediaType, @triggerKind, @status,
                    @plannedCount, @queuedCount, @skippedCount, @notesJson, @startedUtc, @completedUtc
                );
                """;

            AddParameter(command, "@id", id);
            AddParameter(command, "@libraryId", request.LibraryId);
            AddParameter(command, "@libraryName", request.LibraryName);
            AddParameter(command, "@mediaType", request.MediaType);
            AddParameter(command, "@triggerKind", request.TriggerKind);
            AddParameter(command, "@status", request.Status);
            AddParameter(command, "@plannedCount", request.PlannedCount);
            AddParameter(command, "@queuedCount", request.QueuedCount);
            AddParameter(command, "@skippedCount", request.SkippedCount);
            AddParameter(command, "@notesJson", request.NotesJson);
            AddParameter(command, "@startedUtc", request.StartedUtc.ToString("O"));
            AddParameter(command, "@completedUtc", request.CompletedUtc?.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "library.search.cycle",
            message: $"Checked {request.LibraryName}: {request.PlannedCount} planned, {request.QueuedCount} sent, {request.SkippedCount} waiting.",
            detailsJson: request.NotesJson,
            relatedJobId: null,
            relatedEntityType: "library",
            relatedEntityId: request.LibraryId,
            createdUtc: request.CompletedUtc ?? request.StartedUtc,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Checked {request.LibraryName}: {request.PlannedCount} planned, {request.QueuedCount} sent, {request.SkippedCount} waiting.",
            "library.search.cycle",
            SeverityForCategory("library.search.cycle"),
            (request.CompletedUtc ?? request.StartedUtc).ToString("O"),
            cancellationToken);
    }

    public async Task RecordSearchRetryWindowAsync(
        string entityType,
        string entityId,
        string libraryId,
        string mediaType,
        string actionKind,
        DateTimeOffset nextEligibleUtc,
        DateTimeOffset lastAttemptUtc,
        string? lastResult,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO search_retry_windows (
                entity_type, entity_id, library_id, media_type, action_kind,
                next_eligible_utc, last_attempt_utc, attempt_count, last_result, updated_utc
            )
            VALUES (
                @entityType, @entityId, @libraryId, @mediaType, @actionKind,
                @nextEligibleUtc, @lastAttemptUtc, 1, @lastResult, @updatedUtc
            )
            ON CONFLICT(entity_type, entity_id, library_id, action_kind) DO UPDATE SET
                media_type = excluded.media_type,
                next_eligible_utc = excluded.next_eligible_utc,
                last_attempt_utc = excluded.last_attempt_utc,
                attempt_count = search_retry_windows.attempt_count + 1,
                last_result = excluded.last_result,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@entityType", entityType);
        AddParameter(command, "@entityId", entityId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@actionKind", actionKind);
        AddParameter(command, "@nextEligibleUtc", nextEligibleUtc.ToString("O"));
        AddParameter(command, "@lastAttemptUtc", lastAttemptUtc.ToString("O"));
        AddParameter(command, "@lastResult", lastResult);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(
        int take,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
    {
        var events = new List<ActivityEventItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc
            FROM activity_events
            WHERE (@relatedEntityType IS NULL OR related_entity_type = @relatedEntityType)
              AND (@relatedEntityId IS NULL OR related_entity_id = @relatedEntityId)
            ORDER BY created_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@take", take);
        AddParameter(command, "@relatedEntityType", relatedEntityType);
        AddParameter(command, "@relatedEntityId", relatedEntityId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadActivity(reader));
        }

        return events;
    }

    public async Task<ActivityEventItem> RecordActivityAsync(
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var activity = new ActivityEventItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Category: category,
            Message: message,
            DetailsJson: detailsJson,
            RelatedJobId: relatedJobId,
            RelatedEntityType: relatedEntityType,
            RelatedEntityId: relatedEntityId,
            CreatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO activity_events (
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc
            )
            VALUES (
                @id, @category, @message, @detailsJson, @relatedJobId, @relatedEntityType, @relatedEntityId, @createdUtc
            );
            """;

        AddParameter(command, "@id", activity.Id);
        AddParameter(command, "@category", activity.Category);
        AddParameter(command, "@message", activity.Message);
        AddParameter(command, "@detailsJson", activity.DetailsJson);
        AddParameter(command, "@relatedJobId", activity.RelatedJobId);
        AddParameter(command, "@relatedEntityType", activity.RelatedEntityType);
        AddParameter(command, "@relatedEntityId", activity.RelatedEntityId);
        AddParameter(command, "@createdUtc", activity.CreatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            activity.Id,
            activity.Message,
            activity.Category,
            SeverityForCategory(activity.Category),
            activity.CreatedUtc.ToString("O"),
            cancellationToken);
        return activity;
    }

    private static async Task InsertJobAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO job_queue (
                id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
            )
            VALUES (
                @id, @jobType, @source, @status, @payloadJson, @attempts, @createdUtc, @scheduledUtc,
                NULL, NULL, NULL, NULL, NULL, @relatedEntityType, @relatedEntityId,
                @idempotencyKey, @dedupeKey, @maxAttempts, NULL, @nextAttemptUtc
            );
            """;

        AddParameter(command, "@id", job.Id);
        AddParameter(command, "@jobType", job.JobType);
        AddParameter(command, "@source", job.Source);
        AddParameter(command, "@status", job.Status);
        AddParameter(command, "@payloadJson", job.PayloadJson);
        AddParameter(command, "@attempts", job.Attempts);
        AddParameter(command, "@createdUtc", job.CreatedUtc.ToString("O"));
        AddParameter(command, "@scheduledUtc", job.ScheduledUtc.ToString("O"));
        AddParameter(command, "@relatedEntityType", job.RelatedEntityType);
        AddParameter(command, "@relatedEntityId", job.RelatedEntityId);
        AddParameter(command, "@idempotencyKey", job.IdempotencyKey);
        AddParameter(command, "@dedupeKey", job.DedupeKey);
        AddParameter(command, "@maxAttempts", job.MaxAttempts);
        AddParameter(command, "@nextAttemptUtc", job.NextAttemptUtc?.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<JobQueueItem?> FindDuplicateActiveJobAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string? idempotencyKey,
        string? dedupeKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) && string.IsNullOrWhiteSpace(dedupeKey))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
            FROM job_queue
            WHERE status IN ('queued', 'running', 'failed')
              AND (
                (@idempotencyKey IS NOT NULL AND idempotency_key = @idempotencyKey)
                OR (@dedupeKey IS NOT NULL AND dedupe_key = @dedupeKey)
              )
            ORDER BY created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@idempotencyKey", idempotencyKey);
        AddParameter(command, "@dedupeKey", dedupeKey);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    private static async Task RecoverExpiredLeasesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = new List<JobQueueItem>();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                    idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
                FROM job_queue
                WHERE status = 'running'
                  AND leased_until_utc IS NOT NULL
                  AND leased_until_utc <= @now
                ORDER BY leased_until_utc ASC;
                """;
            AddParameter(select, "@now", now.ToString("O"));

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add(ReadJob(reader));
            }
        }

        foreach (var job in expired)
        {
            var shouldDeadLetter = job.Attempts >= job.MaxAttempts;
            var nextAttemptUtc = shouldDeadLetter ? (DateTimeOffset?)null : now.Add(CalculateRetryDelay(Math.Max(job.Attempts, 1)));
            var message = shouldDeadLetter
                ? "Worker lease expired after the retry limit was reached; job moved to dead-letter."
                : "Worker lease expired; job was released for retry.";

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE job_queue
                SET
                    status = @status,
                    completed_utc = CASE WHEN @status = 'dead-letter' THEN @now ELSE NULL END,
                    scheduled_utc = COALESCE(@nextAttemptUtc, scheduled_utc),
                    leased_until_utc = NULL,
                    worker_id = NULL,
                    last_error = @lastError,
                    next_attempt_utc = @nextAttemptUtc
                WHERE id = @id
                  AND status = 'running';
                """;
            AddParameter(update, "@id", job.Id);
            AddParameter(update, "@status", shouldDeadLetter ? "dead-letter" : "failed");
            AddParameter(update, "@now", now.ToString("O"));
            AddParameter(update, "@nextAttemptUtc", nextAttemptUtc?.ToString("O"));
            AddParameter(update, "@lastError", message);
            await update.ExecuteNonQueryAsync(cancellationToken);

            await InsertActivityAsync(
                connection,
                transaction,
                category: shouldDeadLetter ? "job.dead-letter" : "job.lease-expired",
                message: $"{job.JobType}: {message}",
                detailsJson: job.PayloadJson,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);
        }
    }

    private static async Task<Dictionary<string, LibraryAutomationStateItem>> ReadLibraryAutomationStatesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, LibraryAutomationStateItem>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                library_id, library_name, media_type, status, search_requested, last_planned_utc,
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc
            FROM library_automation_state;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadLibraryAutomationState(reader);
            items[item.LibraryId] = item;
        }

        return items;
    }

    private static async Task<HashSet<string>> ReadPendingLibraryJobsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DISTINCT related_entity_id
            FROM job_queue
            WHERE job_type = 'library.search'
              AND related_entity_type = 'library'
              AND status IN ('queued', 'running')
              AND related_entity_id IS NOT NULL;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task UpsertLibraryAutomationStateAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        LibraryAutomationPlanItem library,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO library_automation_state (
                library_id, library_name, media_type, status, search_requested, last_planned_utc,
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc
            )
            VALUES (
                @libraryId, @libraryName, @mediaType, 'idle', 0, NULL, NULL, NULL, NULL, NULL, NULL, @updatedUtc
            )
            ON CONFLICT(library_id) DO UPDATE SET
                library_name = excluded.library_name,
                media_type = excluded.media_type,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@libraryId", library.LibraryId);
        AddParameter(command, "@libraryName", library.LibraryName);
        AddParameter(command, "@mediaType", library.MediaType);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAutomationIdleAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string libraryId,
        string libraryName,
        string mediaType,
        string status,
        DateTimeOffset? nextSearchUtc,
        bool searchRequested,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET
                library_name = @libraryName,
                media_type = @mediaType,
                status = @status,
                next_search_utc = @nextSearchUtc,
                search_requested = @searchRequested,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@libraryName", libraryName);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@status", status);
        AddParameter(command, "@nextSearchUtc", nextSearchUtc?.ToString("O"));
        AddParameter(command, "@searchRequested", searchRequested ? 1 : 0);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAutomationStateRunningAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (job.JobType != "library.search" || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET
                status = 'running',
                last_started_utc = @lastStartedUtc,
                search_requested = 0,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId;
            """;

        AddParameter(command, "@libraryId", job.RelatedEntityId);
        AddParameter(command, "@lastStartedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAutomationStateCompletedAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (job.JobType != "library.search" || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET
                status = 'ready',
                last_completed_utc = @lastCompletedUtc,
                last_error = NULL,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId;
            """;

        AddParameter(command, "@libraryId", job.RelatedEntityId);
        AddParameter(command, "@lastCompletedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAutomationStateFailedAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        string errorMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (job.JobType != "library.search" || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET
                status = 'attention',
                last_completed_utc = @lastCompletedUtc,
                last_error = @lastError,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId;
            """;

        AddParameter(command, "@libraryId", job.RelatedEntityId);
        AddParameter(command, "@lastCompletedUtc", now.ToString("O"));
        AddParameter(command, "@lastError", errorMessage);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActivityAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO activity_events (
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc
            )
            VALUES (
                @id, @category, @message, @detailsJson, @relatedJobId, @relatedEntityType, @relatedEntityId, @createdUtc
            );
            """;

        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@category", category);
        AddParameter(command, "@message", message);
        AddParameter(command, "@detailsJson", detailsJson);
        AddParameter(command, "@relatedJobId", relatedJobId);
        AddParameter(command, "@relatedEntityType", relatedEntityType);
        AddParameter(command, "@relatedEntityId", relatedEntityId);
        AddParameter(command, "@createdUtc", createdUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<JobQueueItem?> GetJobAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
            FROM job_queue
            WHERE id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", jobId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadJob(reader)
            : null;
    }

    private static JobQueueItem ReadJob(System.Data.Common.DbDataReader reader)
    {
        return new JobQueueItem(
            Id: reader.GetString(0),
            JobType: reader.GetString(1),
            Source: reader.GetString(2),
            Status: reader.GetString(3),
            PayloadJson: reader.IsDBNull(4) ? null : reader.GetString(4),
            Attempts: reader.GetInt32(5),
            CreatedUtc: ParseTimestamp(reader.GetString(6)),
            ScheduledUtc: ParseTimestamp(reader.GetString(7)),
            StartedUtc: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            CompletedUtc: reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            LeasedUntilUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
            WorkerId: reader.IsDBNull(11) ? null : reader.GetString(11),
            LastError: reader.IsDBNull(12) ? null : reader.GetString(12),
            RelatedEntityType: reader.IsDBNull(13) ? null : reader.GetString(13),
            RelatedEntityId: reader.IsDBNull(14) ? null : reader.GetString(14),
            IdempotencyKey: reader.IsDBNull(15) ? null : reader.GetString(15),
            DedupeKey: reader.IsDBNull(16) ? null : reader.GetString(16),
            MaxAttempts: reader.IsDBNull(17) ? DefaultMaxAttempts : reader.GetInt32(17),
            LastAttemptUtc: reader.IsDBNull(18) ? null : ParseTimestamp(reader.GetString(18)),
            NextAttemptUtc: reader.IsDBNull(19) ? null : ParseTimestamp(reader.GetString(19)));
    }

    private static LibraryAutomationStateItem ReadLibraryAutomationState(System.Data.Common.DbDataReader reader)
    {
        return new LibraryAutomationStateItem(
            LibraryId: reader.GetString(0),
            LibraryName: reader.GetString(1),
            MediaType: reader.GetString(2),
            Status: reader.GetString(3),
            SearchRequested: reader.GetInt64(4) == 1,
            LastPlannedUtc: reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            LastStartedUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
            LastCompletedUtc: reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
            NextSearchUtc: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            LastJobId: reader.IsDBNull(9) ? null : reader.GetString(9),
            LastError: reader.IsDBNull(10) ? null : reader.GetString(10),
            UpdatedUtc: ParseTimestamp(reader.GetString(11)));
    }

    private static SearchCycleRunItem ReadSearchCycleRun(System.Data.Common.DbDataReader reader)
    {
        return new SearchCycleRunItem(
            Id: reader.GetString(0),
            LibraryId: reader.GetString(1),
            LibraryName: reader.GetString(2),
            MediaType: reader.GetString(3),
            TriggerKind: reader.GetString(4),
            Status: reader.GetString(5),
            PlannedCount: reader.GetInt32(6),
            QueuedCount: reader.GetInt32(7),
            SkippedCount: reader.GetInt32(8),
            NotesJson: reader.IsDBNull(9) ? null : reader.GetString(9),
            StartedUtc: ParseTimestamp(reader.GetString(10)),
            CompletedUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)));
    }

    private static SearchRetryWindowItem ReadSearchRetryWindow(System.Data.Common.DbDataReader reader)
    {
        return new SearchRetryWindowItem(
            EntityType: reader.GetString(0),
            EntityId: reader.GetString(1),
            LibraryId: reader.GetString(2),
            MediaType: reader.GetString(3),
            ActionKind: reader.GetString(4),
            NextEligibleUtc: ParseTimestamp(reader.GetString(5)),
            LastAttemptUtc: ParseTimestamp(reader.GetString(6)),
            AttemptCount: reader.GetInt32(7),
            LastResult: reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedUtc: ParseTimestamp(reader.GetString(9)));
    }

    private static ActivityEventItem ReadActivity(System.Data.Common.DbDataReader reader)
    {
        return new ActivityEventItem(
            Id: reader.GetString(0),
            Category: reader.GetString(1),
            Message: reader.GetString(2),
            DetailsJson: reader.IsDBNull(3) ? null : reader.GetString(3),
            RelatedJobId: reader.IsDBNull(4) ? null : reader.GetString(4),
            RelatedEntityType: reader.IsDBNull(5) ? null : reader.GetString(5),
            RelatedEntityId: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedUtc: ParseTimestamp(reader.GetString(7)));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatQueuedMessage(string jobType, string source, string? payloadJson)
    {
        if (jobType == "library.search")
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                var trigger = context.TriggeredBy == "manual" ? "right away" : "on schedule";
                return $"Queued a library check for {context.LibraryName} {trigger}.";
            }

            return "Queued a library check.";
        }

        return jobType switch
        {
            "movies.catalog.refresh" => "Added a movie check to the queue.",
            "series.catalog.refresh" => "Added a TV show check to the queue.",
            "filesystem.import.execute" => "Added a file import to the queue.",
            "movies.metadata.refresh" => "Added a movie metadata refresh to the queue.",
            "series.metadata.refresh" => "Added a TV metadata refresh to the queue.",
            "movies.quality.recalculate" => "Added a movie quality refresh to the queue.",
            "series.quality.recalculate" => "Added a TV quality refresh to the queue.",
            _ => $"Added a background task from {FormatSourceName(source)}."
        };
    }

    private static string FormatStartedMessage(string jobType, string? payloadJson)
    {
        if (jobType == "library.search")
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                return $"Started checking {context.LibraryName}.";
            }

            return "Started checking a library.";
        }

        return jobType switch
        {
            "movies.catalog.refresh" => "Started checking your movie library.",
            "series.catalog.refresh" => "Started checking your TV show library.",
            "filesystem.import.execute" => "Started importing a completed download.",
            "movies.metadata.refresh" => "Started refreshing movie metadata.",
            "series.metadata.refresh" => "Started refreshing TV metadata.",
            "movies.quality.recalculate" => "Started refreshing movie quality decisions.",
            "series.quality.recalculate" => "Started refreshing TV quality decisions.",
            _ => "Started a background task."
        };
    }

    private static string FormatQueuedTitle(string jobType, string? payloadJson)
    {
        if (jobType == "library.search")
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                return context.LibraryName;
            }
        }

        return jobType switch
        {
            "filesystem.import.execute" => "File import",
            "movies.metadata.refresh" => "Movie metadata refresh",
            "series.metadata.refresh" => "TV metadata refresh",
            "movies.quality.recalculate" => "Movie quality refresh",
            "series.quality.recalculate" => "TV quality refresh",
            _ => jobType
        };
    }

    private static string? NormalizeJobKey(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildDefaultDedupeKey(EnqueueJobRequest request)
    {
        var entityType = string.IsNullOrWhiteSpace(request.RelatedEntityType)
            ? "none"
            : request.RelatedEntityType.Trim().ToLowerInvariant();
        var entityId = string.IsNullOrWhiteSpace(request.RelatedEntityId)
            ? "none"
            : request.RelatedEntityId.Trim().ToLowerInvariant();
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.PayloadJson ?? string.Empty)))
            .ToLowerInvariant();
        return $"{request.JobType.Trim().ToLowerInvariant()}:{request.Source.Trim().ToLowerInvariant()}:{entityType}:{entityId}:{payloadHash}";
    }

    private static int NormalizeMaxAttempts(int? value)
        => value is >= 1 and <= 25 ? value.Value : DefaultMaxAttempts;

    private static TimeSpan CalculateRetryDelay(int attempts)
    {
        var boundedAttempt = Math.Clamp(attempts, 1, 10);
        var delay = TimeSpan.FromSeconds(Math.Pow(2, boundedAttempt - 1) * 30);
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static string SeverityForCategory(string category)
    {
        if (category.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("dead-letter", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (category.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("attention", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (category.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        return "info";
    }

    private static string FormatSourceName(string source)
    {
        return source switch
        {
            "movies" => "Movies",
            "series" => "TV Shows",
            "tv" => "TV Shows",
            _ => "Deluno"
        };
    }

    private static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson, PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy);
}
