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
                new ActivityEventPush(
                    request.Id ?? Guid.NewGuid().ToString("N"),
                    request.Message,
                    request.Category ?? "search",
                    request.Severity ?? "info",
                    timeProvider.GetUtcNow().ToString("O"),
                    request.RelatedEntityType,
                    request.RelatedEntityId,
                    request.RelatedJobId,
                    request.Title,
                    request.FilePath,
                    request.SourceName,
                    request.ReasonGroup,
                    request.DetailsJson),
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

        // Any dashboard-subject or Live-subject event with a payload the test writes (AttentionChanged,
        // ConnectivityChanged, DownloadHealthChanged, JobChanged and so on), so a screen's reaction to it
        // can be checked with no reload.
        endpoints.MapPost("/api/testing/realtime/event", async (
            HttpContext httpContext,
            [FromBody] PushEventRequest request,
            IRealtimeEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest();
            }

            object payload = (object?)request.Payload ?? new { };
            if (string.Equals(request.Subject, "live", StringComparison.OrdinalIgnoreCase))
            {
                await publisher.PublishLiveEventAsync(request.Name, payload, cancellationToken);
            }
            else
            {
                await publisher.PublishDashboardEventAsync(request.Name, payload, cancellationToken);
            }

            return Results.NoContent();
        });

        // A film, show or episode changed (#1179): the same announcement the database write raises.
        endpoints.MapPost("/api/testing/realtime/title-changed", async (
            HttpContext httpContext,
            [FromBody] PushTitleChangedRequest request,
            IRealtimeEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.Equals(request.EntityType, "Episode", StringComparison.OrdinalIgnoreCase))
            {
                await publisher.PublishEntityChangedAsync("Episode", request.Id, cancellationToken);
            }
            else
            {
                await publisher.PublishTitleChangedAsync(
                    string.Equals(request.EntityType, "Series", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie",
                    request.Id,
                    request.LibraryIds ?? [],
                    cancellationToken);
            }

            return Results.NoContent();
        });

            string? title,
        endpoints.MapGet("/api/testing/realtime/watchers", async (
            HttpContext httpContext,
            string? title,
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

public sealed record PushTitleChangedRequest(string EntityType, string Id, string[]? LibraryIds);

public sealed record PushActivityRequest(
    string Message,
    string? Id,
    string? Category,
    string? Severity,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null,
    string? RelatedJobId = null,
    string? Title = null,
    string? FilePath = null,
    string? SourceName = null,
    string? ReasonGroup = null,
    string? DetailsJson = null);

public sealed record PushLiveEventRequest(string EventName, System.Text.Json.JsonElement Payload);

public sealed record PushEventRequest(string Name, string? Subject, System.Text.Json.JsonElement? Payload);
