using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

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

        group.MapDelete("/{caseId}", DeleteSeriesRecoveryCase)
            .WithName("Delete Series Recovery Case")
            .WithDescription("Dismiss/delete a specific series recovery case");

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
                c.Summary,
                c.RecommendedAction,
                c.DetailsJson,
                c.DetectedUtc
            })
        });
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
