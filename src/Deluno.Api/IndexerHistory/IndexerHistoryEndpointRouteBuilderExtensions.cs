using Deluno.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.IndexerHistory;

/// <summary>
/// #754: the combined Indexer History screen -- every RSS sync, search,
/// grab, and auth failure across every indexer, newest first, with a Clear
/// action that hides the combined view without touching the per-movie or
/// per-series history it is built from.
/// </summary>
public static class IndexerHistoryEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoIndexerHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/indexer-history", async (
            int? pageSize,
            string? pageToken,
            string? eventType,
            string? indexerName,
            IIndexerHistoryService service,
            CancellationToken cancellationToken) =>
        {
            var page = await service.ListAsync(
                new PageRequest(pageSize ?? 50, pageToken),
                string.IsNullOrWhiteSpace(eventType) ? null : eventType,
                string.IsNullOrWhiteSpace(indexerName) ? null : indexerName,
                cancellationToken);
            return Results.Ok(page);
        });

        endpoints.MapPost("/api/indexer-history/clear", async (
            HttpContext httpContext,
            IIndexerHistoryService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await service.ClearAsync(cancellationToken);
            return Results.Ok(new { cleared = true });
        });

        return endpoints;
    }
}
