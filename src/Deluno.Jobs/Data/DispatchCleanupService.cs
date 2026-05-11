using Deluno.Jobs.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public sealed class DispatchCleanupService(
    IDownloadDispatchesRepository dispatchesRepository,
    TimeProvider timeProvider,
    ILogger<DispatchCleanupService> logger)
    : IDispatchCleanupService
{
    private static readonly TimeSpan StaleFailedAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaleUnresolvedAge = TimeSpan.FromDays(14);
    private const int BatchLimit = 50;

    public async Task<DispatchCleanupResult> RunCleanupPassAsync(CancellationToken cancellationToken)
    {
        var archivedCount = 0;
        var skippedCount = 0;

        var stale = await dispatchesRepository.FindStaleFailedDispatchesAsync(
            StaleFailedAge, BatchLimit, cancellationToken);

        foreach (var dispatch in stale)
        {
            var policy = ChoosePolicy(dispatch.GrabFailureCode, dispatch.ImportFailureCode);

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

        var summary = archivedCount > 0 || skippedCount > 0
            ? $"Archived {archivedCount} stale dispatch(es); skipped {skippedCount}."
            : "No stale dispatches found.";

        return new DispatchCleanupResult(archivedCount, skippedCount, summary);
    }

    private static CleanupPolicy ChoosePolicy(string? grabFailureCode, string? importFailureCode)
    {
        var code = importFailureCode ?? grabFailureCode ?? string.Empty;
        return code switch
        {
            "max-retries-exceeded" => CleanupPolicy.Archive,
            "notFound" or "paused" or "planned" => CleanupPolicy.Skip,
            "circuitOpen" => CleanupPolicy.Skip,
            _ => CleanupPolicy.Archive
        };
    }

    private enum CleanupPolicy { Archive, Skip }
}
