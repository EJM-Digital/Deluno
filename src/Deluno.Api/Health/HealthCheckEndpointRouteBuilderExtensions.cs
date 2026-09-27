using Deluno.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Health;

/// <summary>
/// The one endpoint every health surface reads from (#909): System &#8594;
/// Health's full list, the Dashboard tile, and the bell all call
/// <c>GET /api/system/health</c> and see the same rows, because there is
/// nowhere else for them to come from.
/// </summary>
public static class HealthCheckEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoHealthCheckEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/system/health")
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        group.MapGet(string.Empty, async (
            IHealthCheckRepository repository,
            CancellationToken cancellationToken) =>
        {
            var checks = await repository.ListAsync(cancellationToken);
            var summary = await repository.GetSummaryAsync(cancellationToken);
            return Results.Ok(new HealthCheckListResponse(checks, summary));
        });

        group.MapGet("/summary", async (
            IHealthCheckRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetSummaryAsync(cancellationToken)));

        var write = group.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        write.MapPost("/{id}/snooze", async (
            string id,
            [FromBody] SnoozeHealthCheckRequest? request,
            HttpContext httpContext,
            IHealthCheckRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // A snooze always names its own end -- an unbounded one is a
            // problem that stopped existing on screen while staying real, and
            // "snoozed forever" is not a state Health is allowed to show.
            var hours = request?.Hours is > 0 ? Math.Clamp(request.Hours.Value, 1, 24 * 30) : 24;
            var until = timeProvider.GetUtcNow().AddHours(hours);
            var updated = await repository.SnoozeAsync(id, until, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        write.MapPost("/{id}/unsnooze", async (
            string id,
            HttpContext httpContext,
            IHealthCheckRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var updated = await repository.SnoozeAsync(id, null, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        return endpoints;
    }
}

public sealed record SnoozeHealthCheckRequest(int? Hours);
