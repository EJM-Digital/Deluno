using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.BlockedReleases;

/// <summary>
/// What Deluno does when an import fails, and the user's right to disagree.
///
/// <para>DESIGN-007, James on all sixteen decisions at once: <i>"I think all
/// these things we decided need to have configuration toggles to set them on
/// and off in a management / blocklist console."</i> The right harshness
/// depends on the library — somebody on a fast line with spare disk wants it
/// strict; somebody on a flaky share does not.</para>
///
/// <para>The list is generated from <see cref="ImportFailurePolicy.KnownReasons"/>
/// rather than from the stored rows, so a failure kind added to the import
/// pipeline is configurable the day it is added rather than the first time it
/// happens to somebody.</para>
/// </summary>
public static class ImportFailureRulesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoImportFailureRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/failure-rules", async (
            HttpContext httpContext,
            [FromServices] IImportFailureRuleRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await repository.ListAsync(cancellationToken));
        });

        endpoints.MapPut("/api/failure-rules/{reasonCode}", async (
            string reasonCode,
            SetImportFailureRuleRequest request,
            HttpContext httpContext,
            [FromServices] IImportFailureRuleRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // A rule for a reason the pipeline cannot produce is a typo that
            // would sit in the table looking like a setting.
            if (!ImportFailurePolicy.KnownReasons.Contains(reasonCode, StringComparer.Ordinal))
            {
                return Results.NotFound();
            }

            await repository.SetAsync(reasonCode, request.Decision, cancellationToken);
            return Results.NoContent();
        });

        // Back to Deluno's answer. Implemented as forgetting the opinion rather
        // than as writing today's default down, so a later change to the
        // shipped table still reaches anybody who has pressed this.
        endpoints.MapDelete("/api/failure-rules/{reasonCode}", async (
            string reasonCode,
            HttpContext httpContext,
            [FromServices] IImportFailureRuleRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await repository.ResetAsync(reasonCode, cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }
}
