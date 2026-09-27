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
        endpoints.MapGet("/api/system/tasks", async (
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var states = (await repository.ListSystemTaskStatesAsync(cancellationToken))
                .ToDictionary(item => item.ScheduleKey, StringComparer.OrdinalIgnoreCase);

            var items = SystemTasks.All.Select(task =>
            {
                states.TryGetValue(task.Key, out var state);
                return new
                {
                    key = task.Key,
                    name = task.Name,
                    description = task.Description,
                    // What it actually runs at, which for a configurable pass is
                    // the user's answer rather than the declared default. Falls
                    // back to the declaration until it has run once.
                    intervalSeconds = state?.IntervalSeconds ?? (long)task.Interval.TotalSeconds,
                    isConfigurable = task.IsConfigurable,
                    lastStartedUtc = state?.LastStartedUtc,
                    lastCompletedUtc = state?.LastCompletedUtc,
                    lastResult = state?.LastResult ?? "not-run",
                    lastDurationMs = state?.LastDurationMs,
                    nextRunUtc = state?.NextRunUtc
                };
            }).ToArray();

            return Results.Ok(items);
        });

        endpoints.MapGet("/api/jobs", async (
            int? pageSize,
            string? pageToken,
            bool? failed,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await repository.ListPageAsync(new PageRequest(pageSize ?? 25, pageToken), failed == true, cancellationToken));
        });

        endpoints.MapPost("/api/jobs/{id}/dismiss", async (
            string id,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var dismissed = await repository.DismissFailedAsync(id, cancellationToken);
            return dismissed is null ? Results.NotFound() : Results.Ok(dismissed);
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

        endpoints.MapGet("/api/activity/display-settings", (
            IActivityDisplaySettingsStore store) =>
        {
            return Results.Ok(new { pageSize = store.PageSize });
        });

        endpoints.MapPut("/api/activity/display-settings", async (
            ActivityDisplaySettingsRequest request,
            IActivityDisplaySettingsStore store,
            CancellationToken cancellationToken) =>
        {
            if (request.PageSize < FileActivityDisplaySettingsStore.MinPageSize ||
                request.PageSize > FileActivityDisplaySettingsStore.MaxPageSize)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pageSize"] = [$"Page size must be between {FileActivityDisplaySettingsStore.MinPageSize} and {FileActivityDisplaySettingsStore.MaxPageSize}."]
                });
            }

            await store.SetPageSizeAsync(request.PageSize, cancellationToken);
            return Results.Ok(new { pageSize = store.PageSize });
        });

        // #871: wipes the stored Activity feed. The same table backs decision
        // explanations, so this also clears that automation-decision history --
        // there is nothing else it lives in to spare. The one row left behind
        // records that the clear happened, so the timeline is never silently
        // empty with no explanation.
        endpoints.MapPost("/api/activity/clear", async (
            IActivityFeedRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.ClearActivityAsync(cancellationToken);
            var note = await repository.RecordActivityAsync(
                "activity.cleared",
                $"Activity history was cleared ({removed} {(removed == 1 ? "entry" : "entries")} removed).",
                null,
                null,
                null,
                null,
                cancellationToken);
            return Results.Ok(new { removed, note });
        });

        endpoints.MapGet("/api/stats", async (
            string? window,
            IActivityFeedRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var hours = NormalizeStatsWindowHours(window);
            var sinceUtc = timeProvider.GetUtcNow().AddHours(-hours);
            var stats = await repository.GetActivityStatsAsync(sinceUtc, hours, cancellationToken);
            return Results.Ok(stats);
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
            bool? includeArchived,
            string? entityId,
            IJobQueueRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListDownloadDispatchesPageAsync(
                new PageRequest(pageSize ?? 20, pageToken),
                mediaType,
                cancellationToken,
                includeArchived ?? false,
                entityId);
            return Results.Ok(page);
        });

        return endpoints;
    }

    /// <summary>
    /// "24h"/"7d"/"30d" (#827), in the words the query string uses rather than
    /// a raw hour count a caller would have to work out themselves. Anything
    /// else, including a missing window, is the safe default.
    /// </summary>
    private static int NormalizeStatsWindowHours(string? window) => window?.Trim().ToLowerInvariant() switch
    {
        "7d" => 24 * 7,
        "30d" => 24 * 30,
        _ => 24
    };
}
