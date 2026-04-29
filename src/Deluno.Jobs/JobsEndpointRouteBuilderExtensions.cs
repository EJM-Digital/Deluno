using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Jobs;

public static class JobsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoJobsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/jobs", async (
            int? take,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(Math.Clamp(take ?? 25, 1, 100), cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapPost("/api/jobs/retry-failed", async (
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var retried = await repository.RetryFailedAsync(cancellationToken);
            return Results.Ok(new { retried });
        });

        endpoints.MapGet("/api/activity", async (
            int? take,
            string? relatedEntityType,
            string? relatedEntityId,
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListActivityAsync(
                Math.Clamp(take ?? 50, 1, 200),
                relatedEntityType,
                relatedEntityId,
                cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapGet("/api/decisions", async (
            int? take,
            string? relatedEntityType,
            string? relatedEntityId,
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListActivityAsync(
                Math.Clamp(take ?? 100, 1, 500),
                relatedEntityType,
                relatedEntityId,
                cancellationToken);
            return Results.Ok(items
                .Select(DecisionExplanationActivity.FromActivity)
                .OfType<DecisionExplanationItem>()
                .ToArray());
        });

        endpoints.MapGet("/api/library-automation", async (
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListLibraryAutomationStatesAsync(cancellationToken);
            return Results.Ok(items.Values.OrderBy(item => item.MediaType).ThenBy(item => item.LibraryName));
        });

        endpoints.MapGet("/api/search-cycles", async (
            int? take,
            string? libraryId,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchCycleRunsAsync(
                Math.Clamp(take ?? 20, 1, 100),
                libraryId,
                cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapGet("/api/search-retry-windows", async (
            int? take,
            string? libraryId,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchRetryWindowsAsync(
                Math.Clamp(take ?? 20, 1, 100),
                libraryId,
                cancellationToken);
            return Results.Ok(items);
        });

        endpoints.MapGet("/api/download-dispatches", async (
            int? take,
            string? mediaType,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadDispatchesAsync(
                Math.Clamp(take ?? 20, 1, 100),
                mediaType,
                cancellationToken);
            return Results.Ok(items);
        });

        return endpoints;
    }
}
