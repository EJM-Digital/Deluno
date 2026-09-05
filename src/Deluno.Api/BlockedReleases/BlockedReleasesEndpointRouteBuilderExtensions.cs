using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.BlockedReleases;

/// <summary>
/// The list of releases Deluno will not use again, and the way to change its
/// mind.
///
/// <para>Decisions 1 and 2 of DESIGN-007 chose permanent refusals — which is
/// only safe because they are visible and reversible. These two routes are
/// that safety, and the Failure and blocklist console is built on them.</para>
/// </summary>
public static class BlockedReleasesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoBlockedReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/blocked-releases", async (
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.ListAsync(cancellationToken));
        });

        endpoints.MapDelete("/api/blocked-releases/{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Unblocking starts nothing. James: "there could be a number of
            // scenarios when the blocklist is being cleared and if an
            // individual title is removed the user is going to manually
            // trigger the search anyway" — so clearing in bulk cannot become a
            // storm of searches. DESIGN-007 decision 8.
            return await repository.UnblockAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // Answering a proposal. The other answer is the DELETE above — allowing
        // a proposed release is the same act as un-refusing a refused one, so
        // it is deliberately the same route rather than a third verb that does
        // the same thing.
        endpoints.MapPost("/api/blocked-releases/{id}/refuse", async (
            string id,
            HttpContext httpContext,
            [FromServices] IBlockedReleaseRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return await repository.RefuseAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // The manual half of the scheduled clear-out — for a refusal that
        // predates the setting, or one whose client was off when the schedule
        // came round. It calls the same service the schedule calls, so it
        // still will not overrule the sharing rule.
        endpoints.MapPost("/api/blocked-releases/{id}/cleanup", async (
            string id,
            HttpContext httpContext,
            [FromServices] IRefusedDownloadCleanupService cleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var outcome = await cleanup.CleanUpOneAsync(id, cancellationToken);
            return outcome == RefusedDownloadCleanupOutcomes.NotFound
                ? Results.NotFound()
                : Results.Ok(new { outcome });
        });

        return endpoints;
    }
}
