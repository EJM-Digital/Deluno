using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Notifications;

/// <summary>
/// /api/notification-webhooks, /api/notifications and
/// /api/notification-preferences. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies are
/// unchanged apart from the repository type and explicit [FromServices].
/// </summary>
public static class NotificationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var notificationWebhooks = endpoints.MapGroup("/api/notification-webhooks");

        notificationWebhooks.MapGet(string.Empty, async (
            [FromServices] INotificationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListNotificationWebhooksAsync(cancellationToken);
            return Results.Ok(items);
        });

        notificationWebhooks.MapGet("/deliveries", async (
            HttpContext httpContext,
            string? status,
            string? webhookId,
            int? take,
            [FromServices] INotificationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!string.IsNullOrWhiteSpace(status) &&
                status.Trim().ToLowerInvariant() is not (
                    NotificationWebhookDeliveryStatuses.Pending or
                    NotificationWebhookDeliveryStatuses.Retrying or
                    NotificationWebhookDeliveryStatuses.Delivered or
                    NotificationWebhookDeliveryStatuses.DeadLetter))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Status must be pending, retrying, delivered, or dead-letter."]
                });
            }

            var items = await repository.ListNotificationWebhookDeliveriesAsync(
                status,
                webhookId,
                Math.Clamp(take ?? 100, 1, 500),
                cancellationToken);
            return Results.Ok(items);
        });

        notificationWebhooks.MapPost("/deliveries/{deliveryId}/replay", async (
            string deliveryId,
            HttpContext httpContext,
            [FromServices] IOutboundNotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await notificationService.ReplayAsync(deliveryId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        notificationWebhooks.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateNotificationWebhookRequest request,
            [FromServices] INotificationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["url"] = ["Webhook URL is required."]
                });
            }

            var item = await repository.CreateNotificationWebhookAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        notificationWebhooks.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateNotificationWebhookRequest request,
            [FromServices] INotificationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateNotificationWebhookAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        notificationWebhooks.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] INotificationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteNotificationWebhookAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        notificationWebhooks.MapPost("{id}/test", async (
            string id,
            HttpContext httpContext,
            [FromServices] INotificationRepository repository,
            [FromServices] IOutboundNotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!await repository.AreOutboundNotificationsEnabledAsync(cancellationToken))
            {
                return Results.Ok(new { sent = false, message = "Notifications are paused. Turn on Send notifications to test this webhook." });
            }

            var result = await notificationService.DispatchToWebhookAsync(
                id,
                "test",
                "Deluno Webhook Test",
                "This is a test notification from Deluno. Your webhook is configured correctly.",
                null,
                cancellationToken);

            return result is null
                ? Results.NotFound()
                : Results.Ok(result);
        });

        // Notification endpoints
        var notifications = endpoints.MapGroup("/api/notifications");

        notifications.MapGet(string.Empty, async (
            HttpContext httpContext,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var items = await notificationService.GetNotificationsAsync(50, 0, cancellationToken);
            return Results.Ok(items);
        });

        notifications.MapGet("/unread-count", async (
            HttpContext httpContext,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var count = await notificationService.GetUnreadCountAsync(cancellationToken);
            return Results.Ok(new { unreadCount = count });
        });

        notifications.MapPost("/{notificationId}/read", async (
            HttpContext httpContext,
            string notificationId,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await notificationService.MarkAsReadAsync(notificationId, cancellationToken);
            return Results.Ok();
        });

        notifications.MapPost("/read-all", async (
            HttpContext httpContext,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await notificationService.MarkAllAsReadAsync(cancellationToken);
            return Results.Ok();
        });

        notifications.MapDelete("/{notificationId}", async (
            HttpContext httpContext,
            string notificationId,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await notificationService.DeleteNotificationAsync(notificationId, cancellationToken);
            return Results.Ok();
        });

        notifications.MapDelete(string.Empty, async (
            HttpContext httpContext,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await notificationService.ClearAllNotificationsAsync(cancellationToken);
            return Results.Ok();
        });

        // Notification preferences endpoints
        var preferences = endpoints.MapGroup("/api/notification-preferences");

        preferences.MapGet(string.Empty, async (
            HttpContext httpContext,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var prefs = await notificationService.GetPreferencesAsync(cancellationToken);
            return Results.Ok(prefs);
        });

        preferences.MapPut(string.Empty, async (
            HttpContext httpContext,
            NotificationPreferences request,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            await notificationService.UpdatePreferencesAsync(request, cancellationToken);
            return Results.Ok();
        });

        return endpoints;
    }
}
