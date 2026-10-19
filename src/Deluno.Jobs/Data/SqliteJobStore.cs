using Deluno.Contracts;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Decisions;
using Deluno.Quality.ReleasePreferences;
using Deluno.Notifications;
using Microsoft.Data.Sqlite;

namespace Deluno.Jobs.Data;

public sealed class SqliteJobStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IRealtimeEventPublisher realtimeEventPublisher,
    IDownloadDispatchesRepository downloadDispatchesRepository,
    IJobLaneSignal? laneSignal = null,
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationPreferences = null,
    // #990: the grabbed title's own tags, so a tagged notification
    // connection fires for the "On Grab" event scoped to it. Null for an
    // "episode" dispatch, whose entity id is the episode rather than the
    // show Deluno's tag store keys on.
    IMediaTagStore? mediaTagStore = null)
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
        if (duplicate is { Status: "dead-letter" })
        {
            // The same work has permanently failed before. Revive that row
            // rather than inserting a second one.
            //
            // Dead-letter rows sit outside ux_job_queue_active_dedupe, so
            // every re-enqueue of work that keeps failing used to add a row.
            // On the lab rig that produced 459 rows for one vanished season
            // pack in seventeen hours, and nothing bounded it. It also broke
            // Retry: it promotes one row per dedupe key, so pressing the button
            // the dashboard sends you to left 458 of them exactly where they
            // were.
            //
            // Reviving keeps what that exclusion was for — the WorkPlanner
            // deliberately does not let a dead-lettered import reserve its
            // source, so that repairing the source lets the work run again.
            // It still runs again. There is just one row for it.
            await ReviveDeadLetteredJobAsync(connection, transaction, duplicate.Id, job, now, cancellationToken);

            if (!JobActivityQuiet.IsQuiet(job.PayloadJson))
            {
                await InsertActivityAsync(
                    connection,
                    transaction,
                    category: "job.revived",
                    message: $"Retrying {JobTypeLabels.ForSentence(duplicate.JobType)}, which had previously given up.",
                    detailsJson: job.PayloadJson,
                    relatedJobId: duplicate.Id,
                    relatedEntityType: duplicate.RelatedEntityType,
                    relatedEntityId: duplicate.RelatedEntityId,
                    createdUtc: now,
                    cancellationToken: cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (job.ScheduledUtc <= now)
            {
                laneSignal?.Notify(job.JobType);
            }

            return duplicate with
            {
                Status = "queued",
                Attempts = 0,
                PayloadJson = job.PayloadJson,
                ScheduledUtc = job.ScheduledUtc,
                NextAttemptUtc = job.ScheduledUtc,
                StartedUtc = null,
                CompletedUtc = null,
                LastError = null
            };
        }

        if (duplicate is not null)
        {
            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.duplicate",
                message: $"Reused existing active job ({JobTypeLabels.ForSentence(duplicate.JobType)}) instead of creating duplicate work.",
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
            message: FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson, job.RelatedEntityType),
            detailsJson: ActivityDetailsOf(job.PayloadJson),
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
            FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson, job.RelatedEntityType),
            "job.queued",
            SeverityForCategory("job.queued"),
            now.ToString("O"),
            cancellationToken);
        // A screen listing jobs (Activity) reads again when one is added, not only once it starts (#1180).
        await PublishJobChangedAsync(job, "queued", null, now, cancellationToken);
        // Every open Transfers screen moves the download's row off "Ready to import" at once, not only the tab that clicked (#1209).
        await PublishImportStateAsync(job, "queued", now, cancellationToken);
        DelunoObservability.JobsQueued.Add(1, new("job.type", job.JobType), new("source", job.Source));

        // Only jobs runnable right now should wake a lane. LeaseBatchAsync
        // filters on scheduled_utc <= @now, so a future-dated job would produce
        // a wake-up that finds nothing.
        if (job.ScheduledUtc <= now)
        {
            laneSignal?.Notify(job.JobType);
        }

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

    public async Task<Page<JobQueueItem>> ListPageAsync(PageRequest request, bool failedOnly, CancellationToken cancellationToken, bool includePayload = true)
    {
        var pageSize = request.BoundedPageSize;
        var token = DelunoPageToken.Decode(request.PageToken, 2);
        var jobs = new List<JobQueueItem>(pageSize + 1);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        // The WHERE is built from only the conditions that apply: a "@x IS NULL OR"
        // guard is not seen through by SQLite, and turns the newest-first read into a
        // scan and a sort of every job ever queued (#1081).
        var conditions = new List<string>();
        if (token is not null)
        {
            conditions.Add("(created_utc < @createdUtc OR (created_utc = @createdUtc AND rowid < CAST(@id AS INTEGER)))");
        }

        if (failedOnly)
        {
            conditions.Add("status IN ('failed', 'dead-letter')");
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                id, job_type, source, status, {(includePayload ? "CASE WHEN octet_length(payload_json) <= 65536 THEN payload_json END" : "NULL")}, attempts, created_utc, scheduled_utc,
                started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc, rowid
            FROM job_queue
            {where}
            ORDER BY created_utc DESC, rowid DESC
            LIMIT @take;
            """;

        if (token is not null)
        {
            AddParameter(command, "@createdUtc", token[0]);
            AddParameter(command, "@id", token[1]);
        }

        AddParameter(command, "@take", pageSize + 1);

        // The order of writing, not the random id, settles rows written in one instant; the page token carries it (#1260).
        var rowids = new List<long>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
            rowids.Add(reader.GetInt64(reader.FieldCount - 1));
        }

        var hasMore = jobs.Count > pageSize;
        if (hasMore)
        {
            jobs.RemoveAt(jobs.Count - 1);
        }

        var nextPageToken = hasMore
            ? DelunoPageToken.Encode(jobs[^1].CreatedUtc.ToString("O"), rowids[jobs.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        return Page<JobQueueItem>.Of(await NameEpisodeTargetsAsync(connection, jobs, cancellationToken), nextPageToken);
    }

    /// <summary>
    /// Gives each episode job the show and episode it is for, from its own payload and the series catalogue, so every
    /// screen can say "Severance S02E10" instead of "Search for an episode" twenty times (#1180). The payload is read
    /// here by id because a list leaves it out; a show that is gone leaves the job unnamed rather than failing the list.
    /// </summary>
    private async Task<List<JobQueueItem>> NameEpisodeTargetsAsync(
        System.Data.Common.DbConnection jobsConnection,
        List<JobQueueItem> jobs,
        CancellationToken cancellationToken)
    {
        var episodeJobs = jobs.Where(job => job.JobType == "episode.search").ToList();
        if (episodeJobs.Count == 0)
        {
            return jobs;
        }

        var facts = new Dictionary<string, (string? SeriesId, long? Season, long? Episode)>(StringComparer.Ordinal);
        using (var command = jobsConnection.CreateCommand())
        {
            var names = new List<string>();
            for (var index = 0; index < episodeJobs.Count; index++)
            {
                names.Add($"@j{index}");
                AddParameter(command, $"@j{index}", episodeJobs[index].Id);
            }

            command.CommandText =
                $"""
                SELECT id,
                    CASE WHEN json_valid(payload_json) THEN json_extract(payload_json, '$.seriesId') END,
                    CASE WHEN json_valid(payload_json) THEN json_extract(payload_json, '$.seasonNumber') END,
                    CASE WHEN json_valid(payload_json) THEN json_extract(payload_json, '$.episodeNumber') END
                FROM job_queue
                WHERE id IN ({string.Join(", ", names)});
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                facts[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1)),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)));
            }
        }

        var seriesIds = facts.Values.Select(fact => fact.SeriesId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (seriesIds.Count > 0)
        {
            await using var seriesConnection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
            using var command = seriesConnection.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < seriesIds.Count; index++)
            {
                names.Add($"@s{index}");
                AddParameter(command, $"@s{index}", seriesIds[index]);
            }

            command.CommandText = $"SELECT id, title FROM series_entries WHERE id IN ({string.Join(", ", names)});";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                titles[reader.GetString(0)] = reader.GetString(1);
            }
        }

        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            if (!facts.TryGetValue(job.Id, out var fact) || fact.SeriesId is null || !titles.TryGetValue(fact.SeriesId, out var title))
            {
                continue;
            }

            var number = fact.Season is { } season && fact.Episode is { } episode ? $" S{season:D2}E{episode:D2}" : string.Empty;
            jobs[index] = job with { Target = title + number, ShowId = fact.SeriesId, ShowTitle = title };
        }

        return jobs;
    }

    /// <summary>Says what the retry did, including what it deliberately left alone.</summary>
    private static string DescribeRetry(int requeued, int selected)
    {
        var jobs = requeued == 1 ? "job" : "jobs";
        var duplicates = selected - requeued;
        return duplicates <= 0
            ? $"{requeued} failed {jobs} requeued."
            : $"{requeued} failed {jobs} requeued, {duplicates} duplicate{(duplicates == 1 ? string.Empty : "s")} left alone.";
    }

    public Task<int> RetryFailedAsync(CancellationToken cancellationToken)
        => RetryFailedCoreAsync(importForDispatchId: null, onlyJobId: null, cancellationToken);

    public Task<int> RetryFailedJobAsync(string jobId, CancellationToken cancellationToken)
        => RetryFailedCoreAsync(importForDispatchId: null, onlyJobId: jobId, cancellationToken);

    public Task<int> RetryFailedImportAsync(string dispatchId, CancellationToken cancellationToken)
        => RetryFailedCoreAsync(dispatchId, onlyJobId: null, cancellationToken);

    /// <param name="importForDispatchId">
    /// Only the failed import of that download: what Try again on its recovery
    /// case means (#558), rather than every failed job.
    /// </param>
    private async Task<int> RetryFailedCoreAsync(string? importForDispatchId, string? onlyJobId, CancellationToken cancellationToken)
    {
        var scope = importForDispatchId is null
            ? string.Empty
            : " AND job_type = 'filesystem.import.execute' AND CASE WHEN json_valid(payload_json) THEN json_extract(payload_json, '$.DispatchId') END = @dispatchId";
        if (onlyJobId is not null)
        {
            scope += " AND id = @onlyJobId";
        }

        var now = timeProvider.GetUtcNow();
        var failedJobs = new List<JobQueueItem>();
        var requeued = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                $"""
                SELECT
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                    idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
                FROM job_queue
                WHERE status IN ('failed', 'dead-letter'){scope}
                ORDER BY completed_utc DESC, created_utc DESC;
                """;
            if (importForDispatchId is not null)
            {
                AddParameter(select, "@dispatchId", importForDispatchId);
            }
            if (onlyJobId is not null)
            {
                AddParameter(select, "@onlyJobId", onlyJobId);
            }

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
                $"""
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
                WHERE status IN ('failed', 'dead-letter'){scope}
                  -- One row per dedupe key, or this violates
                  -- ux_job_queue_active_dedupe the moment it commits.
                  --
                  -- 'dead-letter' is deliberately outside that index, so any
                  -- number of dead-lettered rows may share a key - and they
                  -- routinely do, because dead-lettering is what happens after
                  -- repeated attempts at the same work. Promoting them all to
                  -- 'queued' put them in the index together and the whole
                  -- statement failed, which meant Retry returned a 500 exactly
                  -- when there was a pile worth retrying. The newest row wins;
                  -- the older ones are the same work and stay where they are.
                  AND (
                      dedupe_key IS NULL
                      OR id = (
                          SELECT inner_job.id
                          FROM job_queue AS inner_job
                          WHERE inner_job.dedupe_key = job_queue.dedupe_key
                            AND inner_job.status IN ('failed', 'dead-letter')
                          ORDER BY inner_job.completed_utc DESC, inner_job.created_utc DESC, inner_job.rowid DESC
                          LIMIT 1
                      )
                  );
                """;

            AddParameter(update, "@scheduledUtc", now.ToString("O"));
            if (importForDispatchId is not null)
            {
                AddParameter(update, "@dispatchId", importForDispatchId);
            }
            if (onlyJobId is not null)
            {
                AddParameter(update, "@onlyJobId", onlyJobId);
            }

            // What the statement actually requeued, which is not the same as
            // what was selected: duplicates sharing a dedupe key stay put.
            requeued = await update.ExecuteNonQueryAsync(cancellationToken);
        }

        // The duplicates that were not promoted are the same work as the row
        // that was, so they are removed rather than left dead-lettered for
        // ever. Leaving them was what made Retry look broken: on the lab rig
        // 459 rows shared one dedupe key, so pressing the button the dashboard
        // sends you to promoted one and left 458 exactly where they were.
        //
        // Enqueue no longer creates these, so this only clears what earlier
        // builds accumulated. Their history stays in the activity feed; the
        // rows carry nothing the promoted one does not.
        var discarded = 0;
        using (var discard = connection.CreateCommand())
        {
            discard.Transaction = transaction;
            discard.CommandText =
                """
                DELETE FROM job_queue
                WHERE status = 'dead-letter'
                  AND dedupe_key IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM job_queue AS promoted
                      WHERE promoted.dedupe_key = job_queue.dedupe_key
                        AND promoted.status = 'queued'
                        AND promoted.scheduled_utc = @scheduledUtc
                  );
                """;
            AddParameter(discard, "@scheduledUtc", now.ToString("O"));
            discarded = await discard.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.retry",
            message: DescribeRetry(requeued, failedJobs.Count),
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
            message: $"{DescribeRetry(requeued, failedJobs.Count)}",
            detailsJson: JsonSerializer.Serialize(new DecisionExplanationPayload(
                Scope: "job.retry",
                Status: "requeued",
                Reason: $"{DescribeRetry(requeued, failedJobs.Count)} {discarded} duplicate row(s) sharing a dedupe key were discarded, because they are the same work as one that was requeued.",
                Inputs: new Dictionary<string, string?>
                {
                    ["failedJobCount"] = failedJobs.Count.ToString(CultureInfo.InvariantCulture),
                    ["requeuedCount"] = requeued.ToString(CultureInfo.InvariantCulture),
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
            DescribeRetry(requeued, failedJobs.Count),
            "job.retry",
            SeverityForCategory("job.retry"),
            now.ToString("O"),
            cancellationToken);

        DelunoObservability.JobRetries.Add(requeued);
        return requeued;
    }

    public async Task<JobQueueItem?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;

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
            WHERE id = @id;
            """;
        AddParameter(command, "@id", jobId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<JobQueueItem?> DismissFailedAsync(string jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;

        var now = timeProvider.GetUtcNow();
        JobQueueItem? job = null;

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
                WHERE id = @id
                  AND status IN ('failed', 'dead-letter');
                """;
            AddParameter(select, "@id", jobId);

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                job = ReadJob(reader);
            }
        }

        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // The status is checked again so a job a worker picked back up
            // between the read and here is left running.
            delete.CommandText =
                """
                DELETE FROM job_queue
                WHERE id = @id
                  AND status IN ('failed', 'dead-letter');
                """;
            AddParameter(delete, "@id", jobId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        // The row goes, so the feed is where what it was and why it failed
        // stays readable. It is filed under the title or library it was for,
        // not under the job id, which no longer exists.
        var message = $"Dismissed the job \"{JobTypeLabels.For(job.JobType)}\", which had failed.";
        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.dismissed",
            message: message,
            detailsJson: JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["jobId"] = job.Id,
                ["jobType"] = job.JobType,
                ["source"] = job.Source,
                ["status"] = job.Status,
                ["attempts"] = job.Attempts.ToString(CultureInfo.InvariantCulture),
                ["lastError"] = job.LastError
            }),
            relatedJobId: null,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            message,
            "job.dismissed",
            SeverityForCategory("job.dismissed"),
            now.ToString("O"),
            cancellationToken);

        return job;
    }

    public async Task<int> PruneCompletedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM job_queue
            WHERE status = 'completed'
              AND completed_utc IS NOT NULL
              AND completed_utc < @olderThan;
            """;
        AddParameter(command, "@olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PruneActivityAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM activity_events
            WHERE created_utc < @olderThan;
            """;
        AddParameter(command, "@olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CancelPendingForRelatedEntityAsync(
        string relatedEntityType,
        string relatedEntityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relatedEntityType) || string.IsNullOrWhiteSpace(relatedEntityId)) return 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var cancelled = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT id
                FROM job_queue
                WHERE related_entity_type = @relatedEntityType
                  AND related_entity_id = @relatedEntityId
                  AND status IN ('queued', 'failed', 'dead-letter');
                """;
            AddParameter(select, "@relatedEntityType", relatedEntityType);
            AddParameter(select, "@relatedEntityId", relatedEntityId);
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cancelled.Add(reader.GetString(0));
        }

        if (cancelled.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM job_queue
                WHERE related_entity_type = @relatedEntityType
                  AND related_entity_id = @relatedEntityId
                  AND status IN ('queued', 'failed', 'dead-letter');
                """;
            AddParameter(delete, "@relatedEntityType", relatedEntityType);
            AddParameter(delete, "@relatedEntityId", relatedEntityId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "job.cancelled",
            message: $"{cancelled.Count} pending job{(cancelled.Count == 1 ? string.Empty : "s")} were cancelled because the related title was removed.",
            detailsJson: null,
            relatedJobId: null,
            relatedEntityType: relatedEntityType,
            relatedEntityId: relatedEntityId,
            createdUtc: timeProvider.GetUtcNow(),
            cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var jobId in cancelled)
        {
            await realtimeEventPublisher.PublishQueueItemRemovedAsync(jobId, cancellationToken);
        }

        return cancelled.Count;
    }

    public async Task<JobQueueItem?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? jobTypes,
        CancellationToken cancellationToken)
    {
        var leased = await LeaseBatchAsync(workerId, leaseDuration, jobTypes, 1, cancellationToken);
        return leased.Count == 0 ? null : leased[0];
    }

    public async Task<IReadOnlyList<JobQueueItem>> ReleaseLeasesAsync(
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken)
    {
        var distinctWorkerIds = workerIds
            .Where(workerId => !string.IsNullOrWhiteSpace(workerId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctWorkerIds.Length == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);

        var released = new List<JobQueueItem>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            var placeholders = new List<string>(distinctWorkerIds.Length);
            for (var index = 0; index < distinctWorkerIds.Length; index++)
            {
                var parameterName = $"@workerId{index}";
                placeholders.Add(parameterName);
                AddParameter(select, parameterName, distinctWorkerIds[index]);
            }

            select.CommandText =
                $"""
                SELECT
                    id, job_type, source, status, payload_json, attempts, created_utc, scheduled_utc,
                    started_utc, completed_utc, leased_until_utc, worker_id, last_error, related_entity_type, related_entity_id,
                    idempotency_key, dedupe_key, max_attempts, last_attempt_utc, next_attempt_utc
                FROM job_queue
                WHERE status = 'running'
                  AND worker_id IN ({string.Join(", ", placeholders)})
                ORDER BY scheduled_utc ASC, created_utc ASC;
                """;

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                released.Add(ReadJob(reader));
            }
        }

        foreach (var job in released)
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE job_queue
                    SET
                        status = 'queued',
                        started_utc = NULL,
                        completed_utc = NULL,
                        scheduled_utc = @scheduledUtc,
                        leased_until_utc = NULL,
                        worker_id = NULL,
                        last_error = NULL,
                        next_attempt_utc = @nextAttemptUtc
                    WHERE id = @id
                      AND status = 'running';
                    """;
                AddParameter(update, "@id", job.Id);
                AddParameter(update, "@scheduledUtc", now.ToString("O"));
                AddParameter(update, "@nextAttemptUtc", now.ToString("O"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await MarkAutomationStateQueuedAsync(connection, transaction, job, now, cancellationToken);
            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.lease-released",
                message: $"{JobTypeLabels.For(job.JobType)}: released after this worker restarted.",
                detailsJson: job.PayloadJson,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var job in released)
        {
            await realtimeEventPublisher.PublishQueueItemAddedAsync(
                job.Id,
                FormatQueuedTitle(job.JobType, job.PayloadJson),
                job.RelatedEntityType ?? "job",
                "queued",
                cancellationToken);
            if (LibrarySearchJobTypes.IsLibrarySearch(job.JobType) && string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(job.RelatedEntityId))
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", job.RelatedEntityId, cancellationToken);
            }
            await realtimeEventPublisher.PublishActivityEventAddedAsync(
                Guid.CreateVersion7().ToString("N"),
                $"{JobTypeLabels.For(job.JobType)}: released after this worker restarted.",
                "job.lease-released",
                SeverityForCategory("job.lease-released"),
                now.ToString("O"),
                cancellationToken);
        }

        return released.Select(job => job with
        {
            Status = "queued",
            StartedUtc = null,
            CompletedUtc = null,
            ScheduledUtc = now,
            LeasedUntilUtc = null,
            WorkerId = null,
            LastError = null,
            NextAttemptUtc = now
        }).ToArray();
    }

    /// <summary>
    /// Leases up to <paramref name="maxJobs"/> jobs in a single transaction.
    ///
    /// A worker lane used to lease exactly one job per timer tick, which made
    /// sustained throughput a function of the tick interval rather than of the
    /// machine — the 2-second import lane could not exceed 30 jobs a minute no
    /// matter how much work was queued or how fast the host was.
    ///
    /// Leasing a batch also amortises the write lock: one transaction claims N
    /// jobs instead of N transactions claiming one each.
    /// </summary>
    public async Task<IReadOnlyList<JobQueueItem>> LeaseBatchAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? jobTypes,
        int maxJobs,
        CancellationToken cancellationToken)
    {
        if (maxJobs <= 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        // IMMEDIATE, explicitly. This transaction reads candidates and then
        // writes them, and a DEFERRED transaction that upgrades a read lock to
        // a write lock gets SQLITE_BUSY straight away — busy_timeout does not
        // cover lock upgrades, because waiting could deadlock. Taking the write
        // lock up front is what makes concurrent lanes queue politely instead
        // of failing.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);

        await RecoverExpiredLeasesAsync(connection, transaction, now, cancellationToken);

        var candidates = new List<JobQueueItem>();

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
                LIMIT @maxJobs;
                """;

            AddParameter(select, "@now", now.ToString("O"));
            AddParameter(select, "@maxJobs", maxJobs);

            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(ReadJob(reader));
            }
        }

        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var leasedJobs = new List<JobQueueItem>(candidates.Count);

        foreach (var candidate in candidates)
        {
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
                message: FormatStartedMessage(leasedJob.JobType, leasedJob.PayloadJson, leasedJob.RelatedEntityType),
                detailsJson: leasedJob.PayloadJson,
                relatedJobId: leasedJob.Id,
                relatedEntityType: leasedJob.RelatedEntityType,
                relatedEntityId: leasedJob.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);

            leasedJobs.Add(leasedJob);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var libraryId in leasedJobs
            .Where(job => LibrarySearchJobTypes.IsLibrarySearch(job.JobType) && string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
            .Select(job => job.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", libraryId!, cancellationToken);
        }
        foreach (var started in leasedJobs)
        {
            await PublishJobChangedAsync(started, "started", null, now, cancellationToken);
            await PublishImportStateAsync(started, "running", now, cancellationToken);
        }
        return leasedJobs;
    }

    public async Task<DateTimeOffset?> NextDueUtcAsync(
        IReadOnlyList<string> jobTypes,
        CancellationToken cancellationToken)
    {
        if (jobTypes.Count == 0)
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        var jobTypeFilter = BuildJobTypeFilter(command, jobTypes);
        // The same predicate the lease uses, minus the "<= now" — the lease asks
        // "what can I run", this asks "when could I next run something". Written
        // beside it on purpose: two queries that disagree about what runnable
        // means would have a lane sleeping through work it was holding.
        command.CommandText =
            $"""
            SELECT MIN(scheduled_utc)
            FROM job_queue
            WHERE status IN ('queued', 'failed')
              AND attempts < max_attempts
              {jobTypeFilter};
            """;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? ParseTimestamp(text)
            : null;
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
        await MarkSubtitlesDueAfterImportAsync(connection, transaction, job, cancellationToken);

        var quietCompletion = JobActivityQuiet.IsQuiet(job.PayloadJson);
        if (!quietCompletion)
        {
            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.completed",
                message: completionMessage ?? $"{JobTypeLabels.For(job.JobType)} finished.",
                detailsJson: job.PayloadJson,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (LibrarySearchJobTypes.IsLibrarySearch(job.JobType) && string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(job.RelatedEntityId))
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", job.RelatedEntityId, cancellationToken);
        await realtimeEventPublisher.PublishQueueItemRemovedAsync(job.Id, cancellationToken);
        if (!quietCompletion)
        {
            await realtimeEventPublisher.PublishActivityEventAddedAsync(
                Guid.CreateVersion7().ToString("N"),
                completionMessage ?? $"{JobTypeLabels.For(job.JobType)} finished.",
                "job.completed",
                SeverityForCategory("job.completed"),
                now.ToString("O"),
                cancellationToken);
        }
        if (job.JobType == "filesystem.import.execute")
        {
            await realtimeEventPublisher.PublishImportStateChangedAsync(
                job.Id,
                "completed",
                job.RelatedEntityType,
                job.RelatedEntityId,
                completionMessage,
                null,
                now.ToString("O"),
                cancellationToken);
        }
        await PublishJobChangedAsync(job, "finished", completionMessage, now, cancellationToken);
        DelunoObservability.JobsCompleted.Add(1, new("job.type", job.JobType), new("source", job.Source));
    }

    /// <summary>An import job queued or started is said on the queue group like one that finished or failed, so no screen waits for its next read (#1209).</summary>
    private Task PublishImportStateAsync(JobQueueItem job, string state, DateTimeOffset at, CancellationToken cancellationToken)
        => job.JobType == "filesystem.import.execute"
            ? realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishImportStateChangedAsync(
                job.Id, state, job.RelatedEntityType, job.RelatedEntityId, null, null, at.ToString("O"), cancellationToken))
            : Task.CompletedTask;

    /// <summary>Says on Live, at the moment it happens, that a job started, finished or failed (#1097).</summary>
    private Task PublishJobChangedAsync(JobQueueItem job, string state, string? message, DateTimeOffset at, CancellationToken cancellationToken)
        => realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishJobChangedAsync(
            new JobChangedEvent(
                job.Id,
                job.JobType,
                state,
                JobTypeLabels.For(job.JobType),
                string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase) ? job.RelatedEntityId : null,
                message,
                at.ToString("O")),
            cancellationToken));

    public async Task DeferAsync(string jobId, string workerId, DateTimeOffset runAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var update = connection.CreateCommand();
        // Only the lease that is being handed back: a job another worker has
        // since taken, or one already finished, is left as it is.
        update.CommandText =
            """
            UPDATE job_queue
            SET
                status = 'queued',
                attempts = MAX(attempts - 1, 0),
                scheduled_utc = @runAtUtc,
                leased_until_utc = NULL,
                worker_id = NULL,
                next_attempt_utc = NULL,
                last_error = NULL
            WHERE id = @id AND status = 'running' AND worker_id = @workerId;
            """;
        AddParameter(update, "@id", jobId);
        AddParameter(update, "@workerId", workerId);
        AddParameter(update, "@runAtUtc", runAtUtc.ToString("O"));
        var handedBack = await update.ExecuteNonQueryAsync(cancellationToken);

        // Running -> waiting is a change a screen must hear: without it a deferred search kept reading "running" until some
        // other job happened to start or finish (#1180).
        if (handedBack > 0 && await GetAsync(jobId, cancellationToken) is { } deferred)
        {
            await PublishJobChangedAsync(deferred, "waiting", null, timeProvider.GetUtcNow(), cancellationToken);
        }
    }

    public async Task FailAsync(string jobId, string workerId, string errorMessage, CancellationToken cancellationToken, string? rawDetail = null)
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
        var retryDelay = CalculateRetryDelay(attempts);
        var nextAttemptUtc = shouldDeadLetter ? (DateTimeOffset?)null : now.Add(retryDelay);
        var nextStatus = shouldDeadLetter ? "dead-letter" : "failed";
        var storedError = shouldDeadLetter
            ? $"{errorMessage} {GaveUpSentence(job.MaxAttempts)}"
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
                ? $"{errorMessage} {GaveUpSentence(job.MaxAttempts)}"
                : $"{errorMessage} {PlainFailure.RetrySentence(retryDelay)}",
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
                ? $"{JobTypeLabels.For(job.JobType)} moved to dead-letter after exhausting retries."
                : $"{JobTypeLabels.For(job.JobType)} will try again {PlainFailure.InWords(retryDelay)}.",
            detailsJson: JsonSerializer.Serialize(new DecisionExplanationPayload(
                Scope: "job.failure",
                Status: nextStatus,
                Reason: shouldDeadLetter
                    ? $"{JobTypeLabels.For(job.JobType)} reached the retry limit of {job.MaxAttempts} attempts and was moved to dead-letter."
                    : $"{JobTypeLabels.For(job.JobType)} failed on attempt {attempts}; Deluno scheduled the next attempt after exponential backoff.",
                Inputs: new Dictionary<string, string?>
                {
                    ["jobId"] = job.Id,
                    ["jobType"] = job.JobType,
                    ["attempts"] = attempts.ToString(CultureInfo.InvariantCulture),
                    ["maxAttempts"] = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
                    ["error"] = rawDetail ?? errorMessage,
                    ["nextAttemptUtc"] = nextAttemptUtc?.ToString("O")
                },
                Outcome: shouldDeadLetter
                    ? "No further automatic retries will run until the user retries failed jobs."
                    : $"Next automatic retry is scheduled {PlainFailure.InWords(retryDelay)}.",
                Alternatives: [])),
            relatedJobId: job.Id,
            relatedEntityType: job.RelatedEntityType,
            relatedEntityId: job.RelatedEntityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (LibrarySearchJobTypes.IsLibrarySearch(job.JobType) && string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(job.RelatedEntityId))
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", job.RelatedEntityId, cancellationToken);
        await realtimeEventPublisher.PublishQueueItemRemovedAsync(job.Id, cancellationToken);
        await realtimeEventPublisher.PublishQueueItemStatusChangedAsync(
            job.Id,
            nextStatus,
            storedError,
            cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            shouldDeadLetter
                ? $"{errorMessage} {GaveUpSentence(job.MaxAttempts)}"
                : $"{errorMessage} {PlainFailure.RetrySentence(retryDelay)}",
            shouldDeadLetter ? "job.dead-letter" : "job.failed",
            SeverityForCategory(shouldDeadLetter ? "job.dead-letter" : "job.failed"),
            now.ToString("O"),
            cancellationToken);
        if (job.JobType == "filesystem.import.execute")
        {
            await realtimeEventPublisher.PublishImportStateChangedAsync(
                job.Id,
                shouldDeadLetter ? "failed" : "retrying",
                job.RelatedEntityType,
                job.RelatedEntityId,
                null,
                storedError,
                now.ToString("O"),
                cancellationToken);
        }
        await PublishJobChangedAsync(job, shouldDeadLetter ? "failed" : "retrying", storedError, now, cancellationToken);
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

    public async Task<IReadOnlyDictionary<string, int>> CountJobsByStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Counted in SQL, over every row. A page cannot answer "how many" —
        // it can only answer "how many of the ones I looked at", which is a
        // different question that reads identically on a dashboard.
        command.CommandText = "SELECT status, COUNT(*) FROM job_queue GROUP BY status;";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    public async Task<int> CountActiveJobsAsync(string jobType, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Mirrors LeaseBatchAsync's runnable predicate, plus jobs already
        // leased. A job that has exhausted its attempts is deliberately
        // excluded -- counting permanently-failed rows would inflate the depth
        // forever and silently stall any planner topping up against it.
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM job_queue
            WHERE job_type = @jobType
              AND (
                    status = 'running'
                    OR (status IN ('queued', 'failed') AND attempts < max_attempts)
                  );
            """;

        AddParameter(command, "@jobType", jobType);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> TryClaimScheduledPassAsync(string scheduleKey, TimeSpan interval, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - interval;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO worker_schedule_state (schedule_key, last_run_utc, last_completed_utc, last_result, last_duration_ms, next_run_utc, interval_seconds)
            VALUES (@scheduleKey, @now, NULL, 'running', NULL, @nextRun, @intervalSeconds)
            ON CONFLICT(schedule_key) DO UPDATE SET
                last_run_utc = excluded.last_run_utc,
                last_completed_utc = NULL,
                last_result = 'running',
                last_duration_ms = NULL,
                next_run_utc = excluded.next_run_utc,
                interval_seconds = excluded.interval_seconds
            WHERE worker_schedule_state.last_run_utc <= @cutoff;
            """;

        AddParameter(command, "@scheduleKey", scheduleKey);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@cutoff", cutoff.ToString("O"));
        AddParameter(command, "@nextRun", now.Add(interval).ToString("O"));
        // Recorded so the System screen can report the cadence in use rather
        // than the one declared — they differ wherever the user has a say.
        AddParameter(command, "@intervalSeconds", (long)interval.TotalSeconds);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rowsAffected > 0)
        {
            await realtimeEventPublisher.PublishSystemTaskChangedAsync(scheduleKey, cancellationToken);
        }

        return rowsAffected > 0;
    }

    public async Task RecordScheduledPassOutcomeAsync(
        string scheduleKey,
        DateTimeOffset completedUtc,
        string result,
        long durationMs,
        DateTimeOffset? nextRunUtc,
        CancellationToken cancellationToken,
        string? failureReason = null)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE worker_schedule_state
            SET last_completed_utc = @completedUtc,
                last_result = @result,
                last_duration_ms = @durationMs,
                next_run_utc = COALESCE(@nextRunUtc, next_run_utc),
                last_error = @lastError
            WHERE schedule_key = @scheduleKey;
            """;
        AddParameter(command, "@scheduleKey", scheduleKey);
        AddParameter(command, "@completedUtc", completedUtc.ToString("O"));
        AddParameter(command, "@result", string.IsNullOrWhiteSpace(result) ? "completed" : result.Trim().ToLowerInvariant());
        AddParameter(command, "@durationMs", Math.Max(0, durationMs));
        AddParameter(command, "@nextRunUtc", nextRunUtc?.ToString("O"));
        AddParameter(command, "@lastError", string.Equals(result, "failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(failureReason) ? failureReason.Trim() : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await realtimeEventPublisher.PublishSystemTaskChangedAsync(scheduleKey, cancellationToken);
    }

    public async Task<IReadOnlyList<SystemTaskState>> ListSystemTaskStatesAsync(CancellationToken cancellationToken)
    {
        var states = new List<SystemTaskState>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT schedule_key, last_run_utc, last_completed_utc, last_result, last_duration_ms, next_run_utc, interval_seconds, last_error
            FROM worker_schedule_state;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new SystemTaskState(
                ScheduleKey: reader.GetString(0),
                LastStartedUtc: DateTimeOffset.Parse(reader.GetString(1)),
                LastCompletedUtc: reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                LastResult: reader.IsDBNull(3) ? null : reader.GetString(3),
                LastDurationMs: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                NextRunUtc: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                IntervalSeconds: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                LastError: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return states;
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
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc,
                next_missing_search_utc, next_upgrade_search_utc, next_subtitle_search_utc
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

    public async Task<Page<LibraryAutomationStateItem>> ListLibraryAutomationStatesPageAsync(
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var pageSize = request.BoundedPageSize;
        var token = DelunoPageToken.Decode(request.PageToken, 3);
        var items = new List<LibraryAutomationStateItem>(pageSize + 1);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                library_id, library_name, media_type, status, search_requested, last_planned_utc,
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc,
                next_missing_search_utc, next_upgrade_search_utc, next_subtitle_search_utc
            FROM library_automation_state
            WHERE (@mediaType IS NULL
                OR media_type > @mediaType
                OR (media_type = @mediaType AND library_name > @libraryName)
                OR (media_type = @mediaType AND library_name = @libraryName AND library_id > @libraryId))
            ORDER BY media_type ASC, library_name ASC, library_id ASC
            LIMIT @take;
            """;

        AddParameter(command, "@mediaType", token?[0]);
        AddParameter(command, "@libraryName", token?[1]);
        AddParameter(command, "@libraryId", token?[2]);
        AddParameter(command, "@take", pageSize + 1);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadLibraryAutomationState(reader));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextPageToken = hasMore
            ? DelunoPageToken.Encode(items[^1].MediaType, items[^1].LibraryName, items[^1].LibraryId)
            : null;
        return Page<LibraryAutomationStateItem>.Of(items, nextPageToken);
    }

    public async Task EnsureLibraryAutomationStateAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertLibraryAutomationStateAsync(connection, transaction, library, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveLibraryAutomationStateAsync(
        string libraryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM library_automation_state WHERE library_id = @libraryId;";
        AddParameter(command, "@libraryId", libraryId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
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
                planned_count, queued_count, skipped_count, notes_json, started_utc, completed_utc, search_kind
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

    public async Task<Page<SearchCycleRunItem>> ListSearchCycleRunsPageAsync(
        PageRequest request,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        var pageSize = request.BoundedPageSize;
        var token = DelunoPageToken.Decode(request.PageToken, 2);
        var items = new List<SearchCycleRunItem>(pageSize + 1);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, library_name, media_type, trigger_kind, status,
                planned_count, queued_count, skipped_count, notes_json, started_utc, completed_utc, search_kind, rowid
            FROM search_cycle_runs
            WHERE (@libraryId IS NULL OR library_id = @libraryId)
              AND (@startedUtc IS NULL OR started_utc < @startedUtc OR (started_utc = @startedUtc AND rowid < CAST(@id AS INTEGER)))
            ORDER BY started_utc DESC, rowid DESC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", string.IsNullOrWhiteSpace(libraryId) ? null : libraryId);
        AddParameter(command, "@startedUtc", token?[0]);
        AddParameter(command, "@id", token?[1]);
        AddParameter(command, "@take", pageSize + 1);

        var rowids = new List<long>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSearchCycleRun(reader));
            rowids.Add(reader.GetInt64(reader.FieldCount - 1));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextPageToken = hasMore
            ? DelunoPageToken.Encode(items[^1].StartedUtc.ToString("O"), rowids[items.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        return Page<SearchCycleRunItem>.Of(items, nextPageToken);
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

    public async Task<Page<SearchRetryWindowItem>> ListSearchRetryWindowsPageAsync(
        PageRequest request,
        string? libraryId,
        CancellationToken cancellationToken)
    {
        var pageSize = request.BoundedPageSize;
        var token = DelunoPageToken.Decode(request.PageToken, 6);
        var items = new List<SearchRetryWindowItem>(pageSize + 1);

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
              AND (@nextEligibleUtc IS NULL
                OR next_eligible_utc > @nextEligibleUtc
                OR (next_eligible_utc = @nextEligibleUtc AND updated_utc < @updatedUtc)
                OR (next_eligible_utc = @nextEligibleUtc AND updated_utc = @updatedUtc AND entity_type > @entityType)
                OR (next_eligible_utc = @nextEligibleUtc AND updated_utc = @updatedUtc AND entity_type = @entityType AND entity_id > @entityId)
                OR (next_eligible_utc = @nextEligibleUtc AND updated_utc = @updatedUtc AND entity_type = @entityType AND entity_id = @entityId AND library_id > @tokenLibraryId)
                OR (next_eligible_utc = @nextEligibleUtc AND updated_utc = @updatedUtc AND entity_type = @entityType AND entity_id = @entityId AND library_id = @tokenLibraryId AND action_kind > @actionKind))
            ORDER BY next_eligible_utc ASC, updated_utc DESC, entity_type ASC, entity_id ASC, library_id ASC, action_kind ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", string.IsNullOrWhiteSpace(libraryId) ? null : libraryId);
        AddParameter(command, "@nextEligibleUtc", token?[0]);
        AddParameter(command, "@updatedUtc", token?[1]);
        AddParameter(command, "@entityType", token?[2]);
        AddParameter(command, "@entityId", token?[3]);
        AddParameter(command, "@tokenLibraryId", token?[4]);
        AddParameter(command, "@actionKind", token?[5]);
        AddParameter(command, "@take", pageSize + 1);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSearchRetryWindow(reader));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var nextPageToken = hasMore
            ? DelunoPageToken.Encode(
                items[^1].NextEligibleUtc.ToString("O"),
                items[^1].UpdatedUtc.ToString("O"),
                items[^1].EntityType,
                items[^1].EntityId,
                items[^1].LibraryId,
                items[^1].ActionKind)
            : null;
        return Page<SearchRetryWindowItem>.Of(items, nextPageToken);
    }

    public async Task<IReadOnlyList<DownloadDispatchItem>> ListDownloadDispatchesAsync(
        int take,
        string? mediaType,
        CancellationToken cancellationToken,
        bool includeArchived = false,
        string? clientId = null)
    {
        var (items, _) = await downloadDispatchesRepository.QueryDispatchesAsync(
            new DispatchQueryFilter
            {
                MediaType = string.IsNullOrWhiteSpace(mediaType) ? null : mediaType.Trim().ToLowerInvariant(),
                IncludeArchived = includeArchived,
                ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId
            },
            new DispatchPaginationOptions { PageSize = take },
            cancellationToken);

        return items;
    }

    public Task<IReadOnlyList<string>> FindGrabbedReleaseNamesAsync(
        IReadOnlyCollection<string> candidateNames,
        CancellationToken cancellationToken)
        => downloadDispatchesRepository.FindGrabbedReleaseNamesAsync(candidateNames, cancellationToken);

    public Task<IReadOnlyList<DownloadInFlight>> ListDownloadsInFlightAsync(
        int limit,
        CancellationToken cancellationToken)
        => downloadDispatchesRepository.ListDownloadsInFlightAsync(limit, cancellationToken);

    public async Task<Page<DownloadDispatchItem>> ListDownloadDispatchesPageAsync(
        PageRequest request,
        string? mediaType,
        CancellationToken cancellationToken,
        bool includeArchived = false,
        string? entityId = null,
        bool includeRawJson = false)
    {
        var (items, nextPageToken) = await downloadDispatchesRepository.QueryDispatchesAsync(
            new DispatchQueryFilter
            {
                MediaType = string.IsNullOrWhiteSpace(mediaType) ? null : mediaType.Trim().ToLowerInvariant(),
                EntityId = string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim(),
                IncludeArchived = includeArchived,
                // A grab's notes and response are copies of one search plan and can
                // run to a megabyte a row; a list leaves them out unless asked (#1081).
                OmitLargeJson = !includeRawJson
            },
            new DispatchPaginationOptions
            {
                PageSize = request.BoundedPageSize,
                PageToken = request.PageToken
            },
            cancellationToken);

        return Page<DownloadDispatchItem>.Of(items, nextPageToken);
    }

    /// <summary>
    /// The dispatch behind a release a download client is holding.
    ///
    /// The window used to be six hours, which quietly excluded the case that
    /// matters most: a large torrent takes longer than that to download, so by
    /// the time it had imported and started sharing, nothing could find the
    /// dispatch and the sharing rule skipped it entirely (#287). It is bounded
    /// only to keep the scan off very old rows; the client id and release name
    /// together are specific enough, and the newest match wins.
    /// </summary>
    private const string DispatchLinkColumns =
        """
        id, entity_type, entity_id, indexer_name, library_id,
        replacement_authorized, force_replacement_authorized, replacement_expected_path,
        replacement_targets_json, indexer_id
        """;

    /// <summary>
    /// The grab behind a download, by name (#617). The exact name first; then
    /// the same name whatever its case and punctuation, because a client
    /// reports the torrent's own name ("…x264-nikt0") where the grab kept the
    /// indexer's ("…x264 nikt0"), and an exact match made the first real film
    /// import as a new title named after its file.
    /// </summary>
    public async Task<DispatchCatalogueLink?> FindRecentDispatchLinkAsync(
        string downloadClientId,
        string releaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using (var exact = connection.CreateCommand())
        {
            exact.CommandText =
                $"""
                SELECT {DispatchLinkColumns}
                FROM download_dispatches
                WHERE download_client_id = @downloadClientId
                  AND release_name = @releaseName
                  AND created_utc > datetime('now', '-90 days')
                ORDER BY created_utc DESC
                LIMIT 1;
                """;
            AddParameter(exact, "@downloadClientId", downloadClientId);
            AddParameter(exact, "@releaseName", releaseName);
            using var reader = await exact.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadDispatchLink(reader);
            }
        }

        var wanted = ComparableReleaseName(releaseName);
        if (wanted.Length == 0)
        {
            return null;
        }

        using var loose = connection.CreateCommand();
        loose.CommandText =
            $"""
            SELECT {DispatchLinkColumns}, release_name
            FROM download_dispatches
            WHERE download_client_id = @downloadClientId
              AND created_utc > datetime('now', '-90 days')
            ORDER BY created_utc DESC
            LIMIT 500;
            """;
        AddParameter(loose, "@downloadClientId", downloadClientId);
        // Last, the title and year each name gives: a tracker lists "House on
        // Haunted Hill (1959) [P&M] 1080p Blu-Ray (6.6GB)" for a torrent named
        // "house.on.-haunted.-hill.-1959.1080p…", and only a hash, which an
        // older grab did not keep, could otherwise tie the two (#617).
        var (wantedTitle, wantedYear) = ReleaseTitleMatch.ParseTitleAndYear(releaseName);
        var wantedKey = wantedYear is null ? null : ReleaseTitleMatch.Clean(wantedTitle);
        DispatchCatalogueLink? sameTitleAndYear = null;
        using var candidates = await loose.ExecuteReaderAsync(cancellationToken);
        while (await candidates.ReadAsync(cancellationToken))
        {
            if (candidates.IsDBNull(10))
            {
                continue;
            }

            var candidateName = candidates.GetString(10);
            if (ComparableReleaseName(candidateName) == wanted)
            {
                return ReadDispatchLink(candidates);
            }

            if (sameTitleAndYear is null && wantedKey is { Length: > 0 })
            {
                var (title, year) = ReleaseTitleMatch.ParseTitleAndYear(candidateName);
                if (year == wantedYear && ReleaseTitleMatch.Clean(title) == wantedKey)
                {
                    sameTitleAndYear = ReadDispatchLink(candidates);
                }
            }
        }

        return sameTitleAndYear;
    }

    /// <summary>
    /// The grab behind a download, by the id its client gave it: a torrent's
    /// hash, a Transmission id (#617). Names can differ; this cannot.
    /// </summary>
    public async Task<DispatchCatalogueLink?> FindDispatchLinkByClientItemAsync(
        string downloadClientId,
        string clientItemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientItemId))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {DispatchLinkColumns}
            FROM download_dispatches
            WHERE download_client_id = @downloadClientId
              AND torrent_hash_or_item_id = @clientItemId COLLATE NOCASE
            ORDER BY CASE WHEN entity_type = 'release' THEN 1 ELSE 0 END, created_utc DESC
            LIMIT 1;
            """;
        AddParameter(command, "@downloadClientId", downloadClientId);
        AddParameter(command, "@clientItemId", clientItemId.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDispatchLink(reader) : null;
    }

    /// <summary>A release name with its case and every non-letter, non-digit gone.</summary>
    internal static string ComparableReleaseName(string? releaseName)
        => new((releaseName ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private DispatchCatalogueLink ReadDispatchLink(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
            !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8)
                ? []
                : DeserializeReplacementTargets(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

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
        await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", library.LibraryId, cancellationToken);
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Deluno will check {library.LibraryName} on the next pass.",
            "library.search.requested",
            SeverityForCategory("library.search.requested"),
            now.ToString("O"),
            cancellationToken);

        // The path the Search button takes. It only sets a flag for the
        // planning lane to notice — waking that lane now is most of this
        // issue's user-visible win.
        laneSignal?.Notify("planning.wake");
        return true;
    }

    public async Task<bool> SkipLibrarySearchCycleAsync(
        LibraryAutomationPlanItem library,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var nextSearchUtc = library.AutoSearchEnabled
            ? now.AddMinutes(Math.Max(1, library.SearchIntervalMinutes))
            : (DateTimeOffset?)null;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertLibraryAutomationStateAsync(connection, transaction, library, now, cancellationToken);

        var removedQueuedJobs = 0;
        using (var deleteQueued = connection.CreateCommand())
        {
            deleteQueued.Transaction = transaction;
            deleteQueued.CommandText =
                """
                DELETE FROM job_queue
                -- All three names: the two current types and any legacy row
                -- that outlived its migration. A skip that missed one would
                -- leave the search it was asked to cancel queued and running.
                WHERE job_type IN ('library.search.movies', 'library.search.tv', 'library.search')
                  AND status = 'queued'
                  AND related_entity_type = 'library'
                  AND related_entity_id = @libraryId;
                """;

            AddParameter(deleteQueued, "@libraryId", library.LibraryId);
            removedQueuedJobs = await deleteQueued.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE library_automation_state
                SET
                    search_requested = 0,
                    status = CASE WHEN status = 'running' THEN status ELSE 'idle' END,
                    next_search_utc = @nextSearchUtc,
                    next_missing_search_utc = @nextMissingSearchUtc,
                    next_upgrade_search_utc = @nextUpgradeSearchUtc,
                    last_error = NULL,
                    updated_utc = @updatedUtc
                WHERE library_id = @libraryId;
                """;

            AddParameter(update, "@libraryId", library.LibraryId);
            AddParameter(update, "@nextSearchUtc", nextSearchUtc?.ToString("O"));
            AddParameter(update, "@nextMissingSearchUtc", nextSearchUtc?.ToString("O"));
            AddParameter(update, "@nextUpgradeSearchUtc", nextSearchUtc?.ToString("O"));
            AddParameter(update, "@updatedUtc", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertActivityAsync(
            connection,
            transaction,
            category: "library.search.skipped",
            message: removedQueuedJobs > 0
                ? $"Skipped the current cycle for {library.LibraryName}; removed {removedQueuedJobs} queued search job{(removedQueuedJobs == 1 ? string.Empty : "s")}."
                : $"Skipped the current cycle for {library.LibraryName}.",
            detailsJson: JsonSerializer.Serialize(new
            {
                removedQueuedJobs,
                nextSearchUtc = nextSearchUtc?.ToString("O")
            }),
            relatedJobId: null,
            relatedEntityType: "library",
            relatedEntityId: library.LibraryId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", library.LibraryId, cancellationToken);

        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Skipped the current cycle for {library.LibraryName}.",
            "library.search.skipped",
            SeverityForCategory("library.search.skipped"),
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
        var pendingJobKeys = await ReadPendingLibraryJobsAsync(connection, transaction, cancellationToken);

        var queuedAny = false;
        var queuedJobTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedLibraries = libraries
            .Select(library =>
            {
                stateByLibraryId.TryGetValue(library.LibraryId, out var state);
                return (Library: library, State: state);
            })
            .OrderByDescending(item => item.State?.SearchRequested ?? false)
            .ThenBy(item => item.State?.LastPlannedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.State?.NextSearchUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Library.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Library)
            .ToArray();
        // Only libraries whose state actually moved. Announcing every library the
        // planner merely *looked at* meant an idle dashboard refetching three
        // queries every thirty seconds for a change that had not happened — the
        // storage layer was already guarded against writing nothing, and the
        // wire was not (#274).
        var changedLibraryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in orderedLibraries)
        {
            if (await UpsertLibraryAutomationStateAsync(connection, transaction, library, now, cancellationToken))
            {
                changedLibraryIds.Add(library.LibraryId);
            }

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

            var missingNextSearchUtc = library.MissingSearchEnabled
                ? state.NextMissingSearchUtc ?? state.NextSearchUtc
                : null;
            var upgradeNextSearchUtc = library.UpgradeSearchEnabled
                ? state.NextUpgradeSearchUtc ?? state.NextSearchUtc
                : null;

            var manualRequest = state.SearchRequested;

            // The time-of-day window, read once. Both subtitles and searches ask
            // it the same question, and a manual request bypasses it for both.
            var (inSearchWindow, nextWindowOpenUtc) = SearchWindow(library, now);

            // Subtitles, and why they are planned here rather than below.
            //
            // <b>They used to sit inside the release-search branch,</b> so they
            // inherited its two switches along with its cycle. A library with
            // "Search automatically" turned off — which that screen describes as
            // *keep this library manual*, meaning manual **releases** — asked for
            // English every day and was never given it, and said nothing. So did
            // a library with searching on but both missing and upgrade off.
            //
            // That is precisely the person Bazarr exists for: a library that is
            // already complete and wants subtitles for it. Deluno was refusing
            // exactly them, silently, and only a manual "search now" got through.
            // Found on the rig — the fetch that proved this feature works could
            // not be made to happen any other way.
            //
            // One switch, two subjects: the shape every defect in this codebase
            // has had.
            //
            // <b>The first fix was half of one.</b> It freed subtitles from the two
            // search switches and left them borrowing the search's *timing* — the
            // interval and the time-of-day window. James: <i>"I dont agree that it
            // shares a cycle or schedule and this was told to you back when I said
            // nothing should be shared or have to wait for another process."</i>
            // Right, and the cost was visible: a newly imported episode waited up
            // to a full <c>SearchIntervalMinutes</c>, twelve hours on the rig, before
            // anything even looked at it.
            //
            // So the split is now the whole way down. Subtitles share the
            // <b>planner</b> and the <b>job lanes</b> — one place decides, one set
            // of workers runs it, which is what DESIGN-002 rule 3 was protecting
            // and what Weir's Subber got wrong with a lane and a scheduler and
            // a worker of its own. They share <b>no timing at all</b>:
            //
            // - Not the interval. <c>SearchIntervalMinutes</c> is how often to go
            //   back to an indexer, chosen for a service that bans you for asking
            //   twice. A subtitle pass costs one query when there is nothing to do,
            //   and is paced when there is by per-title backoff that already exists.
            // - Not the window. A search window exists to be polite to indexers at
            //   3am; subtitle providers are different servers with their own
            //   rate-limit backoff on the Connection.
            // - Not the manual override, which it no longer needs.
            var subtitleNextSearchUtc = state.NextSubtitleSearchUtc;
            var subtitleDue = library.WantsSubtitles &&
                (subtitleNextSearchUtc is null || subtitleNextSearchUtc <= now);

            if (subtitleDue)
            {
                // It runs before the searches for the obvious reason: knowing
                // which subtitles you already hold is what decides which ones are
                // worth fetching.
                //
                // It does not touch the automation status or the next-search
                // clock. Those belong to searching, and a subtitle pass is not a
                // search; borrowing them would make a library read "queued" for
                // work that never reaches an indexer.
            var subtitleDedupeKey = $"library.subtitles.scan:{library.LibraryId}";
            if (!pendingJobKeys.Contains(subtitleDedupeKey) &&
                await FindDuplicateActiveJobAsync(connection, transaction, subtitleDedupeKey, subtitleDedupeKey, cancellationToken) is null)
            {
                var subtitleJob = new JobQueueItem(
                    Id: Guid.CreateVersion7().ToString("N"),
                    JobType: "library.subtitles.scan",
                    Source: library.MediaType,
                    Status: "queued",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        libraryId = library.LibraryId,
                        libraryName = library.LibraryName,
                        mediaType = library.MediaType
                    }),
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
                    IdempotencyKey: subtitleDedupeKey,
                    DedupeKey: subtitleDedupeKey,
                    MaxAttempts: DefaultMaxAttempts,
                    LastAttemptUtc: null,
                    NextAttemptUtc: now);

                await InsertJobAsync(connection, transaction, subtitleJob, cancellationToken);
                queuedAny = true;
                await InsertActivityAsync(connection, transaction, "job.queued", FormatQueuedMessage(subtitleJob.JobType, subtitleJob.Source, subtitleJob.PayloadJson, subtitleJob.RelatedEntityType), subtitleJob.PayloadJson, subtitleJob.Id, subtitleJob.RelatedEntityType, subtitleJob.RelatedEntityId, now, cancellationToken);
            }

            // And then go and get the ones that are still missing.
            //
            // Queued together rather than chained, because the queue already
            // orders them: the scan is on the import lane and the fetch on
            // intake, so the scan drains first on any install that is not
            // already idle, and on one that is, the fetch simply finds one
            // slice less than it could have and picks the rest up next
            // cycle. Chaining would mean a failed scan silently stopping
            // every subsequent fetch, which is a worse failure than a slow
            // first night.
            //
            // Same window, same cycle, same override — DESIGN-002 rule 3.
            // Nothing about subtitles decides when subtitles happen.
            var subtitleSearchKey = $"library.subtitles.search:{library.LibraryId}";
            if (!pendingJobKeys.Contains(subtitleSearchKey) &&
                await FindDuplicateActiveJobAsync(connection, transaction, subtitleSearchKey, subtitleSearchKey, cancellationToken) is null)
            {
                var subtitleSearchJob = new JobQueueItem(
                    Id: Guid.CreateVersion7().ToString("N"),
                    JobType: "library.subtitles.search",
                    Source: library.MediaType,
                    Status: "queued",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        libraryId = library.LibraryId,
                        libraryName = library.LibraryName,
                        mediaType = library.MediaType
                    }),
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
                    IdempotencyKey: subtitleSearchKey,
                    DedupeKey: subtitleSearchKey,
                    MaxAttempts: DefaultMaxAttempts,
                    LastAttemptUtc: null,
                    NextAttemptUtc: now);

                await InsertJobAsync(connection, transaction, subtitleSearchJob, cancellationToken);
                queuedAny = true;
                await InsertActivityAsync(connection, transaction, "job.queued", FormatQueuedMessage(subtitleSearchJob.JobType, subtitleSearchJob.Source, subtitleSearchJob.PayloadJson, subtitleSearchJob.RelatedEntityType), subtitleSearchJob.PayloadJson, subtitleSearchJob.Id, subtitleSearchJob.RelatedEntityType, subtitleSearchJob.RelatedEntityId, now, cancellationToken);
            }
                subtitleNextSearchUtc = now.Add(SubtitleCycleInterval);
            }

            // Written whatever happens below, and on its own column, so that a
            // library which only wants subtitles still records when it last had
            // them looked at. Left to UpdateAutomationIdleAsync it would have
            // been lost on every path that returns early — which, for a paused
            // library, is all of them — and the clock never advancing means the
            // work is re-queued on every thirty-second tick.
            if (library.WantsSubtitles &&
                await WriteSubtitleScheduleAsync(
                    connection, transaction, library.LibraryId, subtitleNextSearchUtc, now, cancellationToken))
            {
                changedLibraryIds.Add(library.LibraryId);
            }

            if (!library.AutoSearchEnabled && !state.SearchRequested)
            {
                if (await UpdateAutomationIdleAsync(
                    connection,
                    transaction,
                    library.LibraryId,
                    library.LibraryName,
                    library.MediaType,
                    "paused",
                    nextMissingSearchUtc: null,
                    nextUpgradeSearchUtc: null,
                    searchRequested: false,
                    updatedUtc: now,
                    cancellationToken: cancellationToken))
                {
                    changedLibraryIds.Add(library.LibraryId);
                }

                continue;
            }

            var missingDue = library.AutoSearchEnabled && library.MissingSearchEnabled &&
                (missingNextSearchUtc is null || missingNextSearchUtc <= now);
            var upgradeDue = library.AutoSearchEnabled && library.UpgradeSearchEnabled &&
                (upgradeNextSearchUtc is null || upgradeNextSearchUtc <= now);

            // Respect time-of-day search window independently for both schedules
            // (manual requests bypass the window). The window itself is read
            // once, above, because subtitles ask it the same question and two
            // copies of a midnight-wrapping hour comparison is how they drift.
            if ((missingDue || upgradeDue) && !manualRequest && !inSearchWindow)
            {
                // Advance each due schedule to the next window opening.
                if (missingDue) missingNextSearchUtc = nextWindowOpenUtc;
                if (upgradeDue) upgradeNextSearchUtc = nextWindowOpenUtc;

                if (await UpdateAutomationIdleAsync(
                    connection, transaction,
                    library.LibraryId, library.LibraryName, library.MediaType,
                    "idle", missingNextSearchUtc, upgradeNextSearchUtc, searchRequested: false,
                    updatedUtc: now, cancellationToken: cancellationToken))
                {
                    changedLibraryIds.Add(library.LibraryId);
                }

                continue;
            }

            if (!manualRequest && !missingDue && !upgradeDue)
            {
                continue;
            }

            var queuedForLibrary = false;
            var hasPendingForLibrary = false;
            var attemptedKinds = 0;

            // A saved view is an alternate scope for the existing cycle, not a
            // second scheduler. Manual requests deliberately bypass it: the
            // explicit "search now" action means the whole library, while a
            // scheduled pass is the one the saved view is attached to.
            var scopesToPlan = manualRequest || library.SearchScopes is not { Count: > 0 }
                ? new LibraryAutomationScope?[] { null }
                : library.SearchScopes!.Select(scope => (LibraryAutomationScope?)scope).ToArray();

            foreach (var scope in scopesToPlan)
            {
                string? requestedKind = null;
                if (scope is not null &&
                    (!scope.IsValid || string.Equals(scope.Monitoring, "unmonitored", StringComparison.OrdinalIgnoreCase) ||
                     !TryResolveScopeSearchKind(scope.QuickFilter, out requestedKind)))
                {
                    continue;
                }

                var checkMissing = library.MissingSearchEnabled && (manualRequest || missingDue) &&
                    (requestedKind is null || string.Equals(requestedKind, "missing", StringComparison.OrdinalIgnoreCase));
                var checkUpgrades = library.UpgradeSearchEnabled && (manualRequest || upgradeDue) &&
                    (requestedKind is null || string.Equals(requestedKind, "upgrade", StringComparison.OrdinalIgnoreCase));

                if (!checkMissing && !checkUpgrades)
                {
                    continue;
                }

                // One scheduled check per library (or scope) per due slot (#658).
                //
                // Missing and upgrade searches keep independent next-search clocks
                // so either can be paused and resumed on its own, but
                // NextSearchAfterQueue advances both by the same interval, so a
                // library that has never had one of them diverge finds them due
                // at exactly the same instant on every single cycle — not
                // occasionally, every time. That used to mean two separate jobs,
                // each logged as the same "Queued a library check" line, run
                // concurrently by two lanes against the same due slot. When both
                // are due together they now go out as one job: the handler
                // already treats CheckMissing/CheckUpgrades as independent flags
                // on a single payload (ResolveSearchStatus falls back to neither
                // when both are set), so nothing downstream needed to change.
                // Only when the two clocks have actually pulled apart — one due,
                // the other not — does this fall back to a single-kind job on
                // its own dedupe key, exactly as before.
                attemptedKinds += (checkMissing ? 1 : 0) + (checkUpgrades ? 1 : 0);
                var kindSuffix = checkMissing && checkUpgrades
                    ? string.Empty
                    : checkMissing ? ":missing" : ":upgrade";
                var searchKind = checkMissing && checkUpgrades
                    ? "combined"
                    : checkMissing ? "missing" : "upgrade";
                var scopeSuffix = scope is null ? string.Empty : $":scope:{scope.Id}";
                var dedupeKey = $"library.search:{library.LibraryId}{kindSuffix}{scopeSuffix}";
                // Only meaningful for a single-kind job: the combined dedupe key
                // above already collapses to this exact string when unscoped, so
                // checking it a second time there would be redundant rather than
                // wrong.
                var legacyDedupeKey = scope is null && kindSuffix.Length > 0
                    ? $"library.search:{library.LibraryId}"
                    : null;
                if (pendingJobKeys.Contains(dedupeKey) ||
                    (legacyDedupeKey is not null && pendingJobKeys.Contains(legacyDedupeKey)))
                {
                    hasPendingForLibrary = true;
                    continue;
                }

                var payload = JsonSerializer.Serialize(new
                {
                    libraryId = library.LibraryId,
                    libraryName = library.LibraryName,
                    mediaType = library.MediaType,
                    checkMissing,
                    checkUpgrades,
                    searchKind,
                    maxItems = library.MaxMissingItemsPerRun + library.MaxUpgradeItemsPerRun,
                    maxMissingItems = library.MaxMissingItemsPerRun,
                    maxUpgradeItems = library.MaxUpgradeItemsPerRun,
                    retryDelayHours = library.RetryDelayHours,
                    randomizeSearchOrder = library.RandomizeSearchOrder,
                    triggeredBy = manualRequest ? "manual" : "schedule",
                    scopeId = scope?.Id,
                    scopeName = scope?.Name,
                    scopeConditions = scope?.Filters.Conditions
                });
                var idempotencyKey = $"library.search:{library.LibraryId}{kindSuffix}{scopeSuffix}:{(manualRequest ? "manual" : "schedule")}";
                var job = new JobQueueItem(
                    Id: Guid.CreateVersion7().ToString("N"),
                    JobType: LibrarySearchJobTypes.For(library.MediaType),
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

                var duplicate = await FindDuplicateActiveJobAsync(connection, transaction, idempotencyKey, dedupeKey, cancellationToken);
                if (duplicate is not null)
                {
                    hasPendingForLibrary = true;
                    if (checkMissing) missingNextSearchUtc = NextSearchAfterQueue(now, library);
                    if (checkUpgrades) upgradeNextSearchUtc = NextSearchAfterQueue(now, library);
                    continue;
                }

                try
                {
                    await InsertJobAsync(connection, transaction, job, cancellationToken);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // ux_job_queue_active_dedupe is what actually makes "one job
                    // per due slot" true under concurrency: a second planning
                    // pass — another scheduler tick, or a manual "Search now"
                    // landing in the same instant as the automatic one — can
                    // still read no pending job above and then lose this insert
                    // to the unique index once the first pass commits. Treat
                    // that refusal the same way EnqueueAsync does, as evidence
                    // the work is already queued, rather than letting it fail
                    // the whole planning pass (#658).
                    hasPendingForLibrary = true;
                    if (checkMissing) missingNextSearchUtc = NextSearchAfterQueue(now, library);
                    if (checkUpgrades) upgradeNextSearchUtc = NextSearchAfterQueue(now, library);
                    continue;
                }

                queuedAny = true;
                queuedForLibrary = true;
                if (checkMissing) missingNextSearchUtc = NextSearchAfterQueue(now, library);
                if (checkUpgrades) upgradeNextSearchUtc = NextSearchAfterQueue(now, library);

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE library_automation_state
                    SET library_name = @libraryName,
                        media_type = @mediaType,
                        status = 'queued',
                        search_requested = 0,
                        last_planned_utc = @lastPlannedUtc,
                        next_search_utc = @nextSearchUtc,
                        next_missing_search_utc = @nextMissingSearchUtc,
                        next_upgrade_search_utc = @nextUpgradeSearchUtc,
                        last_job_id = @lastJobId,
                        last_error = NULL,
                        updated_utc = @updatedUtc
                    WHERE library_id = @libraryId;
                    """;
                AddParameter(update, "@libraryId", library.LibraryId);
                AddParameter(update, "@libraryName", library.LibraryName);
                AddParameter(update, "@mediaType", library.MediaType);
                AddParameter(update, "@lastPlannedUtc", now.ToString("O"));
                AddParameter(update, "@nextSearchUtc", Earliest(missingNextSearchUtc, upgradeNextSearchUtc)?.ToString("O"));
                AddParameter(update, "@nextMissingSearchUtc", missingNextSearchUtc?.ToString("O"));
                AddParameter(update, "@nextUpgradeSearchUtc", upgradeNextSearchUtc?.ToString("O"));
                AddParameter(update, "@lastJobId", job.Id);
                AddParameter(update, "@updatedUtc", now.ToString("O"));
                await update.ExecuteNonQueryAsync(cancellationToken);
                // Queueing a search always moves the state — status, last job and
                // next search all change — so this one is unconditional.
                changedLibraryIds.Add(library.LibraryId);

                await InsertActivityAsync(connection, transaction, "job.queued", FormatQueuedMessage(job.JobType, job.Source, job.PayloadJson, job.RelatedEntityType), job.PayloadJson, job.Id, job.RelatedEntityType, job.RelatedEntityId, now, cancellationToken);
                queuedJobTypes.Add(job.JobType);
            }

            // A manual request against a library with both search types off has
            // nothing to queue, so it is consumed rather than retried forever on
            // every planner tick.
            //
            // It was already meant to be, and was not: clearing the local flag
            // is what stopped the branch below — the only place that writes
            // `search_requested = 0` — from ever running for this case. Both
            // libraries on the lab rig had been sitting at "requested" for days
            // because of it, re-entering the cycle every thirty seconds.
            //
            // Invisible until #301 gave the cycle something to do here: a
            // subtitle scan was then queued on every one of those ticks. The
            // same defect, in the shape this codebase keeps producing it — a
            // decision recorded in one place and read from another.
            var consumeManualRequest = manualRequest && attemptedKinds == 0;
            if (consumeManualRequest)
            {
                manualRequest = false;
            }

            if (!queuedForLibrary && (hasPendingForLibrary || manualRequest || consumeManualRequest))
            {
                if (await UpdateAutomationIdleAsync(connection, transaction, library.LibraryId, library.LibraryName, library.MediaType, "queued", missingNextSearchUtc, upgradeNextSearchUtc, searchRequested: false, updatedUtc: now, cancellationToken: cancellationToken))
                {
                    changedLibraryIds.Add(library.LibraryId);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var libraryId in changedLibraryIds)
        {
            await realtimeEventPublisher.PublishEntityChangedAsync("AutomationState", libraryId, cancellationToken);
        }
        if (queuedAny)
        {
            foreach (var jobType in queuedJobTypes)
            {
                laneSignal?.Notify(jobType);
            }
        }
    }

    public async Task PlanEpisodeSearchesAsync(
        string libraryId,
        IReadOnlyList<EpisodeSearchPlanItem> episodes,
        CancellationToken cancellationToken)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var queuedAny = false;

        foreach (var episode in episodes)
        {
            var payload = JsonSerializer.Serialize(new
            {
                episodeId = episode.EpisodeId,
                seriesId = episode.SeriesId,
                libraryId,
                seasonNumber = episode.SeasonNumber,
                episodeNumber = episode.EpisodeNumber,
                title = episode.Title
            });
            var idempotencyKey = $"episode.search:{episode.EpisodeId}:schedule";
            var dedupeKey = $"episode.search:{episode.EpisodeId}";

            var job = new JobQueueItem(
                Id: Guid.CreateVersion7().ToString("N"),
                JobType: "episode.search",
                Source: "series",
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
                RelatedEntityType: "episode",
                RelatedEntityId: episode.EpisodeId,
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
                continue;
            }

            await InsertJobAsync(connection, transaction, job, cancellationToken);
            queuedAny = true;

            // Nothing is written to episode_wanted_state here. That table is in
            // the series database and this is a connection to the jobs one, so
            // the UPDATE that used to sit here failed with "no such table" every
            // time there was an episode to plan. It took the transaction with
            // it: no episode search was ever queued from a scheduled cycle, and
            // the library search that called this failed three times and gave
            // up (seen on the rig on 16 and 19 September). It was never needed:
            // the dedupe key above stops an episode being planned twice, and the
            // episode search records its own search time, in the right database,
            // when it runs.

            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.queued",
                message: $"Episode search scheduled: S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - {episode.Title}",
                detailsJson: payload,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);

            DelunoObservability.JobsQueued.Add(1, new("job.type", "episode.search"), new("source", "series"));
        }

        await transaction.CommitAsync(cancellationToken);
        if (queuedAny)
        {
            laneSignal?.Notify("episode.search");
        }
    }

    /// <summary>
    /// Queues one whole-season search for each season the automatic cycle
    /// found entirely missing and fully aired, in place of one
    /// <c>episode.search</c> per episode (#646).
    ///
    /// <para>Deduped like <see cref="PlanEpisodeSearchesAsync"/>: a cycle that
    /// runs while the last one is still working the same season adds nothing.
    /// The job handler that executes <c>series.search.season</c> queues its own
    /// per-episode fallback if the pack search finds nothing worth grabbing, so
    /// nothing here needs to.</para>
    /// </summary>
    public async Task PlanSeasonSearchesAsync(
        string libraryId,
        IReadOnlyList<SeasonSearchPlanItem> seasons,
        CancellationToken cancellationToken)
    {
        if (seasons.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var queuedAny = false;

        foreach (var season in seasons)
        {
            var payload = JsonSerializer.Serialize(new
            {
                seriesId = season.SeriesId,
                seriesTitle = season.SeriesTitle,
                libraryId,
                seasonNumber = season.SeasonNumber,
                episodes = season.Episodes.Select(episode => new
                {
                    episodeId = episode.EpisodeId,
                    episodeNumber = episode.EpisodeNumber,
                    title = episode.Title
                })
            });
            var idempotencyKey = $"season.search:{season.SeriesId}:{season.SeasonNumber}:schedule";
            var dedupeKey = $"season.search:{season.SeriesId}:{season.SeasonNumber}";

            var job = new JobQueueItem(
                Id: Guid.CreateVersion7().ToString("N"),
                JobType: "series.search.season",
                Source: "series",
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
                RelatedEntityType: "series",
                RelatedEntityId: season.SeriesId,
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
                continue;
            }

            await InsertJobAsync(connection, transaction, job, cancellationToken);
            queuedAny = true;

            await InsertActivityAsync(
                connection,
                transaction,
                category: "job.queued",
                message: $"Season search scheduled: S{season.SeasonNumber:D2} - {season.SeriesTitle} ({season.Episodes.Count} episodes)",
                detailsJson: payload,
                relatedJobId: job.Id,
                relatedEntityType: job.RelatedEntityType,
                relatedEntityId: job.RelatedEntityId,
                createdUtc: now,
                cancellationToken: cancellationToken);

            DelunoObservability.JobsQueued.Add(1, new("job.type", "series.search.season"), new("source", "series"));
        }

        await transaction.CommitAsync(cancellationToken);
        if (queuedAny)
        {
            laneSignal?.Notify("series.search.season");
        }
    }

    public async Task<string> RecordDownloadDispatchAsync(
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
        int? grabResponseCode = null,
        string? grabFailureCode = null,
        CancellationToken cancellationToken = default,
        IntegrationFailure? failure = null,
        bool replacementAuthorized = false,
        bool forceReplacementAuthorized = false,
        string? replacementExpectedPath = null,
        IReadOnlyList<DispatchReplacementTarget>? replacementTargets = null,
        string? clientExternalId = null,
        string? startedBy = null,
        string? titleEntityId = null)
    {
        var now = timeProvider.GetUtcNow();
        var dispatchId = Guid.CreateVersion7().ToString("N");
        var decisionTelemetry = TryParseDispatchDecisionTelemetry(notesJson);
        // The decision is in its own columns now; keep the notes to a size a screen
        // can carry, and keep one copy of them (#1081).
        notesJson = DispatchNotesTrimmer.Trim(notesJson);
        var normalizedReplacementTargets = NormalizeReplacementTargets(replacementTargets);
        var ownedReplacementAuthorized = replacementAuthorized &&
            (!string.IsNullOrWhiteSpace(replacementExpectedPath) || normalizedReplacementTargets.Count > 0);

        // Filed under the indexer's id (#1077), asked while its name still is its name.
        var indexerId = await IndexerIdentity.ResolveIdAsync(databaseConnectionFactory, indexerName, cancellationToken);

        // Why it was started (#1098), from facts already held.
        var (startedByReason, startedByDetail) = await DispatchStartedByResolver.ResolveAsync(
            databaseConnectionFactory, startedBy, mediaType, entityType, entityId, titleEntityId, cancellationToken);

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
                    indexer_name, indexer_id, download_client_id, download_client_name, status, notes_json,
                    decision_quality, decision_score, decision_meets_cutoff, decision_status, decision_quality_delta,
                    decision_custom_format_score, decision_seeder_score, decision_size_score, decision_release_group,
                    decision_estimated_bitrate_mbps, decision_size_bytes, decision_seeders, decision_policy_version,
                    decision_matched_custom_formats_json, decision_reasons_json, decision_risk_flags_json,
                    decision_override_used, decision_override_reason, decision_language,
                    replacement_authorized, force_replacement_authorized, replacement_expected_path,
                    replacement_targets_json, torrent_hash_or_item_id, created_utc, started_by, started_by_detail
                )
                VALUES (
                    @id, @libraryId, @mediaType, @entityType, @entityId, @releaseName,
                    @indexerName, @indexerId, @downloadClientId, @downloadClientName, @status, @notesJson,
                    @decisionQuality, @decisionScore, @decisionMeetsCutoff, @decisionStatus, @decisionQualityDelta,
                    @decisionCustomFormatScore, @decisionSeederScore, @decisionSizeScore, @decisionReleaseGroup,
                    @decisionEstimatedBitrateMbps, @decisionSizeBytes, @decisionSeeders, @decisionPolicyVersion,
                    @decisionMatchedCustomFormatsJson, @decisionReasonsJson, @decisionRiskFlagsJson,
                    @decisionOverrideUsed, @decisionOverrideReason, @decisionLanguage,
                    @replacementAuthorized, @forceReplacementAuthorized, @replacementExpectedPath,
                    @replacementTargetsJson, @clientExternalId, @createdUtc, @startedBy, @startedByDetail
                );
                """;

            AddParameter(command, "@id", dispatchId);
            AddParameter(command, "@libraryId", libraryId);
            AddParameter(command, "@mediaType", mediaType);
            AddParameter(command, "@entityType", entityType);
            AddParameter(command, "@entityId", entityId);
            AddParameter(command, "@releaseName", releaseName);
            AddParameter(command, "@indexerName", indexerName);
            AddParameter(command, "@indexerId", indexerId);
            AddParameter(command, "@downloadClientId", downloadClientId);
            AddParameter(command, "@downloadClientName", downloadClientName);
            AddParameter(command, "@status", status);
            AddParameter(command, "@notesJson", notesJson);
            AddParameter(command, "@decisionQuality", decisionTelemetry?.Quality);
            AddParameter(command, "@decisionScore", decisionTelemetry?.Score);
            AddParameter(
                command,
                "@decisionMeetsCutoff",
                decisionTelemetry?.MeetsCutoff is true ? 1 : decisionTelemetry?.MeetsCutoff is false ? 0 : null);
            AddParameter(command, "@decisionStatus", decisionTelemetry?.DecisionStatus);
            AddParameter(command, "@decisionQualityDelta", decisionTelemetry?.QualityDelta);
            AddParameter(command, "@decisionCustomFormatScore", decisionTelemetry?.CustomFormatScore);
            AddParameter(command, "@decisionSeederScore", decisionTelemetry?.SeederScore);
            AddParameter(command, "@decisionSizeScore", decisionTelemetry?.SizeScore);
            AddParameter(command, "@decisionReleaseGroup", decisionTelemetry?.ReleaseGroup);
            AddParameter(command, "@decisionEstimatedBitrateMbps", decisionTelemetry?.EstimatedBitrateMbps);
            AddParameter(command, "@decisionSizeBytes", decisionTelemetry?.SizeBytes);
            AddParameter(command, "@decisionSeeders", decisionTelemetry?.Seeders);
            AddParameter(command, "@decisionPolicyVersion", decisionTelemetry?.PolicyVersion);
            AddParameter(command, "@decisionMatchedCustomFormatsJson", decisionTelemetry?.MatchedCustomFormatsJson);
            AddParameter(command, "@decisionReasonsJson", decisionTelemetry?.ReasonsJson);
            AddParameter(command, "@decisionRiskFlagsJson", decisionTelemetry?.RiskFlagsJson);
            AddParameter(command, "@decisionOverrideUsed", decisionTelemetry is null ? 0 : decisionTelemetry.OverrideUsed ? 1 : 0);
            AddParameter(command, "@decisionOverrideReason", decisionTelemetry?.OverrideReason);
            AddParameter(command, "@decisionLanguage", ReleasePreferenceFactFactory.DetectLanguageFromReleaseName(releaseName));
            AddParameter(command, "@replacementAuthorized", ownedReplacementAuthorized ? 1 : 0);
            // Force is a stricter form of replacement authority, never an
            // independent way to turn an ordinary import into an overwrite.
            AddParameter(command, "@forceReplacementAuthorized", ownedReplacementAuthorized && forceReplacementAuthorized ? 1 : 0);
            AddParameter(
                command,
                "@replacementExpectedPath",
                ownedReplacementAuthorized && !string.IsNullOrWhiteSpace(replacementExpectedPath)
                    ? replacementExpectedPath!.Trim()
                    : null);
            AddParameter(
                command,
                "@replacementTargetsJson",
                ownedReplacementAuthorized && normalizedReplacementTargets.Count > 0
                    ? JsonSerializer.Serialize(normalizedReplacementTargets)
                    : null);
            AddParameter(command, "@clientExternalId", string.IsNullOrWhiteSpace(clientExternalId) ? null : clientExternalId.Trim());
            AddParameter(command, "@createdUtc", now.ToString("O"));
            AddParameter(command, "@startedBy", startedByReason);
            AddParameter(command, "@startedByDetail", startedByDetail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The dispatch is now Deluno's record of this send; the reservation that
        // stood in for it until now is no longer needed (#1062).
        if (!string.IsNullOrWhiteSpace(clientExternalId))
        {
            using var release = connection.CreateCommand();
            release.Transaction = transaction;
            release.CommandText = "DELETE FROM download_grab_reservations WHERE download_client_id = @clientId AND client_item_id = @itemId";
            AddParameter(release, "@clientId", downloadClientId);
            AddParameter(release, "@itemId", clientExternalId.Trim().ToLowerInvariant());
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        // Say what happened, not what was attempted. This line read "Deluno sent
        // … to qBittorrent" while the dispatch was failing and the client's
        // torrent list stayed empty (#292), which is the one place a user looks
        // to find out whether a grab worked.
        var (activityCategory, activityMessage) = DescribeDispatch(status, releaseName, downloadClientName, grabFailureCode, failure);

        await InsertActivityAsync(
            connection,
            transaction,
            category: activityCategory,
            message: activityMessage,
            // The line names the release and the client; the full candidate list stays on the dispatch itself (#1083).
            detailsJson: SearchHistoryDetails.Trim(notesJson),
            relatedJobId: null,
            relatedEntityType: entityType,
            relatedEntityId: entityId,
            createdUtc: now,
            cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var grabStatus = MapStatusToGrabStatus(status);
        var responseCode = grabResponseCode ?? (string.Equals(status, "sent", StringComparison.OrdinalIgnoreCase) ? 200 : 400);

        await downloadDispatchesRepository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: grabStatus,
            grabResponseCode: responseCode,
            grabMessage: status,
            grabFailureCode: grabFailureCode,
            // Not a second copy of the notes: they are stored once, on the dispatch (#1081).
            grabResponseJson: null,
            cancellationToken: cancellationToken,
            failure: failure,
            externalId: clientExternalId);

        // No DownloadProgress here any more (#273). This fired once per grab with
        // progress and speed both zero, under the *dispatch* id while the
        // frontend keys its rows by the download client's queue-item id — so it
        // announced a transfer with a number that was false and an id that
        // matched nothing. DownloadProgressPublisher reports the client's own
        // readings under the client's own id; that a grab happened is already
        // said by DispatchGrabCompleted and by the activity event below.
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            activityMessage,
            activityCategory,
            SeverityForCategory(activityCategory),
            now.ToString("O"),
            cancellationToken);

        // Radarr/Sonarr's "On Grab" (#856): every release Deluno actually sent
        // to a download client, manual or automated -- this is the one place
        // every grab path (manual, automated search, season/episode search)
        // converges, so it is dispatched once here rather than at each of
        // those call sites.
        if (string.Equals(status, "sent", StringComparison.OrdinalIgnoreCase) &&
            outboundNotificationService is not null)
        {
            var enabled = notificationPreferences is null ||
                (await notificationPreferences.GetPreferencesAsync(cancellationToken)).GrabEnabled;
            if (enabled)
            {
                var grabTitleTags = await mediaTagStore.ListTitleTagNamesAsync(entityType, entityId, cancellationToken);
                await NotificationOutboundDispatch.GuardAsync(async () => await outboundNotificationService.DispatchAsync(
                    "grab",
                    $"Grabbed: {releaseName}",
                    $"Deluno sent {releaseName} to {downloadClientName}.",
                    notesJson,
                    cancellationToken,
                    grabTitleTags), "grab");
            }
        }

        return dispatchId;
    }

    private static IReadOnlyList<DispatchReplacementTarget> NormalizeReplacementTargets(
        IReadOnlyList<DispatchReplacementTarget>? targets)
        => (targets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.EntityId) &&
                             !string.IsNullOrWhiteSpace(target.ExpectedPath))
            .Select(target => new DispatchReplacementTarget(
                target.EntityId.Trim(),
                target.ExpectedPath.Trim()))
            .Distinct()
            .OrderBy(target => target.EntityId, StringComparer.Ordinal)
            .ThenBy(target => target.ExpectedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<DispatchReplacementTarget> DeserializeReplacementTargets(string json)
    {
        try
        {
            return NormalizeReplacementTargets(
                JsonSerializer.Deserialize<IReadOnlyList<DispatchReplacementTarget>>(json));
        }
        catch (JsonException)
        {
            return [];
        }
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
                    planned_count, queued_count, skipped_count, notes_json, started_utc, completed_utc, search_kind
                )
                VALUES (
                    @id, @libraryId, @libraryName, @mediaType, @triggerKind, @status,
                    @plannedCount, @queuedCount, @skippedCount, @notesJson, @startedUtc, @completedUtc, @searchKind
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
            AddParameter(command, "@searchKind", request.SearchKind);
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
        var cycleTimestamp = (request.CompletedUtc ?? request.StartedUtc).ToString("O");
        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishSearchCycleProgressAsync(
            new SearchCycleProgressEvent(request.LibraryId, request.LibraryName, request.MediaType, "finished", request.PlannedCount, request.PlannedCount, cycleTimestamp),
            cancellationToken));
        await realtimeEventPublisher.PublishActivityEventAddedAsync(
            Guid.CreateVersion7().ToString("N"),
            $"Checked {request.LibraryName}: {request.PlannedCount} planned, {request.QueuedCount} sent, {request.SkippedCount} waiting.",
            "library.search.cycle",
            SeverityForCategory("library.search.cycle"),
            cycleTimestamp,
            cancellationToken);
        await realtimeEventPublisher.PublishSearchRunCompletedAsync(
            request.LibraryId,
            request.LibraryName,
            request.MediaType,
            request.PlannedCount,
            request.QueuedCount,
            request.SkippedCount,
            cycleTimestamp,
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

    public async Task<int> ClearActivityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity_events;";
        return await command.ExecuteNonQueryAsync(cancellationToken);
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

        // The by-hundreds turned-down rows are left out of a plain recent list; the Activity history asks for them (#1100).
        var conditions = new List<string> { "category <> '" + ActivityHistory.TurnedDownCategory + "'" };
        if (relatedEntityType is not null) conditions.Add("related_entity_type = @relatedEntityType");
        if (relatedEntityId is not null) conditions.Add("related_entity_id = @relatedEntityId");
        var where = "WHERE " + string.Join(" AND ", conditions);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc,
                title, file_path, source_name, reason_group
            FROM activity_events
            {where}
            ORDER BY created_utc DESC
            LIMIT @take;
            """;

        AddParameter(command, "@take", take);
        if (relatedEntityType is not null) AddParameter(command, "@relatedEntityType", relatedEntityType);
        if (relatedEntityId is not null) AddParameter(command, "@relatedEntityId", relatedEntityId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadActivity(reader));
        }

        return events;
    }

    public Task<Page<ActivityEventItem>> ListActivityPageAsync(
        PageRequest request,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
        // The plain recent lists leave the by-hundreds turned-down rows out; the Activity history asks for them (#1100).
        => QueryActivityPageAsync(request, new ActivityQuery(RelatedEntityType: relatedEntityType, RelatedEntityId: relatedEntityId), includeTurnedDown: false, cancellationToken);

    public Task<Page<ActivityEventItem>> ListActivityPageAsync(
        PageRequest request,
        ActivityQuery query,
        CancellationToken cancellationToken)
        => QueryActivityPageAsync(request, query, query.IncludesTurnedDown, cancellationToken);

    private static string LikeContains(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private async Task<Page<ActivityEventItem>> QueryActivityPageAsync(
        PageRequest request,
        ActivityQuery query,
        bool includeTurnedDown,
        CancellationToken cancellationToken)
    {
        var pageSize = request.BoundedPageSize;
        // The default (newest first) keeps its two-part token; a sort by title or by what happened carries the sort key too (#1119).
        var sort = query.Sort;
        var keyed = sort is not null && sort.Field != ActivitySort.When;
        var descending = sort?.Descending ?? true;
        var token = DelunoPageToken.Decode(request.PageToken, keyed ? 3 : 2);
        var events = new List<ActivityEventItem>(pageSize + 1);
        var sortKeys = new List<string>(pageSize + 1);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        // The Title column shows the event's title or, failing that, its kind in plain words; sorting by it needs the same words.
        if (sort?.Field == ActivitySort.Title && connection is SqliteConnection sqlite)
        {
            sqlite.CreateFunction("deluno_event_label", (string? category) => ActivityEventLabels.For(category), isDeterministic: true);
        }

        var sortKey = sort?.Field switch
        {
            ActivitySort.Title => "COALESCE(NULLIF(title, ''), deluno_event_label(category))",
            ActivitySort.What => "message",
            _ => null
        };

        // Only the conditions that apply are written, so SQLite can seek the
        // (related_entity_type, created_utc) or (category, created_utc) index instead of scanning (#1081, #1100).
        var conditions = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (!includeTurnedDown)
        {
            conditions.Add("category <> 'release.turned-down'");
        }

        void AddCategories(IReadOnlyList<string> categories, string prefix)
        {
            if (categories.Count == 0)
            {
                conditions.Add("1 = 0");
                return;
            }

            var names = new List<string>();
            for (var i = 0; i < categories.Count; i++)
            {
                names.Add($"@{prefix}{i}");
                parameters.Add(($"@{prefix}{i}", categories[i]));
            }

            conditions.Add($"category IN ({string.Join(", ", names)})");
        }

        if (!string.IsNullOrWhiteSpace(query.Type)) AddCategories(ActivityHistory.CategoriesOfType(query.Type.Trim().ToLowerInvariant()), "type");
        if (!string.IsNullOrWhiteSpace(query.Source)) AddCategories(ActivityHistory.CategoriesOfSource(query.Source.Trim().ToLowerInvariant()), "source");
        if (!string.IsNullOrWhiteSpace(query.Outcome)) AddCategories(ActivityHistory.CategoriesOfOutcome(query.Outcome.Trim().ToLowerInvariant()), "outcome");

        if (!string.IsNullOrWhiteSpace(query.SourceName))
        {
            conditions.Add("(source_name LIKE @sourceName ESCAPE '\\' OR message LIKE @sourceName ESCAPE '\\')");
            parameters.Add(("@sourceName", LikeContains(query.SourceName)));
        }

        if (!string.IsNullOrWhiteSpace(query.RelatedEntityType)) conditions.Add("related_entity_type = @relatedEntityType");
        if (!string.IsNullOrWhiteSpace(query.RelatedEntityType)) parameters.Add(("@relatedEntityType", query.RelatedEntityType));

        var hasTitle = !string.IsNullOrWhiteSpace(query.Title);
        var hasEntity = !string.IsNullOrWhiteSpace(query.RelatedEntityId);
        if (hasEntity && hasTitle)
        {
            // The title's own events, plus any that name it: an episode, a release or a file of that title.
            conditions.Add("(related_entity_id = @relatedEntityId OR title LIKE @title ESCAPE '\\' OR message LIKE @title ESCAPE '\\')");
            parameters.Add(("@relatedEntityId", query.RelatedEntityId!));
            parameters.Add(("@title", LikeContains(query.Title!)));
        }
        else if (hasEntity)
        {
            conditions.Add("related_entity_id = @relatedEntityId");
            parameters.Add(("@relatedEntityId", query.RelatedEntityId!));
        }
        else if (hasTitle)
        {
            conditions.Add("(title LIKE @title ESCAPE '\\' OR message LIKE @title ESCAPE '\\')");
            parameters.Add(("@title", LikeContains(query.Title!)));
        }

        if (!string.IsNullOrWhiteSpace(query.File))
        {
            conditions.Add("(file_path LIKE @file ESCAPE '\\' OR message LIKE @file ESCAPE '\\')");
            parameters.Add(("@file", LikeContains(query.File)));
        }

        if (!string.IsNullOrWhiteSpace(query.ReasonGroup))
        {
            conditions.Add("reason_group = @reasonGroup");
            parameters.Add(("@reasonGroup", query.ReasonGroup.Trim()));
        }

        if (query.SinceUtc is { } since)
        {
            conditions.Add("created_utc >= @sinceUtc");
            parameters.Add(("@sinceUtc", since.ToUniversalTime().ToString("O")));
        }

        if (query.UntilUtc is { } until)
        {
            conditions.Add("created_utc < @untilUtc");
            parameters.Add(("@untilUtc", until.ToUniversalTime().ToString("O")));
        }

        if (token is not null)
        {
            if (keyed)
            {
                var keyOp = descending ? "<" : ">";
                conditions.Add($"(({sortKey}) COLLATE NOCASE {keyOp} @sortKey OR (({sortKey}) COLLATE NOCASE = @sortKey AND (created_utc < @createdUtc OR (created_utc = @createdUtc AND rowid < CAST(@id AS INTEGER)))))");
            }
            else
            {
                var op = descending ? "<" : ">";
                conditions.Add($"(created_utc {op} @createdUtc OR (created_utc = @createdUtc AND rowid {op} CAST(@id AS INTEGER)))");
            }
        }

        var direction = descending ? "DESC" : "ASC";
        // Ties on a title or on what happened keep newest first, so the order is the same on every page.
        var orderBy = keyed
            ? $"ORDER BY ({sortKey}) COLLATE NOCASE {direction}, created_utc DESC, rowid DESC"
            : $"ORDER BY created_utc {direction}, rowid {direction}";
        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                id, category, message, CASE WHEN octet_length(details_json) <= 65536 THEN details_json END, related_job_id, related_entity_type, related_entity_id, created_utc,
                title, file_path, source_name, reason_group{(keyed ? $", {sortKey}" : string.Empty)}, rowid
            FROM activity_events
            {where}
            {orderBy}
            LIMIT @take;
            """;

        AddParameter(command, "@take", pageSize + 1);
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        if (token is not null)
        {
            var at = keyed ? 1 : 0;
            if (keyed) AddParameter(command, "@sortKey", token[0] ?? string.Empty);
            AddParameter(command, "@createdUtc", token[at]);
            AddParameter(command, "@id", token[at + 1]);
        }

        var rowids = new List<long>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadActivity(reader));
            rowids.Add(reader.GetInt64(reader.FieldCount - 1));
            if (keyed) sortKeys.Add(reader.IsDBNull(12) ? string.Empty : reader.GetString(12));
        }

        var hasMore = events.Count > pageSize;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1);
        }

        var nextPageToken = hasMore
            ? keyed
                ? DelunoPageToken.Encode(sortKeys[events.Count - 1], events[^1].CreatedUtc.ToString("O"), rowids[events.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture))
                : DelunoPageToken.Encode(events[^1].CreatedUtc.ToString("O"), rowids[events.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        return Page<ActivityEventItem>.Of(events, nextPageToken);
    }

    public async Task<ActivityStatsSnapshot> GetActivityStatsAsync(
        DateTimeOffset sinceUtc,
        int windowHours,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // One pass over the window rather than five separate COUNT queries.
        // The category buckets mirror the strings every caller already writes
        // (grep in ActivityStatsRepositoryTests pins the list), so a search,
        // grab, import or removal counts here the moment it is logged to
        // Activity -- there is no second, parallel bookkeeping to keep in step.
        command.CommandText =
            """
            SELECT
                SUM(CASE WHEN category LIKE '%.search.%' THEN 1 ELSE 0 END),
                SUM(CASE WHEN category LIKE '%.release.grabbed' OR category LIKE '%.release.force-grabbed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN category IN ('filesystem.import.completed', 'filesystem.import.season-pack.completed') THEN 1 ELSE 0 END),
                SUM(CASE WHEN category IN ('download.orphan.removed', 'download.queue.removed', 'download.sharing.reclaimed', 'download.sharing.stopped') THEN 1 ELSE 0 END),
                SUM(CASE WHEN category = 'retention.removed' THEN 1 ELSE 0 END)
            FROM activity_events
            WHERE created_utc >= @sinceUtc;
            """;
        AddParameter(command, "@sinceUtc", sinceUtc.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var generatedUtc = timeProvider.GetUtcNow();
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ActivityStatsSnapshot(sinceUtc, generatedUtc, windowHours, 0, 0, 0, 0, 0);
        }

        return new ActivityStatsSnapshot(
            sinceUtc,
            generatedUtc,
            windowHours,
            SearchesRun: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            DownloadsStarted: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            ImportsCompleted: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            CleanupRemovals: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            RetentionRemovals: reader.IsDBNull(4) ? 0 : reader.GetInt32(4));
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

        var recorded = await RecordActivityBatchAsync([activity], cancellationToken);
        return recorded[0];
    }

    public async Task<ActivityEventItem?> RecordActivityOnChangeAsync(
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
    {
        await using (var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken))
        {
            using var latest = connection.CreateCommand();
            latest.CommandText =
                """
                SELECT message
                FROM activity_events
                WHERE category = @category
                  AND related_entity_type IS @relatedEntityType
                  AND related_entity_id IS @relatedEntityId
                ORDER BY created_utc DESC, rowid DESC
                LIMIT 1;
                """;
            AddParameter(latest, "@category", category);
            AddParameter(latest, "@relatedEntityType", relatedEntityType);
            AddParameter(latest, "@relatedEntityId", relatedEntityId);
            var previous = await latest.ExecuteScalarAsync(cancellationToken) as string;
            if (string.Equals(previous, message, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return await RecordActivityAsync(category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(
        IReadOnlyList<ActivityEventItem> activities,
        CancellationToken cancellationToken)
    {
        if (activities.Count == 0)
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO activity_events (
                id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc,
                title, file_path, source_name, reason_group
            )
            VALUES (
                @id, @category, @message, @detailsJson, @relatedJobId, @relatedEntityType, @relatedEntityId, @createdUtc,
                @title, @filePath, @sourceName, @reasonGroup
            );
            """;

        AddParameter(command, "@title", null);
        AddParameter(command, "@filePath", null);
        AddParameter(command, "@sourceName", null);
        AddParameter(command, "@reasonGroup", null);
        AddParameter(command, "@id", null);
        AddParameter(command, "@category", null);
        AddParameter(command, "@message", null);
        AddParameter(command, "@detailsJson", null);
        AddParameter(command, "@relatedJobId", null);
        AddParameter(command, "@relatedEntityType", null);
        AddParameter(command, "@relatedEntityId", null);
        AddParameter(command, "@createdUtc", null);
        foreach (var activity in activities)
        {
            command.Parameters["@id"].Value = activity.Id;
            command.Parameters["@category"].Value = activity.Category;
            command.Parameters["@message"].Value = activity.Message;
            command.Parameters["@detailsJson"].Value = (object?)activity.DetailsJson ?? DBNull.Value;
            command.Parameters["@relatedJobId"].Value = (object?)activity.RelatedJobId ?? DBNull.Value;
            command.Parameters["@relatedEntityType"].Value = (object?)activity.RelatedEntityType ?? DBNull.Value;
            command.Parameters["@relatedEntityId"].Value = (object?)activity.RelatedEntityId ?? DBNull.Value;
            command.Parameters["@createdUtc"].Value = activity.CreatedUtc.ToString("O");
            // The title, file and source an event concerns are read once, here, from what its writer put in the details (#1100).
            var facts = ActivityHistory.FactsFromDetails(activity.DetailsJson);
            command.Parameters["@title"].Value = (object?)(activity.Title ?? facts.Title) ?? DBNull.Value;
            command.Parameters["@filePath"].Value = (object?)(activity.FilePath ?? facts.FilePaths) ?? DBNull.Value;
            command.Parameters["@sourceName"].Value = (object?)(activity.SourceName ?? facts.SourceName) ?? DBNull.Value;
            command.Parameters["@reasonGroup"].Value = (object?)activity.ReasonGroup ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var activity in activities)
        {
            var pushedFacts = ActivityHistory.FactsFromDetails(activity.DetailsJson);
            await realtimeEventPublisher.PublishActivityEventAddedAsync(
                new ActivityEventPush(
                    activity.Id,
                    activity.Message,
                    activity.Category,
                    SeverityForCategory(activity.Category),
                    activity.CreatedUtc.ToString("O"),
                    activity.RelatedEntityType,
                    activity.RelatedEntityId,
                    activity.RelatedJobId,
                    activity.Title ?? pushedFacts.Title,
                    activity.FilePath ?? pushedFacts.FilePaths,
                    activity.SourceName ?? pushedFacts.SourceName,
                    activity.ReasonGroup,
                    activity.DetailsJson),
                cancellationToken);
        }

        return activities;
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

    /// <summary>
    /// Puts a dead-lettered row back in the queue, carrying the newly requested
    /// payload and schedule. One row per piece of work, however many times that
    /// work is asked for.
    /// </summary>
    private static async Task ReviveDeadLetteredJobAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string jobId,
        JobQueueItem requested,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE job_queue
            SET
                status = 'queued',
                attempts = 0,
                payload_json = @payloadJson,
                scheduled_utc = @scheduledUtc,
                next_attempt_utc = @scheduledUtc,
                started_utc = NULL,
                completed_utc = NULL,
                leased_until_utc = NULL,
                worker_id = NULL,
                last_error = NULL
            WHERE id = @id;
            """;

        AddParameter(command, "@payloadJson", requested.PayloadJson);
        AddParameter(command, "@scheduledUtc", requested.ScheduledUtc.ToString("O"));
        AddParameter(command, "@id", jobId);

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
            WHERE status IN ('queued', 'running', 'failed', 'dead-letter')
              AND (
                (@idempotencyKey IS NOT NULL AND idempotency_key = @idempotencyKey)
                OR (@dedupeKey IS NOT NULL AND dedupe_key = @dedupeKey)
              )
            -- An active row wins over a dead-lettered one: if the same work is
            -- already runnable there is nothing to revive.
            ORDER BY CASE WHEN status = 'dead-letter' THEN 1 ELSE 0 END, created_utc ASC
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
                ? $"The worker running it stopped answering. {GaveUpSentence(job.MaxAttempts)}"
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
                message: $"{JobTypeLabels.For(job.JobType)}: {message}",
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
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc,
                next_missing_search_utc, next_upgrade_search_utc, next_subtitle_search_utc
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
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DISTINCT COALESCE(dedupe_key, 'library.search:' || related_entity_id)
            FROM job_queue
            -- All three names. This set is what stops a second search being
            -- planned for a library that already has one in flight, so missing
            -- a name here would queue duplicates rather than skip them.
            WHERE job_type IN ('library.search.movies', 'library.search.tv', 'library.search')
              AND related_entity_type = 'library'
              AND status IN ('queued', 'running')
              AND related_entity_id IS NOT NULL;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    /// <summary>
    /// Makes sure a row exists and its denormalised name and media type track
    /// the library.
    ///
    /// The DO UPDATE is guarded because this runs for every library on every
    /// search-lane tick. Without the guard it rewrote every row every five
    /// seconds — measured at 15 rewrites a minute of byte-identical data apart
    /// from updated_utc, which no caller uses as a liveness signal.
    /// "IS NOT" rather than "&lt;&gt;" so a NULL on either side compares correctly.
    /// </summary>
    /// <summary>Returns true when a row actually changed, so callers can announce only that.</summary>
    private static async Task<bool> UpsertLibraryAutomationStateAsync(
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
                last_started_utc, last_completed_utc, next_search_utc, last_job_id, last_error, updated_utc,
                next_missing_search_utc, next_upgrade_search_utc
            )
            VALUES (
                @libraryId, @libraryName, @mediaType, 'idle', 0, NULL, NULL, NULL, NULL, NULL, NULL, @updatedUtc, NULL, NULL
            )
            ON CONFLICT(library_id) DO UPDATE SET
                library_name = excluded.library_name,
                media_type = excluded.media_type,
                updated_utc = excluded.updated_utc
            WHERE library_automation_state.library_name IS NOT excluded.library_name
               OR library_automation_state.media_type IS NOT excluded.media_type;
            """;

        AddParameter(command, "@libraryId", library.LibraryId);
        AddParameter(command, "@libraryName", library.LibraryName);
        AddParameter(command, "@mediaType", library.MediaType);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Writes a library's automation state, but only when some part of it
    /// actually differs.
    ///
    /// This runs for every idle or auto-search-off library on every search-lane
    /// tick. Unguarded it rewrote the same rows every five seconds — measured at
    /// 15 writes a minute carrying no new information. updated_utc is excluded
    /// from the comparison on purpose: it is the field that always differs, and
    /// nothing treats it as a liveness signal.
    /// </summary>
    /// <summary>Returns true when a row actually changed, so callers can announce only that.</summary>
    private static async Task<bool> UpdateAutomationIdleAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string libraryId,
        string libraryName,
        string mediaType,
        string status,
        DateTimeOffset? nextMissingSearchUtc,
        DateTimeOffset? nextUpgradeSearchUtc,
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
                next_missing_search_utc = @nextMissingSearchUtc,
                next_upgrade_search_utc = @nextUpgradeSearchUtc,
                search_requested = @searchRequested,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId
              AND (
                     library_name IS NOT @libraryName
                  OR media_type IS NOT @mediaType
                  OR status IS NOT @status
                  OR next_search_utc IS NOT @nextSearchUtc
                  OR next_missing_search_utc IS NOT @nextMissingSearchUtc
                  OR next_upgrade_search_utc IS NOT @nextUpgradeSearchUtc
                  OR search_requested IS NOT @searchRequested
              );
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@libraryName", libraryName);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@status", status);
        AddParameter(command, "@nextSearchUtc", Earliest(nextMissingSearchUtc, nextUpgradeSearchUtc)?.ToString("O"));
        AddParameter(command, "@nextMissingSearchUtc", nextMissingSearchUtc?.ToString("O"));
        AddParameter(command, "@nextUpgradeSearchUtc", nextUpgradeSearchUtc?.ToString("O"));
        AddParameter(command, "@searchRequested", searchRequested ? 1 : 0);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DateTimeOffset NextSearchAfterQueue(
        DateTimeOffset now,
        LibraryAutomationPlanItem library)
        => now.AddMinutes(Math.Max(1, library.SearchIntervalMinutes));

    private static bool TryResolveScopeSearchKind(string? quickFilter, out string? searchKind)
    {
        searchKind = quickFilter?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "missing" => "missing",
            "upgrades" => "upgrade",
            _ => null
        };

        return quickFilter?.Trim().ToLowerInvariant() is null or "" or "all" or "missing" or "upgrades";
    }

    /// <summary>
    /// How often a library's subtitles are looked at, when nothing has just
    /// arrived to make them due sooner.
    ///
    /// <para><b>Deluno's number, not a setting.</b> The standing check asks
    /// whether Deluno can decide and explain the consequence once rather than add
    /// a control, and here it can: unlike an indexer interval there is nothing to
    /// tune. A pass over a covered shelf is a single query returning nothing, and
    /// a pass over a shelf with gaps is bounded by <c>MaxItemsPerRun</c> and then
    /// by each title's own backoff, which starts at hours and doubles to a
    /// fortnight. The interval is not what protects the providers; the backoff
    /// is.</para>
    ///
    /// <para>Five minutes, then, because the only thing it is really trading is
    /// idle database reads against how stale a shelf is allowed to get — and
    /// AUDIT-001 finding 4 is the reminder that idle reads are not free. At five
    /// minutes a shelf that wants subtitles costs well under one query a minute.
    /// An import does not wait even that long; see <c>MarkSubtitlesDueAsync</c>.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SubtitleCycleInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether this library's time-of-day window is open, and when it next
    /// opens if it is not.
    ///
    /// <para>One reader, because two things now ask: release searches and
    /// subtitle work. It was written inline for searches alone, and a second
    /// copy of a comparison that has to handle a window wrapping midnight is
    /// the shape this codebase keeps producing defects in.</para>
    ///
    /// <para>No window configured means always open, and the caller's own
    /// clocks decide the rest.</para>
    /// </summary>
    private static (bool InWindow, DateTimeOffset NextOpenUtc) SearchWindow(
        LibraryAutomationPlanItem library,
        DateTimeOffset now)
    {
        var daysMask = NormalizeSearchWindowDaysMask(library.SearchWindowDays);
        var dayAllowedToday = SearchWindowDayAllowed(daysMask, now.DayOfWeek);

        if (!library.SearchWindowStartHour.HasValue || !library.SearchWindowEndHour.HasValue)
        {
            if (dayAllowedToday)
            {
                return (true, now);
            }

            return (false, NextAllowedSearchWindowDayStart(daysMask, now));
        }

        var currentHour = now.Hour;
        var windowStart = library.SearchWindowStartHour.Value;
        var windowEnd = library.SearchWindowEndHour.Value;
        var inHourWindow = windowStart <= windowEnd
            ? currentHour >= windowStart && currentHour < windowEnd
            : currentHour >= windowStart || currentHour < windowEnd; // wraps midnight

        if (inHourWindow && dayAllowedToday)
        {
            return (true, now);
        }

        if (inHourWindow)
        {
            // The hour window is open, but today itself is excluded (#832):
            // skip straight to the next allowed day's window opening instead
            // of treating "now" as in-window.
            return (false, NextAllowedSearchWindowDayStart(daysMask, now.AddDays(1), windowStart));
        }

        var hoursUntilWindow = windowStart > currentHour
            ? windowStart - currentHour
            : 24 - currentHour + windowStart;
        var candidateOpenUtc = now.AddHours(hoursUntilWindow);
        if (SearchWindowDayAllowed(daysMask, candidateOpenUtc.DayOfWeek))
        {
            return (false, candidateOpenUtc);
        }

        return (false, NextAllowedSearchWindowDayStart(daysMask, candidateOpenUtc, windowStart));
    }

    /// <summary>Every day, used when a library's mask is unset or corrupt so the day axis never silently pauses a library that never configured one (#832).</summary>
    private const int AllSearchWindowDaysMask = 0b111_1111;

    private static int NormalizeSearchWindowDaysMask(int mask)
        => mask <= 0 || mask > AllSearchWindowDaysMask ? AllSearchWindowDaysMask : mask;

    private static bool SearchWindowDayAllowed(int mask, DayOfWeek day)
        => ((mask >> (int)day) & 1) != 0;

    /// <summary>Midnight of the next day (from <paramref name="from"/>, exclusive) whose bit is set in <paramref name="mask"/>.</summary>
    private static DateTimeOffset NextAllowedSearchWindowDayStart(int mask, DateTimeOffset from)
    {
        for (var offset = 1; offset <= 7; offset++)
        {
            var candidate = from.AddDays(offset);
            if (SearchWindowDayAllowed(mask, candidate.DayOfWeek))
            {
                return new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, candidate.Offset);
            }
        }

        return from.AddDays(1);
    }

    /// <summary>The next day (starting from <paramref name="from"/>, inclusive) whose bit is set in <paramref name="mask"/>, at <paramref name="windowStartHour"/>.</summary>
    private static DateTimeOffset NextAllowedSearchWindowDayStart(int mask, DateTimeOffset from, int windowStartHour)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var candidate = from.AddDays(offset);
            if (SearchWindowDayAllowed(mask, candidate.DayOfWeek))
            {
                return new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, windowStartHour, 0, 0, candidate.Offset);
            }
        }

        return from;
    }

    /// <summary>
    /// A file just landed, so subtitles stop waiting.
    ///
    /// <para>Bazarr fetches on import and Deluno should not be slower at the one
    /// moment a reader is watching. Without this a new episode sits until the
    /// next subtitle pass — and before that, until the next <i>release search</i>,
    /// which on the rig was twelve hours.</para>
    ///
    /// <para><b>One writer, on purpose.</b> Two import paths reach a library:
    /// <c>filesystem.import.execute</c> for a completed download and
    /// <c>library.import.existing</c> for bringing in what is already on disk.
    /// Hooking both would be the same rule written twice in two places that
    /// cannot check each other, which is what every defect here has been. Job
    /// completion is the one place that already knows an import finished, so it
    /// is the one place that says so.</para>
    ///
    /// <para><b>Every library, not the one that received the file.</b> Only
    /// <c>library.import.existing</c> carries a library id;
    /// <c>filesystem.import.execute</c> is related to a movie or a series and has
    /// none. Rather than teach the import pipeline to carry one just for this,
    /// every shelf that asked for languages becomes due — and a shelf that did
    /// not is untouched, because its cursor is never read. The cost of being
    /// blunt is one query per subtitle-wanting library, which returns nothing on
    /// a covered shelf; the cost of being precise would be a second writer.</para>
    /// </summary>
    private static async Task MarkSubtitlesDueAfterImportAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        CancellationToken cancellationToken)
    {
        if (job.JobType is not ("filesystem.import.execute" or "library.import.existing"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // NULL is "due now" to the planner, which is the same state a library
        // starts in — so this adds no third meaning to the column.
        command.CommandText =
            """
            UPDATE library_automation_state
            SET next_subtitle_search_utc = NULL
            WHERE next_subtitle_search_utc IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records when this library's subtitles are next due.
    ///
    /// <para>Its own writer, touching its own column, rather than a parameter on
    /// <c>UpdateAutomationIdleAsync</c>: that one also writes status,
    /// <c>next_search_utc</c> and <c>search_requested</c>, and every path that
    /// needs to move the subtitle clock is a path that must leave all three
    /// exactly as it found them.</para>
    ///
    /// <para>Guarded like its neighbour, so an idle library with a settled clock
    /// writes nothing on any of its ticks.</para>
    /// </summary>
    private static async Task<bool> WriteSubtitleScheduleAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string libraryId,
        DateTimeOffset? nextSubtitleSearchUtc,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET next_subtitle_search_utc = @nextSubtitleSearchUtc,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId
              AND next_subtitle_search_utc IS NOT @nextSubtitleSearchUtc;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@nextSubtitleSearchUtc", nextSubtitleSearchUtc?.ToString("O"));
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second)
        => first is null
            ? second
            : second is null
                ? first
                : first <= second ? first : second;

    private static async Task MarkAutomationStateRunningAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!LibrarySearchJobTypes.IsLibrarySearch(job.JobType) || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
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

    private static async Task MarkAutomationStateQueuedAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        JobQueueItem job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!LibrarySearchJobTypes.IsLibrarySearch(job.JobType) || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE library_automation_state
            SET
                status = 'queued',
                last_error = NULL,
                updated_utc = @updatedUtc
            WHERE library_id = @libraryId;
            """;
        AddParameter(command, "@libraryId", job.RelatedEntityId);
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
        if (!LibrarySearchJobTypes.IsLibrarySearch(job.JobType) || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
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
        if (!LibrarySearchJobTypes.IsLibrarySearch(job.JobType) || !string.Equals(job.RelatedEntityType, "library", StringComparison.OrdinalIgnoreCase))
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
            UpdatedUtc: ParseTimestamp(reader.GetString(11)),
            NextMissingSearchUtc: reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
            NextUpgradeSearchUtc: reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
            NextSubtitleSearchUtc: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)));
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
            CompletedUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            SearchKind: reader.IsDBNull(12) ? "combined" : reader.GetString(12));
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

    private static DownloadDispatchItem ReadDownloadDispatch(System.Data.Common.DbDataReader reader)
        => new(
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
            CreatedUtc: ParseTimestamp(reader.GetString(11)),
            GrabStatus: null,
            GrabAttemptedUtc: null,
            GrabResponseCode: null,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: null,
            TorrentHashOrItemId: null,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null);

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
            CreatedUtc: ParseTimestamp(reader.GetString(7)))
        {
            Title = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            FilePath = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null,
            SourceName = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
            ReasonGroup = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null
        };
    }

    public async Task<JobDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var from = fromDate.ToString("yyyy-MM-dd");
        var toExclusive = toDate.AddDays(1).ToString("yyyy-MM-dd");

        async Task<IReadOnlyDictionary<string, int>> ReadAsync(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@from", from);
            AddParameter(command, "@to", toExclusive);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    counts[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            return counts;
        }

        var completed = await ReadAsync(
            "SELECT substr(completed_utc, 1, 10) AS day, COUNT(*) FROM job_queue WHERE status = 'completed' AND completed_utc >= @from AND completed_utc < @to GROUP BY day;");
        var failed = await ReadAsync(
            "SELECT substr(completed_utc, 1, 10) AS day, COUNT(*) FROM job_queue WHERE status = 'failed' AND completed_utc >= @from AND completed_utc < @to GROUP BY day;");
        var grabs = await ReadAsync(
            "SELECT substr(created_utc, 1, 10) AS day, COUNT(*) FROM download_dispatches WHERE created_utc >= @from AND created_utc < @to GROUP BY day;");

        return new JobDailyMetrics(completed, failed, grabs);
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

    /// <summary>
    /// A job's payload as its "queued" Activity line keeps it: without a carried
    /// .torrent file, which a connectivity retry holds so the tracker is not
    /// fetched again (#1062) and which would otherwise be copied into Activity
    /// on every attempt.
    /// </summary>
    public static string? ActivityDetailsOf(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson) || !payloadJson.Contains("\"torrentFile\"", StringComparison.OrdinalIgnoreCase))
        {
            return payloadJson;
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson);
            if (node is System.Text.Json.Nodes.JsonObject payload)
            {
                foreach (var name in payload.Select(pair => pair.Key).Where(key => string.Equals(key, "torrentFile", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    payload.Remove(name);
                }

                return payload.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return payloadJson;
    }

    private static string FormatQueuedMessage(string jobType, string source, string? payloadJson, string? relatedEntityType = null)
    {
        if (LibrarySearchJobTypes.IsLibrarySearch(jobType))
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                var trigger = SearchStartedWords.WhenQueued(context.TriggeredBy);
                return $"Queued a library check for {context.LibraryName} {trigger}.";
            }

            return "Queued a library check.";
        }

        return JobTypeWords.TryGetValue(jobType, out var words)
            ? $"Added {words.Queued}{MediaKindSuffix(jobType, relatedEntityType)} to the queue."
            : $"Added a background task. Started by {JobSourceLabels.ForSentence(source)}.";
    }

    private static string FormatStartedMessage(string jobType, string? payloadJson, string? relatedEntityType = null)
    {
        if (LibrarySearchJobTypes.IsLibrarySearch(jobType))
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                return $"Started checking {context.LibraryName}.";
            }

            return "Started checking a library.";
        }

        return JobTypeWords.TryGetValue(jobType, out var words)
            ? words.Started.TrimEnd('.') + MediaKindSuffix(jobType, relatedEntityType) + "."
            : "Started a background task.";
    }

    /// <summary>
    /// Job types whose own words already name Movies or TV, so appending it a
    /// second time would read as "Started refreshing movie metadata (Movies)."
    /// </summary>
    private static readonly IReadOnlySet<string> JobTypesThatAlreadyNameTheirMediaKind = new HashSet<string>(StringComparer.Ordinal)
    {
        "movies.catalog.refresh", "series.catalog.refresh",
        "movies.metadata.refresh", "series.metadata.refresh",
        "movies.quality.recalculate", "series.quality.recalculate",
        "movies.collection.sync"
    };

    /// <summary>
    /// " (Movies)" / " (TV)", for the job types (#916, most visibly
    /// <c>library.media.probe</c>) whose own wording is the same whichever
    /// kind of library they ran for — the routine-job flood this fixed had
    /// six near-identical lines an hour with nothing saying which library
    /// among them.
    /// </summary>
    private static string MediaKindSuffix(string jobType, string? relatedEntityType)
    {
        if (JobTypesThatAlreadyNameTheirMediaKind.Contains(jobType))
        {
            return string.Empty;
        }

        return relatedEntityType?.Trim().ToLowerInvariant() switch
        {
            "movie" => " (Movies)",
            "series" => " (TV)",
            _ => string.Empty
        };
    }

    private static string FormatQueuedTitle(string jobType, string? payloadJson)
    {
        if (LibrarySearchJobTypes.IsLibrarySearch(jobType))
        {
            var context = ParseLibraryPayload(payloadJson);
            if (context is not null)
            {
                return context.LibraryName;
            }
        }

        return JobTypeWords.TryGetValue(jobType, out var words) ? words.Title : JobTypeLabels.For(jobType);
    }

    /// <summary>
    /// What each kind of job is called, in the three places Activity needs to
    /// say it.
    ///
    /// <para><b>One table, and it used to be three switches.</b> They were
    /// separate lists in three methods that could not check each other, which is
    /// the shape behind every defect worth finding in this codebase — and it had
    /// already gone wrong here, quietly. <c>episode.search</c>,
    /// <c>intake.sync</c> and <c>library.import.existing</c> were in none of
    /// them, so a person watching Activity was shown the raw string
    /// <c>library.import.existing</c> where it meant to say "Library scan". A
    /// fourth omission was about to be made the same way when
    /// <c>subtitle.sync</c> was added.</para>
    ///
    /// <para>Adding a job type is now one row. Forgetting to name one is still
    /// possible, but it is one omission rather than three, and
    /// <c>Every_job_type_is_named</c> fails on it.</para>
    /// </summary>
    /// <param name="Queued">
    /// Reads after "Added " and before " to the queue", so it is a noun phrase:
    /// <i>a subtitle search</i>.
    /// </param>
    /// <param name="Started">A whole sentence, because it stands alone.</param>
    /// <param name="Title">Two or three words for a queue row.</param>
    public sealed record JobWords(string Queued, string Started, string Title);

    public static readonly IReadOnlyDictionary<string, JobWords> JobTypeWords =
        new Dictionary<string, JobWords>(StringComparer.Ordinal)
        {
            ["library.subtitles.scan"] = new(
                "a subtitle check", "Started reading your files for subtitles.", "Subtitle check"),
            ["library.media.probe"] = new(
                "a media read", "Started reading what your files actually are.", "Media read"),
            ["library.subtitles.search"] = new(
                "a subtitle search", "Started looking for the subtitles you asked for.", "Subtitle search"),
            ["subtitle.sync"] = new(
                "a subtitle timing check",
                "Started timing a subtitle against the video's audio.",
                "Subtitle timing"),
            ["subtitle.title.search"] = new(
                "a subtitle search",
                "Started looking for the subtitles you asked to search for.",
                "Subtitle search"),
            ["movies.catalog.refresh"] = new(
                "a movie check", "Started checking your movie library.", "Movie check"),
            ["series.catalog.refresh"] = new(
                "a TV show check", "Started checking your TV show library.", "TV show check"),
            ["filesystem.import.execute"] = new(
                "a file import", "Started importing a completed download.", "File import"),
            ["library.import.existing"] = new(
                "a library scan", "Started reading a folder you already had.", "Library scan"),
            ["intake.sync"] = new(
                "a list sync", "Started reading one of your lists.", "List sync"),
            ["episode.search"] = new(
                "an episode search", "Started looking for an episode.", "Episode search"),
            ["series.search.season"] = new(
                "a season search", "Started looking for a whole season.", "Season search"),
            ["movies.search.manual"] = new(
                "a film search", "Started looking for a film you chose.", "Film search"),
            ["series.search.manual"] = new(
                "a show search", "Started looking for a show you chose.", "Show search"),
            ["movies.metadata.refresh"] = new(
                "a movie metadata refresh", "Started refreshing movie metadata.", "Movie metadata refresh"),
            ["series.metadata.refresh"] = new(
                "a TV metadata refresh", "Started refreshing TV metadata.", "TV metadata refresh"),
            ["movies.quality.recalculate"] = new(
                "a movie quality refresh", "Started refreshing movie quality decisions.", "Movie quality refresh"),
            ["series.quality.recalculate"] = new(
                "a TV quality refresh", "Started refreshing TV quality decisions.", "TV quality refresh"),
            ["movies.collection.sync"] = new(
                "a movie collection refresh", "Started refreshing a movie collection.", "Movie collection refresh"),
            ["media-servers.refresh"] = new(
                "a media server refresh", "Started telling your media servers about changed files.", "Media server refresh"),
            ["media-servers.refresh-retry"] = new(
                "a media server refresh retry", "Started retrying a media server that had not answered.", "Media server refresh retry"),
            ["download-client.retry-grab"] = new(
                "a download client retry", "Started resending a release to a download client that had not answered.", "Download client retry")
        };

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

    /// <summary>
    /// What a job that ran out of attempts says about it, in the words the
    /// screens use for that status ("Gave up", #644) rather than the queue's
    /// own name for it. One sentence in one place: it was written out three
    /// times, and every copy said "moved to dead-letter".
    /// </summary>
    private static string GaveUpSentence(int maxAttempts)
        => $"Deluno tried {maxAttempts} time{(maxAttempts == 1 ? string.Empty : "s")} and gave up.";

    private static TimeSpan CalculateRetryDelay(int attempts)
    {
        var boundedAttempt = Math.Clamp(attempts, 1, 10);
        var delay = TimeSpan.FromSeconds(Math.Pow(2, boundedAttempt - 1) * 30);
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    /// <summary>The one shared rule (Deluno.Contracts), so Jobs and Monitoring cannot drift apart again.</summary>
    private static string SeverityForCategory(string category) => ActivityEventSeverity.For(category);

    private static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            // A payload with no library name reads as "a library", never as a blank in the sentence (#1203).
            var payload = JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson, PayloadJsonOptions);
            return string.IsNullOrWhiteSpace(payload?.LibraryName) ? null : payload;
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

    /// <summary>
    /// The activity line for a dispatch, told from its outcome.
    /// </summary>
    private static (string Category, string Message) DescribeDispatch(
        string status,
        string releaseName,
        string downloadClientName,
        string? grabFailureCode,
        IntegrationFailure? failure = null)
    {
        // The indexer would not hand the release over: the client was never asked,
        // so the line must not blame it (#1062).
        if (failure is { Operation: "fetch" } && status is not "sent")
        {
            return ("download.dispatch.failed", $"Deluno could not get {releaseName} from the indexer, so it was not sent to {downloadClientName}: {failure.Message}");
        }

        var reason = string.IsNullOrWhiteSpace(grabFailureCode) ? "." : $" — {grabFailureCode}.";
        return status switch
        {
            "sent" => ("download.dispatch.recorded", $"Deluno sent {releaseName} to {downloadClientName}."),
            "planned" => ("download.dispatch.planned", $"Deluno chose {releaseName} for {downloadClientName} and has not sent it yet."),
            "paused" => ("download.dispatch.paused", $"Deluno held {releaseName} back because {downloadClientName} is paused."),
            "circuitOpen" => ("download.dispatch.failed", $"Deluno stopped sending to {downloadClientName} after repeated failures, so {releaseName} was not sent{reason}"),
            "notFound" => ("download.dispatch.failed", $"Deluno could not find {downloadClientName} to send {releaseName} to{reason}"),
            _ => ("download.dispatch.failed", $"Deluno could not send {releaseName} to {downloadClientName}{reason}")
        };
    }

    private static string MapStatusToGrabStatus(string status) =>
        status switch
        {
            "sent" => "succeeded",
            "failed" => "failed",
            "planned" => "planned",
            "circuitOpen" => "circuit_open",
            "notFound" => "not_found",
            "paused" => "paused",
            _ => status
        };

    private static DispatchDecisionTelemetry? TryParseDispatchDecisionTelemetry(string? notesJson)
    {
        if (string.IsNullOrWhiteSpace(notesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(notesJson);
            var root = document.RootElement;
            var candidate = ResolveDecisionCandidate(root);
            if (candidate is null)
            {
                return null;
            }

            return new DispatchDecisionTelemetry(
                Quality: GetString(candidate.Value, "Quality"),
                Score: GetInt32(candidate.Value, "Score"),
                MeetsCutoff: GetBoolean(candidate.Value, "MeetsCutoff"),
                DecisionStatus: GetString(candidate.Value, "DecisionStatus"),
                QualityDelta: GetInt32(candidate.Value, "QualityDelta"),
                CustomFormatScore: GetInt32(candidate.Value, "CustomFormatScore"),
                SeederScore: GetInt32(candidate.Value, "SeederScore"),
                SizeScore: GetInt32(candidate.Value, "SizeScore"),
                ReleaseGroup: GetString(candidate.Value, "ReleaseGroup"),
                EstimatedBitrateMbps: GetDouble(candidate.Value, "EstimatedBitrateMbps"),
                SizeBytes: GetInt64(candidate.Value, "SizeBytes"),
                Seeders: GetInt32(candidate.Value, "Seeders"),
                PolicyVersion: GetString(candidate.Value, "PolicyVersion"),
                MatchedCustomFormatsJson: GetRawJson(candidate.Value, "MatchedCustomFormats"),
                ReasonsJson: GetRawJson(candidate.Value, "DecisionReasons"),
                RiskFlagsJson: GetRawJson(candidate.Value, "RiskFlags"),
                OverrideUsed: GetBoolean(root, "ForceOverride") ?? false,
                OverrideReason: GetString(root, "OverrideReason"));
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? ResolveDecisionCandidate(JsonElement root)
    {
        if (TryGetPropertyIgnoreCase(root, "decision", out var decision) &&
            TryGetPropertyIgnoreCase(decision, "candidate", out var decisionCandidate))
        {
            return decisionCandidate;
        }

        if (TryGetPropertyIgnoreCase(root, "searchPlan", out var searchPlan) &&
            TryGetPropertyIgnoreCase(searchPlan, "bestCandidate", out var bestCandidate))
        {
            return bestCandidate;
        }

        if (TryGetPropertyIgnoreCase(root, "bestCandidate", out var rootBestCandidate))
        {
            return rootBestCandidate;
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? GetInt64(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

    private static double? GetDouble(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool? GetBoolean(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static string? GetRawJson(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) ? value.GetRawText() : null;

    private sealed record DispatchDecisionTelemetry(
        string? Quality,
        int? Score,
        bool? MeetsCutoff,
        string? DecisionStatus,
        int? QualityDelta,
        int? CustomFormatScore,
        int? SeederScore,
        int? SizeScore,
        string? ReleaseGroup,
        double? EstimatedBitrateMbps,
        long? SizeBytes,
        int? Seeders,
        string? PolicyVersion,
        string? MatchedCustomFormatsJson,
        string? ReasonsJson,
        string? RiskFlagsJson,
        bool OverrideUsed,
        string? OverrideReason);
}
