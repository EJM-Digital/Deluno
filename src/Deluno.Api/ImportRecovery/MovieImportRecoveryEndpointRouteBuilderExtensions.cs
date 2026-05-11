using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

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

        group.MapDelete("/{caseId}", DeleteMovieRecoveryCase)
            .WithName("Delete Movie Recovery Case")
            .WithDescription("Dismiss/delete a specific movie recovery case");

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
                c.Summary,
                c.RecommendedAction,
                c.DetailsJson,
                c.DetectedUtc
            })
        });
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
