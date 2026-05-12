using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.ImportRecovery;

public static class SeriesImportRecoveryEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSeriesImportRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/import-recovery/series")
            .WithName("SeriesImportRecovery");

        group.MapGet(string.Empty, GetSeriesImportRecoverySummary)
            .WithName("Get Series Recovery Summary")
            .WithDescription("Get summary of series import recovery cases including recent failures");

        group.MapPost("/{caseId}/resolve", ResolveSeriesRecoveryCase)
            .WithName("Resolve Series Recovery Case")
            .WithDescription("Mark a series recovery case as resolved");

        group.MapPost("/{caseId}/dismiss", DismissSeriesRecoveryCase)
            .WithName("Dismiss Series Recovery Case")
            .WithDescription("Dismiss a series recovery case without resolving the underlying issue");

        group.MapPost("/{caseId}/re-search", RetriggerSeriesSearch)
            .WithName("Re-search Series Recovery Case")
            .WithDescription("Queue a new search attempt for the series associated with this recovery case");

        group.MapDelete("/{caseId}", DeleteSeriesRecoveryCase)
            .WithName("Delete Series Recovery Case")
            .WithDescription("Permanently delete a specific series recovery case");

        return endpoints;
    }

    private static async Task<IResult> GetSeriesImportRecoverySummary(
        [FromServices] ISeriesCatalogRepository catalogRepository,
        CancellationToken cancellationToken)
    {
        var summary = await catalogRepository.GetImportRecoverySummaryAsync(cancellationToken);

        return Results.Ok(new
        {
            casesByKind = new
            {
                summary.OpenCount,
                summary.QualityCount,
                summary.UnmatchedCount,
                summary.CorruptCount,
                summary.DownloadFailedCount,
                summary.ImportFailedCount
            },
            recentCases = summary.RecentCases.Select(c => new
            {
                c.Id,
                c.Title,
                c.FailureKind,
                c.Status,
                c.Summary,
                c.RecommendedAction,
                c.DetailsJson,
                c.DetectedUtc
            })
        });
    }

    private static async Task<IResult> ResolveSeriesRecoveryCase(
        string caseId,
        [FromBody] ResolveRecoveryCaseRequest? request,
        [FromServices] ISeriesCatalogRepository catalogRepository,
        CancellationToken cancellationToken)
    {
        var resolved = await catalogRepository.ResolveImportRecoveryCaseAsync(caseId, "resolved", cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(new { error = "Recovery case not found" });
        }

        await catalogRepository.AddImportRecoveryEventAsync(
            caseId,
            "resolved",
            request?.Note ?? "Manually marked as resolved.",
            null,
            cancellationToken);

        return Results.Ok(new { caseId, status = "resolved" });
    }

    private static async Task<IResult> DismissSeriesRecoveryCase(
        string caseId,
        [FromBody] ResolveRecoveryCaseRequest? request,
        [FromServices] ISeriesCatalogRepository catalogRepository,
        CancellationToken cancellationToken)
    {
        var dismissed = await catalogRepository.ResolveImportRecoveryCaseAsync(caseId, "dismissed", cancellationToken);
        if (dismissed is null)
        {
            return Results.NotFound(new { error = "Recovery case not found" });
        }

        await catalogRepository.AddImportRecoveryEventAsync(
            caseId,
            "dismissed",
            request?.Note ?? "Dismissed without action.",
            null,
            cancellationToken);

        return Results.Ok(new { caseId, status = "dismissed" });
    }

    private static async Task<IResult> RetriggerSeriesSearch(
        string caseId,
        [FromServices] ISeriesCatalogRepository catalogRepository,
        [FromServices] IJobScheduler jobScheduler,
        CancellationToken cancellationToken)
    {
        var summary = await catalogRepository.GetImportRecoverySummaryAsync(cancellationToken);
        var recoveryCase = summary.RecentCases.FirstOrDefault(c => c.Id == caseId);
        if (recoveryCase is null)
        {
            return Results.NotFound(new { error = "Recovery case not found" });
        }

        var job = await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "series.search.recovery",
                Source: "import-recovery",
                PayloadJson: JsonSerializer.Serialize(new
                {
                    caseId,
                    title = recoveryCase.Title,
                    failureKind = recoveryCase.FailureKind
                }),
                RelatedEntityType: "series",
                RelatedEntityId: null),
            cancellationToken);

        await catalogRepository.AddImportRecoveryEventAsync(
            caseId,
            "re-search-queued",
            $"Re-search job queued (jobId: {job.Id}).",
            null,
            cancellationToken);

        return Results.Accepted(null, new { caseId, jobId = job.Id, status = "queued" });
    }

    private static async Task<IResult> DeleteSeriesRecoveryCase(
        string caseId,
        [FromServices] ISeriesCatalogRepository catalogRepository,
        CancellationToken cancellationToken)
    {
        var deleted = await catalogRepository.DeleteImportRecoveryCaseAsync(caseId, cancellationToken);

        if (!deleted)
        {
            return Results.NotFound(new { error = "Recovery case not found" });
        }

        return Results.NoContent();
    }
}

public sealed record ResolveRecoveryCaseRequest(string? Note = null);
