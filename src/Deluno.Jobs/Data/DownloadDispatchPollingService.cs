using System.Data.Common;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Microsoft.Data.Sqlite;

namespace Deluno.Jobs.Data;

public sealed class DownloadDispatchPollingService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    CompositeDispatchRecoveryHandler recoveryHandler,
    IActivityFeedRepository activityFeedRepository,
    IDownloadDispatchesRepository downloadDispatchesRepository,
    IDispatchAlertRepository alertRepository,
    IJobScheduler jobScheduler,
    Deluno.Realtime.IRealtimeEventPublisher realtimeEventPublisher)
    : IDownloadDispatchPollingService
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromHours(24);

    public async Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var report = new DownloadDispatchPollingReport(
            UnresolvedDispatchesChecked: 0,
            GrabTimeoutsDetected: 0,
            DetectionTimeoutsDetected: 0,
            ImportTimeoutsDetected: 0,
            ImportFailuresDetected: 0,
            RecoveryCasesRecorded: 0);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        var unresolvedDispatches = await QueryUnresolvedDispatchesAsync(connection, cancellationToken);
        report = report with { UnresolvedDispatchesChecked = unresolvedDispatches.Count };

        foreach (var dispatch in unresolvedDispatches)
        {
            if (dispatch.DetectedUtc is not null && dispatch.ImportStatus is null)
            {
                await realtimeEventPublisher.PublishDispatchDetectedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    null,
                    null,
                    cancellationToken);
            }

            if (dispatch.ImportStatus == "completed" && dispatch.ImportDetectedUtc is not null)
            {
                await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    true,
                    null,
                    null,
                    cancellationToken);
            }
            else if (dispatch.ImportStatus == "failed" && !string.IsNullOrWhiteSpace(dispatch.ImportFailureCode))
            {
                await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    false,
                    null,
                    dispatch.ImportFailureMessage ?? dispatch.ImportFailureCode,
                    cancellationToken);
            }

            if (dispatch.GrabStatus == "succeeded" && dispatch.DetectedUtc is null)
            {
                var age = now - dispatch.GrabAttemptedUtc;
                if (age > GrabTimeout)
                {
                    await RecordGrabTimeoutRecoveryAsync(
                        dispatch,
                        recoveryHandler,
                        activityFeedRepository,
                        alertRepository,
                        cancellationToken);
                    report = report with { GrabTimeoutsDetected = report.GrabTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                }
            }
            else if (dispatch.DetectedUtc is not null && dispatch.ImportStatus is null)
            {
                var age = now - dispatch.DetectedUtc.Value;
                if (age > DetectionTimeout)
                {
                    await RecordDetectionTimeoutRecoveryAsync(
                        dispatch,
                        recoveryHandler,
                        activityFeedRepository,
                        alertRepository,
                        cancellationToken);
                    report = report with { DetectionTimeoutsDetected = report.DetectionTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                }
            }
            else if (dispatch.ImportStatus == "pending" && dispatch.ImportDetectedUtc is not null)
            {
                var age = now - dispatch.ImportDetectedUtc.Value;
                if (age > ImportTimeout)
                {
                    await RecordImportTimeoutRecoveryAsync(
                        dispatch,
                        recoveryHandler,
                        activityFeedRepository,
                        alertRepository,
                        cancellationToken);
                    report = report with { ImportTimeoutsDetected = report.ImportTimeoutsDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
                }
            }
            else if (dispatch.ImportStatus == "failed" && !string.IsNullOrWhiteSpace(dispatch.ImportFailureCode))
            {
                await RecordImportFailureRecoveryAsync(
                    dispatch,
                    recoveryHandler,
                    activityFeedRepository,
                    alertRepository,
                    cancellationToken);
                report = report with { ImportFailuresDetected = report.ImportFailuresDetected + 1, RecoveryCasesRecorded = report.RecoveryCasesRecorded + 1 };
            }
        }

        await ArchiveSuccessfulDispatchesAsync(connection, cancellationToken);

        return report;
    }

    private static async Task<IReadOnlyList<UnresolvedDispatch>> QueryUnresolvedDispatchesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var items = new List<UnresolvedDispatch>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, library_id, media_type, entity_type, entity_id, release_name,
                download_client_id, download_client_name, grab_status, grab_attempted_utc,
                detected_utc, import_status, import_detected_utc, import_failure_code, import_failure_message
            FROM download_dispatches
            WHERE (grab_status IS NULL OR grab_status = 'succeeded' AND detected_utc IS NULL)
               OR (detected_utc IS NOT NULL AND import_status IS NULL)
               OR (import_status IN ('pending', 'failed'))
            ORDER BY grab_attempted_utc DESC
            LIMIT 500;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new UnresolvedDispatch(
                Id: reader.GetString(0),
                LibraryId: reader.GetString(1),
                MediaType: reader.GetString(2),
                EntityType: reader.GetString(3),
                EntityId: reader.GetString(4),
                ReleaseName: reader.GetString(5),
                DownloadClientId: reader.GetString(6),
                DownloadClientName: reader.GetString(7),
                GrabStatus: reader.IsDBNull(8) ? null : reader.GetString(8),
                GrabAttemptedUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                DetectedUtc: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                ImportStatus: reader.IsDBNull(11) ? null : reader.GetString(11),
                ImportDetectedUtc: reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
                ImportFailureCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                ImportFailureMessage: reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return items;
    }

    private async Task RecordGrabTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        IActivityFeedRepository activityFeedRepository,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType
        });

        await recoveryHandler.HandleGrabTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            detailsJson,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.grab.timeout",
            $"{title} was not detected in {dispatch.DownloadClientName} after {GrabTimeout.TotalHours:F0} hours.",
            detailsJson,
            null,
            "download",
            dispatch.Id,
            cancellationToken);

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            $"Download not detected in {dispatch.DownloadClientName}",
            "grab-timeout",
            "warning",
            new Dictionary<string, string> { { "client", dispatch.DownloadClientName } },
            cancellationToken);

        await QueueRetryJobAsync(
            dispatch.Id,
            "search_retry_grab",
            "grab_timeout_recovery",
            detailsJson,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task RecordDetectionTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        IActivityFeedRepository activityFeedRepository,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId
        });

        await recoveryHandler.HandleDetectionTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            detailsJson,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.detection.timeout",
            $"{title} was detected but never imported after {DetectionTimeout.TotalHours:F0} hours.",
            detailsJson,
            null,
            "download",
            dispatch.Id,
            cancellationToken);

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            "Download detected but import did not complete",
            "detection-timeout",
            "warning",
            new Dictionary<string, string> { { "mediaType", dispatch.MediaType } },
            cancellationToken);

        await QueueRetryJobAsync(
            dispatch.Id,
            "search_retry_detection",
            "detection_timeout_recovery",
            detailsJson,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task RecordImportTimeoutRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        IActivityFeedRepository activityFeedRepository,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var detailsJson = JsonSerializer.Serialize(new
        {
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId
        });

        await recoveryHandler.HandleImportTimeoutAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            detailsJson,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.import.timeout",
            $"{title} import was detected but never completed after {ImportTimeout.TotalHours:F0} hours.",
            detailsJson,
            null,
            "download",
            dispatch.Id,
            cancellationToken);

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            "Import process did not complete within timeout period",
            "import-timeout",
            "error",
            new Dictionary<string, string> { { "mediaType", dispatch.MediaType } },
            cancellationToken);

        await QueueRetryJobAsync(
            dispatch.Id,
            "search_retry_import",
            "import_timeout_recovery",
            detailsJson,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task RecordImportFailureRecoveryAsync(
        UnresolvedDispatch dispatch,
        IDispatchRecoveryHandler recoveryHandler,
        IActivityFeedRepository activityFeedRepository,
        IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var title = dispatch.ReleaseName;
        var failureReason = !string.IsNullOrWhiteSpace(dispatch.ImportFailureMessage)
            ? dispatch.ImportFailureMessage
            : dispatch.ImportFailureCode ?? "unknown";
        var detailsJson = JsonSerializer.Serialize(new
        {
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.MediaType,
            dispatch.EntityId,
            dispatch.ImportFailureCode,
            dispatch.ImportFailureMessage
        });

        await recoveryHandler.HandleImportFailureAsync(
            title,
            dispatch.MediaType,
            dispatch.DownloadClientId,
            dispatch.DownloadClientName,
            dispatch.ReleaseName,
            dispatch.EntityId,
            dispatch.ImportFailureCode ?? "",
            dispatch.ImportFailureMessage ?? "",
            detailsJson,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.import.failed",
            $"{title} import failed: {failureReason}",
            detailsJson,
            null,
            "download",
            dispatch.Id,
            cancellationToken);

        await alertRepository.CreateAlertAsync(
            dispatch.Id,
            title,
            $"Import failed: {failureReason}",
            "import-failed",
            "error",
            new Dictionary<string, string>
            {
                { "failureCode", dispatch.ImportFailureCode ?? "" },
                { "failureMessage", dispatch.ImportFailureMessage ?? "" }
            },
            cancellationToken);

        await QueueRetryJobAsync(
            dispatch.Id,
            "search_retry_import_failure",
            "import_failure_recovery",
            detailsJson,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task ArchiveSuccessfulDispatchesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id FROM download_dispatches
            WHERE import_status = 'completed' AND archived_utc IS NULL
            ORDER BY import_detected_utc DESC
            LIMIT 100;
            """;

        var dispatchIds = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dispatchIds.Add(reader.GetString(0));
        }

        foreach (var dispatchId in dispatchIds)
        {
            await downloadDispatchesRepository.ArchiveDispatchAsync(
                dispatchId,
                "import_completed",
                cancellationToken);
        }
    }

    private async Task QueueRetryJobAsync(
        string dispatchId,
        string jobType,
        string failureKind,
        string payloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = RetryPolicies.GetPolicyForKind(failureKind);
        if (policy.MaxRetries == 0)
            return;

        var nextRetryDelay = RetryPolicies.CalculateNextRetryDelay(1, policy);
        var scheduledUtc = now.Add(nextRetryDelay);

        var retryPayload = JsonSerializer.Serialize(new
        {
            dispatchId,
            failureKind,
            attemptNumber = 1,
            originalPayload = payloadJson
        });

        var request = new EnqueueJobRequest(
            JobType: jobType,
            Source: "dispatch_polling_recovery",
            PayloadJson: retryPayload,
            RelatedEntityType: "download_dispatch",
            RelatedEntityId: dispatchId,
            ScheduledUtc: scheduledUtc,
            MaxAttempts: policy.MaxRetries);

        await jobScheduler.EnqueueAsync(request, cancellationToken);
    }

    private sealed record UnresolvedDispatch(
        string Id,
        string LibraryId,
        string MediaType,
        string EntityType,
        string EntityId,
        string ReleaseName,
        string DownloadClientId,
        string DownloadClientName,
        string? GrabStatus,
        DateTimeOffset? GrabAttemptedUtc,
        DateTimeOffset? DetectedUtc,
        string? ImportStatus,
        DateTimeOffset? ImportDetectedUtc,
        string? ImportFailureCode,
        string? ImportFailureMessage);
}
