using Deluno.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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

        // "Check now" (#1217): every check runs again within seconds, and the rows that no longer hold clear
        // through the usual health push.
        write.MapPost("/run", async (
            HttpContext httpContext,
            IHealthCheckRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            foreach (var category in HealthCheckCategories.AllChecked)
            {
                await repository.RequestImmediateRunAsync(category, cancellationToken);
            }

            return Results.Accepted();
        });

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

        // A one-time note ("notice:...") is cleared by hand: no check re-creates it (#1073).
        write.MapDelete("/notices/{id}", async (
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

            if (!id.StartsWith("notice:", StringComparison.OrdinalIgnoreCase) || await repository.GetAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            await repository.RemoveAsync(id, cancellationToken);
            return Results.NoContent();
        });

        // A start-up step that did not finish (#1080): run it again now, or clear the row. Only "startup:"
        // problems: a job that keeps failing shows live and goes away when it works again.
        write.MapPost("/background-problems/{key}/retry", async (
            string key,
            HttpContext httpContext,
            [FromServices] IEnumerable<IBackgroundProblemRetry> retries,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var retry = retries.FirstOrDefault(item => string.Equals(item.ProblemKey, key, StringComparison.Ordinal));
            if (retry is null)
            {
                return Results.NotFound();
            }

            var cleared = await retry.RetryAsync(cancellationToken);
            return Results.Ok(new BackgroundProblemRetryResponse(
                cleared,
                cleared ? "It worked, so the note is gone." : "It still did not work. Deluno's logs say why; try again after fixing that."));
        });

        write.MapDelete("/background-problems/{key}", async (
            string key,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!key.StartsWith("startup:", StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            httpContext.RequestServices.GetService<IBackgroundProblemReporter>()?.Clear(key);
            return Results.NoContent();
        });

        return endpoints;
    }
}

public sealed record BackgroundProblemRetryResponse(bool Cleared, string Message);

public sealed record SnoozeHealthCheckRequest(int? Hours);
