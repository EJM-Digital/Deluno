using System.Text.Json;
using Deluno.Jobs.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public interface IDownloadRetryService
{
    Task<DownloadRetryResult> RunRetryPassAsync(CancellationToken cancellationToken);
}

public sealed record DownloadRetryResult(
    int RetriedCount,
    int SkippedCount,
    string Summary);

public sealed class DownloadRetryService(
    IDownloadDispatchesRepository dispatchesRepository,
    IJobScheduler jobScheduler,
    TimeProvider timeProvider,
    ILogger<DownloadRetryService> logger)
    : IDownloadRetryService
{
    private const int BatchLimit = 50;

    public async Task<DownloadRetryResult> RunRetryPassAsync(CancellationToken cancellationToken)
    {
        var retriedCount = 0;
        var skippedCount = 0;
        var now = timeProvider.GetUtcNow();

        var failedDispatches = await dispatchesRepository.FindDispatchesEligibleForRetryAsync(
            BatchLimit,
            cancellationToken);

        foreach (var dispatch in failedDispatches)
        {
            if (dispatch.GrabStatus == "failed" && dispatch.NextRetryEligibleUtc != null)
            {
                if (dispatch.NextRetryEligibleUtc > now)
                {
                    skippedCount++;
                    continue;
                }

                var failureCode = dispatch.GrabFailureCode ?? "unknown";
                var policy = RetryPolicies.GetPolicyForKind(failureCode);

                if (policy.MaxRetries == 0)
                {
                    logger.LogDebug(
                        "Dispatch {DispatchId} has failure code {FailureCode} with no retry policy.",
                        dispatch.Id, failureCode);
                    skippedCount++;
                    continue;
                }

                var nextRetryNumber = (dispatch.AttemptCount ?? 0) + 1;
                if (nextRetryNumber > policy.MaxRetries)
                {
                    logger.LogDebug(
                        "Dispatch {DispatchId} exceeded max retries ({MaxRetries}).",
                        dispatch.Id, policy.MaxRetries);
                    skippedCount++;
                    continue;
                }

                var nextDelay = RetryPolicies.CalculateNextRetryDelay(nextRetryNumber, policy);
                var nextRetryEligible = now.Add(nextDelay);

                var jobPayload = JsonSerializer.Serialize(new
                {
                    libraryId = dispatch.LibraryId,
                    libraryName = dispatch.LibraryId,
                    mediaType = string.IsNullOrWhiteSpace(dispatch.MediaType) ? "movies" : dispatch.MediaType,
                    maxItems = 1,
                    retryDelayHours = 24,
                    triggeredBy = "manual",
                    retryOfDispatchId = dispatch.Id
                });

                try
                {
                    await jobScheduler.EnqueueAsync(
                        new EnqueueJobRequest(
                            JobType: "library.search",
                            Source: "DownloadRetryService",
                            PayloadJson: jobPayload,
                            RelatedEntityType: "download_dispatch",
                            RelatedEntityId: dispatch.Id,
                            ScheduledUtc: nextRetryEligible,
                            IdempotencyKey: $"retry-{dispatch.Id}-{nextRetryNumber}"),
                        cancellationToken);

                    await dispatchesRepository.UpdateFailureRetryWindowAsync(
                        dispatch.Id,
                        nextRetryEligible,
                        nextRetryNumber,
                        cancellationToken);

                    logger.LogInformation(
                        "Queued retry {RetryNumber} for dispatch {DispatchId} ({ReleaseName}), scheduled for {ScheduledUtc}.",
                        nextRetryNumber, dispatch.Id, dispatch.ReleaseName, nextRetryEligible);

                    retriedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to queue retry for dispatch {DispatchId}.",
                        dispatch.Id);
                }
            }
        }

        if (retriedCount > 0)
        {
            logger.LogInformation(
                "Download retry pass complete: queued {Retried} retry job(s); skipped {Skipped}.",
                retriedCount, skippedCount);
        }

        var summary = retriedCount > 0 || skippedCount > 0
            ? $"Queued {retriedCount} retry job(s); skipped {skippedCount}."
            : "No dispatches eligible for retry.";

        return new DownloadRetryResult(retriedCount, skippedCount, summary);
    }
}
