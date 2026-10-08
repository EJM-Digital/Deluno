using Deluno.Contracts;
using Deluno.Realtime;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Testing;

/// <summary>
/// Lets a browser test make the server raise realtime events on demand (#1178):
/// a <c>DownloadProgress</c> and an <c>ActivityEventAdded</c> for something on
/// screen, so the test can check the screen changes with no reload, and a read of
/// how many browsers have joined each subject, so it can check the page really
/// subscribed.
///
/// <para><b>Never reachable outside a smoke run.</b> Mapped only when
/// <c>Testing:EnableRealtimePushEndpoint</c> is true, which the shipped app's
/// configuration never sets (the same guard as the large-library seed route): in a
/// real install the routes are absent from the endpoint table, not merely
/// unauthorized.</para>
/// </summary>
public static class RealtimePushTestEndpoints
{
    public static IEndpointRouteBuilder MapDelunoRealtimePushTestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/testing/realtime/download-progress", async (
            HttpContext httpContext,
            [FromBody] PushDownloadProgressRequest request,
            IRealtimeEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await publisher.PublishDownloadProgressAsync(
                request.Id, request.Title ?? request.Id, request.Progress, request.SpeedMbps, request.Eta, request.Status ?? "downloading", cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapPost("/api/testing/realtime/activity", async (
            HttpContext httpContext,
            [FromBody] PushActivityRequest request,
            IRealtimeEventPublisher publisher,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await publisher.PublishActivityEventAddedAsync(
                request.Id ?? Guid.NewGuid().ToString("N"),
                request.Message,
                request.Category ?? "search",
                request.Severity ?? "info",
                timeProvider.GetUtcNow().ToString("O"),
                cancellationToken);
            return Results.NoContent();
        });

        // A Live event that carries its own data (JourneyStageChanged, ProcessorHandoffChanged,
        // RecoveryCaseChanged, JobChanged, ClientStateChanged), pushed as the server pushes it.
        endpoints.MapPost("/api/testing/realtime/live-event", async (
            HttpContext httpContext,
            [FromBody] PushLiveEventRequest request,
            IRealtimeEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await publisher.PublishLiveEventAsync(request.EventName, request.Payload, cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapGet("/api/testing/realtime/watchers", async (
            HttpContext httpContext,
            IRealtimeWatchers watchers,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Ok(new
            {
                connections = watchers.ConnectionCount,
                dashboard = watchers.Count(RealtimeGroups.Dashboard),
                queue = watchers.Count(RealtimeGroups.Queue),
                activity = watchers.Count(RealtimeGroups.Activity),
                live = watchers.Count(RealtimeGroups.Live)
            });
        });

        return endpoints;
    }
}

public sealed record PushDownloadProgressRequest(string Id, string? Title, double Progress, double SpeedMbps, string? Eta, string? Status);

public sealed record PushActivityRequest(string Message, string? Id, string? Category, string? Severity);

public sealed record PushLiveEventRequest(string EventName, System.Text.Json.JsonElement Payload);
