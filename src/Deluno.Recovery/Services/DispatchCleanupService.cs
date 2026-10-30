using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Recovery.Services;

public sealed class DispatchCleanupService(
    IDownloadDispatchesRepository dispatchesRepository,
    TimeProvider timeProvider,
    ILogger<DispatchCleanupService> logger,
    IIndexerQueryStatsRepository? indexerQueryStatsRepository = null,
    IJobQueueRepository? jobQueueRepository = null,
    IAutomationIdempotencyStore? automationIdempotencyStore = null)
    : IDispatchCleanupService
{
    private static readonly TimeSpan StaleFailedAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan IndexerQueryStatsRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// A year, because that is the longest window the dashboard charts finished
    /// jobs over. Nothing else reads a completed job, and nothing used to delete
    /// one: the media read alone adds about 35,000 rows a year.
    /// </summary>
    private static readonly TimeSpan CompletedJobRetention = TimeSpan.FromDays(365);
    /// <summary>Integration API repeat keys: a repeat inside 7 days gets the stored answer; older keys are removed.</summary>
    private static readonly TimeSpan AutomationIdempotencyRetention = TimeSpan.FromDays(7);
    private const int BatchLimit = 50;

    public async Task<DispatchCleanupResult> RunCleanupPassAsync(CancellationToken cancellationToken)
    {
        var archivedCount = 0;
        var skippedCount = 0;

        var stale = await dispatchesRepository.FindStaleFailedDispatchesAsync(
            StaleFailedAge, BatchLimit, cancellationToken);

        foreach (var dispatch in stale)
        {
            var policy = ChoosePolicy(dispatch.GrabStatus, dispatch.GrabFailureCode, dispatch.ImportFailureCode);

            if (policy == CleanupPolicy.Archive)
            {
                await dispatchesRepository.ArchiveDispatchAsync(
                    dispatch.Id,
                    "auto-cleanup: dispatch exceeded retention period",
                    cancellationToken);

                await dispatchesRepository.RecordTimelineEventAsync(
                    dispatch.Id,
                    "auto-archived",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason = "Exceeded retention period",
                        policy = "archive",
                        agedays = (int)(timeProvider.GetUtcNow() - dispatch.CreatedUtc).TotalDays
                    }),
                    cancellationToken);

                logger.LogDebug(
                    "Auto-archived stale dispatch {DispatchId} ({ReleaseName}), failure code: {FailureCode}.",
                    dispatch.Id, dispatch.ReleaseName, dispatch.GrabFailureCode ?? dispatch.ImportFailureCode);

                archivedCount++;
            }
            else
            {
                skippedCount++;
            }
        }

        if (archivedCount > 0)
        {
            logger.LogInformation(
                "Dispatch cleanup pass complete: archived {Archived}, skipped {Skipped}.",
                archivedCount, skippedCount);
        }

        if (indexerQueryStatsRepository is not null)
        {
            try
            {
                var prunedCount = await indexerQueryStatsRepository.PruneAsync(
                    timeProvider.GetUtcNow() - IndexerQueryStatsRetention,
                    cancellationToken);
                if (prunedCount > 0)
                {
                    logger.LogInformation(
                        "Indexer query telemetry housekeeping removed {PrunedCount} event(s) older than {RetentionDays} days.",
                        prunedCount,
                        IndexerQueryStatsRetention.TotalDays);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostics retention must not prevent dispatch recovery.
                logger.LogWarning(ex, "Indexer query telemetry housekeeping failed.");
            }
        }

        if (jobQueueRepository is not null)
        {
            try
            {
                var prunedJobs = await jobQueueRepository.PruneCompletedAsync(
                    timeProvider.GetUtcNow() - CompletedJobRetention,
                    cancellationToken);
                if (prunedJobs > 0)
                {
                    logger.LogInformation(
                        "Job housekeeping removed {PrunedCount} completed job(s) older than {RetentionDays} days.",
                        prunedJobs,
                        CompletedJobRetention.TotalDays);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Job retention must not prevent dispatch recovery.
                logger.LogWarning(ex, "Completed job housekeeping failed.");
            }
        }

        if (automationIdempotencyStore is not null)
        {
            try
            {
                var prunedKeys = await automationIdempotencyStore.PruneAsync(
                    timeProvider.GetUtcNow() - AutomationIdempotencyRetention,
                    cancellationToken);
                if (prunedKeys > 0)
                {
                    logger.LogInformation(
                        "Integration housekeeping removed {PrunedCount} saved repeat key(s) older than {RetentionDays} days.",
                        prunedKeys,
                        AutomationIdempotencyRetention.TotalDays);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Integration repeat key housekeeping failed.");
            }
        }

        var summary = archivedCount > 0 || skippedCount > 0
            ? $"Archived {archivedCount} stale dispatch(es); skipped {skippedCount}."
            : "No stale dispatches found.";

        return new DispatchCleanupResult(archivedCount, skippedCount, summary);
    }

    private static CleanupPolicy ChoosePolicy(string? grabStatus, string? grabFailureCode, string? importFailureCode)
    {
        var code = importFailureCode ?? grabFailureCode ?? string.Empty;
        if (code == "max-retries-exceeded")
        {
            return CleanupPolicy.Archive;
        }

        // A grab waiting on something (its client gone, paused, not yet sent, or stopped after repeated
        // failures) is left alone, whether the store kept that as the failure code or as the grab status (#1412).
        return GrabStatuses.IsStoredWaiting(code) || GrabStatuses.IsStoredWaiting(grabStatus)
            ? CleanupPolicy.Skip
            : CleanupPolicy.Archive;
    }

    private enum CleanupPolicy { Archive, Skip }
}
