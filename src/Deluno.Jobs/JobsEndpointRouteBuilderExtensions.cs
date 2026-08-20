using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Jobs;

public static class JobsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoJobsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/jobs", async (
            int? pageSize,
            string? pageToken,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await repository.ListPageAsync(new PageRequest(pageSize ?? 25, pageToken), cancellationToken));
        });

        endpoints.MapPost("/api/jobs/retry-failed", async (
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var retried = await repository.RetryFailedAsync(cancellationToken);
            return Results.Ok(new { retried });
        });

        endpoints.MapGet("/api/activity", async (
            int? pageSize,
            string? pageToken,
            string? relatedEntityType,
            string? relatedEntityId,
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListActivityPageAsync(
                new PageRequest(pageSize ?? 50, pageToken),
                relatedEntityType,
                relatedEntityId,
                cancellationToken);
            return Results.Ok(page);
        });

        endpoints.MapGet("/api/decisions", async (
            int? pageSize,
            string? pageToken,
            string? relatedEntityType,
            string? relatedEntityId,
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListActivityPageAsync(
                new PageRequest(pageSize ?? 100, pageToken),
                relatedEntityType,
                relatedEntityId,
                cancellationToken);
            return Results.Ok(Page<DecisionExplanationItem>.Of(page.Items
                .Select(DecisionExplanationActivity.FromActivity)
                .OfType<DecisionExplanationItem>()
                .ToArray(), page.NextPageToken));
        });

        endpoints.MapGet("/api/library-automation", async (
            int? pageSize,
            string? pageToken,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await repository.ListLibraryAutomationStatesPageAsync(
                new PageRequest(pageSize ?? 50, pageToken), cancellationToken));
        });

        endpoints.MapGet("/api/search-cycles", async (
            int? pageSize,
            string? pageToken,
            string? libraryId,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListSearchCycleRunsPageAsync(
                new PageRequest(pageSize ?? 20, pageToken),
                libraryId,
                cancellationToken);
            return Results.Ok(page);
        });

        endpoints.MapGet("/api/search-retry-windows", async (
            int? pageSize,
            string? pageToken,
            string? libraryId,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListSearchRetryWindowsPageAsync(
                new PageRequest(pageSize ?? 20, pageToken),
                libraryId,
                cancellationToken);
            return Results.Ok(page);
        });

        endpoints.MapGet("/api/download-dispatches", async (
            int? pageSize,
            string? pageToken,
            string? mediaType,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListDownloadDispatchesPageAsync(
                new PageRequest(pageSize ?? 20, pageToken),
                mediaType,
                cancellationToken);
            return Results.Ok(page);
        });

        return endpoints;
    }
}
