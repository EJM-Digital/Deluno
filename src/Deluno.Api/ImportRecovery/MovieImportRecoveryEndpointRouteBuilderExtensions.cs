using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.ImportRecovery;

public static class MovieImportRecoveryEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMovieImportRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/import-recovery/movies")
            .WithName("MovieImportRecovery");

        group.MapGet(string.Empty, GetMovieImportRecoverySummary)
            .WithName("Get Movie Recovery Summary")
            .WithDescription("Get summary of movie import recovery cases including recent failures");

        group.MapPost("/{caseId}/resolve", ResolveMovieRecoveryCase)
            .WithName("Resolve Movie Recovery Case")
            .WithDescription("Mark a movie recovery case as resolved");

        group.MapPost("/{caseId}/dismiss", DismissMovieRecoveryCase)
            .WithName("Dismiss Movie Recovery Case")
            .WithDescription("Dismiss a movie recovery case without resolving the underlying issue");

        group.MapPost("/{caseId}/re-search", RetriggerMovieSearch)
            .WithName("Re-search Movie Recovery Case")
            .WithDescription("Queue a new search attempt for the movie associated with this recovery case");

        group.MapDelete("/{caseId}", DeleteMovieRecoveryCase)
            .WithName("Delete Movie Recovery Case")
            .WithDescription("Permanently delete a specific movie recovery case");

        return endpoints;
    }

    private static async Task<IResult> GetMovieImportRecoverySummary(
        [FromServices] IMovieCatalogRepository catalogRepository,
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

    private static async Task<IResult> ResolveMovieRecoveryCase(
        string caseId,
        [FromBody] ResolveMovieRecoveryCaseRequest? request,
        [FromServices] IMovieCatalogRepository catalogRepository,
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

    private static async Task<IResult> DismissMovieRecoveryCase(
        string caseId,
        [FromBody] ResolveMovieRecoveryCaseRequest? request,
        [FromServices] IMovieCatalogRepository catalogRepository,
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

    private static async Task<IResult> RetriggerMovieSearch(
        string caseId,
        [FromServices] IMovieCatalogRepository catalogRepository,
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
                JobType: "movies.search.recovery",
                Source: "import-recovery",
                PayloadJson: JsonSerializer.Serialize(new
                {
                    caseId,
                    title = recoveryCase.Title,
                    failureKind = recoveryCase.FailureKind
                }),
                RelatedEntityType: "movie",
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

    private static async Task<IResult> DeleteMovieRecoveryCase(
        string caseId,
        [FromServices] IMovieCatalogRepository catalogRepository,
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

public sealed record ResolveMovieRecoveryCaseRequest(string? Note = null);
