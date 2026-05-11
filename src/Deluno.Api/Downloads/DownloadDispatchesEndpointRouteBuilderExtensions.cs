using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Downloads;

public static class DownloadDispatchesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDownloadDispatchesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/download-dispatches")
            .WithName("DownloadDispatches");

        group.MapGet(string.Empty, GetDispatches)
            .WithName("List Download Dispatches")
            .WithDescription("Query download dispatches with filtering and pagination");

        group.MapGet("/{dispatchId}", GetDispatch)
            .WithName("Get Download Dispatch")
            .WithDescription("Get a single dispatch with full timeline");

        group.MapGet("/unresolved", GetUnresolvedDispatches)
            .WithName("List Unresolved Dispatches")
            .WithDescription("Find dispatches that were grabbed but not detected in client");

        group.MapPost("/{dispatchId}/retry", RetryDispatch)
            .WithName("Retry Dispatch")
            .WithDescription("Manually retry a failed grab");

        group.MapDelete("/{dispatchId}", ArchiveDispatch)
            .WithName("Archive Dispatch")
            .WithDescription("Archive/delete a dispatch (soft delete)");

        group.MapPost("/poll", PollDispatchOutcomes)
            .WithName("Poll Dispatch Outcomes")
            .WithDescription("Manually trigger dispatch lifecycle polling and timeout detection");

        var imports = endpoints.MapGroup("/api/v1/import-resolutions")
            .WithName("ImportResolutions");

        imports.MapGet(string.Empty, GetImportResolutions)
            .WithName("List Import Resolutions")
            .WithDescription("Query import outcomes for external tools");

        var alerts = endpoints.MapGroup("/api/v1/dispatch-alerts")
            .WithName("DispatchAlerts");

        alerts.MapGet(string.Empty, GetDispatchAlerts)
            .WithName("List Dispatch Alerts")
            .WithDescription("Get open alerts for dispatch failures");

        alerts.MapPost("/{alertId}/acknowledge", AcknowledgeAlert)
            .WithName("Acknowledge Alert")
            .WithDescription("Mark a dispatch alert as acknowledged");

        var metrics = endpoints.MapGroup("/api/v1/dispatch-metrics")
            .WithName("DispatchMetrics");

        metrics.MapGet(string.Empty, GetDispatchMetrics)
            .WithName("Get Dispatch Metrics")
            .WithDescription("Get operational metrics for dispatch lifecycle");

        return endpoints;
    }

    private static async Task<IResult> GetDispatches(
        IDownloadDispatchesRepository repository,
        string? grabStatus,
        string? importStatus,
        string? clientId,
        string? entityType,
        string? entityId,
        string? libraryId,
        DateTime? minGrabTime,
        DateTime? maxGrabTime,
        int pageSize = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new DispatchQueryFilter
        {
            GrabStatus = grabStatus,
            ImportStatus = importStatus,
            ClientId = clientId,
            EntityType = entityType,
            EntityId = entityId,
            LibraryId = libraryId,
            MinGrabTime = minGrabTime.HasValue ? new DateTimeOffset(minGrabTime.Value, TimeSpan.Zero) : null,
            MaxGrabTime = maxGrabTime.HasValue ? new DateTimeOffset(maxGrabTime.Value, TimeSpan.Zero) : null
        };

        var pagination = new DispatchPaginationOptions
        {
            PageSize = Math.Max(10, Math.Min(pageSize, 100)),
            PageToken = pageToken
        };

        var (items, nextPageToken) = await repository.QueryDispatchesAsync(filter, pagination, cancellationToken);

        return Results.Ok(new
        {
            dispatches = items,
            nextPageToken,
            hasMore = !string.IsNullOrEmpty(nextPageToken)
        });
    }

    private static async Task<IResult> GetDispatch(
        string dispatchId,
        IDownloadDispatchesRepository repository,
        CancellationToken cancellationToken)
    {
        var dispatch = await repository.GetDispatchAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return Results.NotFound(new { error = "Dispatch not found" });
        }

        var timeline = await repository.GetDispatchTimelineAsync(dispatchId, cancellationToken);

        return Results.Ok(new
        {
            dispatch,
            timeline
        });
    }

    private static async Task<IResult> GetUnresolvedDispatches(
        IDownloadDispatchesRepository repository,
        int minAgeMinutes = 30,
        string? clientId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var unresolved = await repository.FindUnresolvedDispatchesAsync(
            minAgeMinutes,
            clientId,
            limit,
            cancellationToken);

        return Results.Ok(new
        {
            unresolvedCount = unresolved.Count,
            dispatches = unresolved.Select(d => new
            {
                d.Id,
                d.ReleaseName,
                d.DownloadClientName,
                d.GrabStatus,
                d.GrabAttemptedUtc,
                minutesSinceGrab = d.GrabAttemptedUtc.HasValue
                    ? (int)(DateTimeOffset.UtcNow - d.GrabAttemptedUtc.Value).TotalMinutes
                    : 0,
                notes = "Grab succeeded but never appeared in client. Possible: client restarted, release name doesn't match client item, torrent was immediately removed."
            })
        });
    }

    private static async Task<IResult> RetryDispatch(
        string dispatchId,
        IDownloadDispatchesRepository repository,
        CancellationToken cancellationToken)
    {
        var dispatch = await repository.GetDispatchAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return Results.NotFound(new { error = "Dispatch not found" });
        }

        if (dispatch.GrabStatus != "failed")
        {
            return Results.BadRequest(new
            {
                code = "CANNOT_RETRY",
                message = $"Cannot retry dispatch with status '{dispatch.GrabStatus}'. Only 'failed' grabs can be retried."
            });
        }

        // TODO: Queue retry job in search retry window
        var nextRetryTime = DateTimeOffset.UtcNow.AddMinutes(30);

        var response = new
        {
            dispatchId,
            newJobId = $"job-{Guid.CreateVersion7().ToString("N")[..8]}",
            nextRetryEligibleUtc = nextRetryTime,
            message = $"Retry queued. Next eligibility window: {nextRetryTime:O}"
        };

        return Results.Json(response, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ArchiveDispatch(
        string dispatchId,
        IDownloadDispatchesRepository repository,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var dispatch = await repository.GetDispatchAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return Results.NotFound();
        }

        await repository.ArchiveDispatchAsync(dispatchId, reason ?? "manual_cleanup", cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> PollDispatchOutcomes(
        IDownloadDispatchPollingService pollingService,
        CancellationToken cancellationToken = default)
    {
        var report = await pollingService.PollAsync(cancellationToken);
        return Results.Json(report, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetImportResolutions(
        IDownloadDispatchesRepository repository,
        string? status = "imported",
        string? libraryId = null,
        string? mediaType = null,
        string? entityId = null,
        DateTime? importedAfter = null,
        DateTime? importedBefore = null,
        int pageSize = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        // Query dispatches with the specified import status
        var filter = new DispatchQueryFilter
        {
            ImportStatus = status,
            LibraryId = libraryId,
            EntityType = mediaType,
            EntityId = entityId,
            MinGrabTime = importedAfter.HasValue ? new DateTimeOffset(importedAfter.Value, TimeSpan.Zero) : null,
            MaxGrabTime = importedBefore.HasValue ? new DateTimeOffset(importedBefore.Value, TimeSpan.Zero) : null
        };

        var pagination = new DispatchPaginationOptions
        {
            PageSize = Math.Max(10, Math.Min(pageSize, 100)),
            PageToken = pageToken
        };

        var (items, nextPageToken) = await repository.QueryDispatchesAsync(filter, pagination, cancellationToken);

        var resolutions = items
            .Where(d => !string.IsNullOrEmpty(d.ImportStatus))
            .Select(d => new ImportResolutionItem(
                Id: d.Id,
                DispatchId: d.Id,
                EntityId: d.EntityId,
                MediaType: d.MediaType,
                LibraryId: d.LibraryId,
                Status: d.ImportStatus ?? "undetected",
                FilePath: d.ImportedFilePath,
                FileName: string.IsNullOrEmpty(d.ImportedFilePath)
                    ? null
                    : Path.GetFileName(d.ImportedFilePath),
                FileSize: d.DownloadedBytes,
                ImportedUtc: d.ImportCompletedUtc,
                FailureCode: d.ImportFailureCode,
                FailureMessage: d.ImportFailureMessage,
                FailedUtc: string.Equals(d.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? d.ImportCompletedUtc
                    : null
            ))
            .ToList();

        return Results.Ok(new
        {
            resolutions,
            nextPageToken,
            hasMore = !string.IsNullOrEmpty(nextPageToken)
        });
    }

    private static async Task<IResult> GetDispatchAlerts(
        [FromServices] IDispatchAlertRepository alertRepository,
        string? severity = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var alerts = await alertRepository.GetOpenAlertsAsync(severity, limit, cancellationToken);

        return Results.Ok(new
        {
            openCount = alerts.Count,
            alerts = alerts.Select(a => new
            {
                a.Id,
                a.DispatchId,
                a.Title,
                a.Summary,
                a.AlertKind,
                a.Severity,
                a.Metadata,
                a.DetectedUtc
            })
        });
    }

    private static async Task<IResult> AcknowledgeAlert(
        string alertId,
        [FromServices] IDispatchAlertRepository alertRepository,
        CancellationToken cancellationToken)
    {
        var acknowledged = await alertRepository.AcknowledgeAlertAsync(alertId, cancellationToken);

        if (!acknowledged)
        {
            return Results.NotFound(new { error = "Alert not found" });
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetDispatchMetrics(
        [FromServices] IDispatchMetricsRepository metricsRepository,
        CancellationToken cancellationToken)
    {
        var metrics = await metricsRepository.GetMetricsAsync(cancellationToken);

        return Results.Ok(new
        {
            metrics.TotalDispatchesRecorded,
            metrics.SuccessfulGrabs,
            metrics.FailedGrabs,
            metrics.DetectedDownloads,
            metrics.SuccessfulImports,
            metrics.FailedImports,
            metrics.ActiveDispatchesCount,
            metrics.RecoveryCasesOpenCount,
            metrics.AverageGrabToDetection,
            metrics.AverageDetectionToImport,
            metrics.GrabFailuresByClient,
            metrics.ImportFailuresByKind,
            metrics.ComputedUtc
        });
    }
}
