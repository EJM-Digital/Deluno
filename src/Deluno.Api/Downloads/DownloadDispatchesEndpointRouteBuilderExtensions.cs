using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Contracts;
using Deluno.Libraries.Data;
using System.Text.Json;
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

        group.MapDelete("/{dispatchId}", ArchiveDispatch)
            .WithName("Archive Dispatch")
            .WithDescription("Archive/delete a dispatch (soft delete)");

        var imports = endpoints.MapGroup("/api/v1/import-resolutions")
            .WithName("ImportResolutions");

        imports.MapGet(string.Empty, GetImportResolutions)
            .WithName("List Import Resolutions")
            .WithDescription("Query import outcomes for external tools");

        return endpoints;
    }

    private static async Task<IResult> GetDispatches(
        IDownloadDispatchesRepository repository,
        string? grabStatus,
        string? importStatus,
        string? clientId,
        string? mediaType,
        string? entityType,
        string? entityId,
        string? libraryId,
        DateTime? minGrabTime,
        DateTime? maxGrabTime,
        bool includeArchived = false,
        int pageSize = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new DispatchQueryFilter
        {
            GrabStatus = grabStatus,
            ImportStatus = importStatus,
            ClientId = clientId,
            MediaType = mediaType,
            EntityType = entityType,
            EntityId = entityId,
            LibraryId = libraryId,
            MinGrabTime = minGrabTime.HasValue ? new DateTimeOffset(minGrabTime.Value, TimeSpan.Zero) : null,
            MaxGrabTime = maxGrabTime.HasValue ? new DateTimeOffset(maxGrabTime.Value, TimeSpan.Zero) : null,
            IncludeArchived = includeArchived
        };

        var pagination = new DispatchPaginationOptions
        {
            PageSize = new PageRequest(pageSize, pageToken).BoundedPageSize,
            PageToken = pageToken
        };

        var (items, nextPageToken) = await repository.QueryDispatchesAsync(filter, pagination, cancellationToken);

        return Results.Ok(new
        {
            items,
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

    private static async Task<IResult> ArchiveDispatch(
        string dispatchId,
        IDownloadDispatchesRepository repository,
        [FromServices] IDispatchProblemLog problemLog,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var dispatch = await repository.GetDispatchAsync(dispatchId, cancellationToken);
        if (dispatch is null)
        {
            return Results.NotFound();
        }

        await repository.ArchiveDispatchAsync(dispatchId, reason ?? "manual_cleanup", cancellationToken);

        // The problems recorded about this download settle with it. No screen
        // lists them, so left open they are a count nothing can bring down (#537).
        await problemLog.AcknowledgeForDispatchAsync(dispatchId, cancellationToken);
        return Results.NoContent();
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
            MediaType = mediaType,
            EntityId = entityId,
            MinImportTime = importedAfter.HasValue ? new DateTimeOffset(importedAfter.Value, TimeSpan.Zero) : null,
            MaxImportTime = importedBefore.HasValue ? new DateTimeOffset(importedBefore.Value, TimeSpan.Zero) : null
        };

        var pagination = new DispatchPaginationOptions
        {
            PageSize = new PageRequest(pageSize, pageToken).BoundedPageSize,
            PageToken = pageToken
        };

        var (items, nextPageToken) = await repository.QueryDispatchesAsync(filter, pagination, cancellationToken);

        var resolutionItems = items
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
                    : MediaPath.FileName(d.ImportedFilePath),
                FileSize: d.DownloadedBytes,
                ImportedUtc: d.ImportCompletedUtc,
                FailureCode: d.ImportFailureCode,
                FailureMessage: d.ImportFailureMessage,
                FailedUtc: string.Equals(d.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? d.ImportCompletedUtc
                    : null,
                Failure: d.Failure
            ))
            .ToList();

        return Results.Ok(new
        {
            items = resolutionItems,
            nextPageToken,
            hasMore = !string.IsNullOrEmpty(nextPageToken)
        });
    }

}
