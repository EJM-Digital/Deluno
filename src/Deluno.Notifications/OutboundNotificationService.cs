using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Notifications;

public sealed class OutboundNotificationService(
    INotificationRepository repository,
    IHttpClientFactory httpClientFactory,
    ILogger<OutboundNotificationService> logger) : IOutboundNotificationService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public async Task DispatchAsync(
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NotificationWebhookItem> webhooks;
        try
        {
            // The master switch gates every delivery, tests included — the
            // settings page promises exactly that (#253).
            if (!await repository.AreOutboundNotificationsEnabledAsync(cancellationToken))
            {
                logger.LogDebug("Outbound notifications are paused; skipping event {Category}.", eventCategory);
                return;
            }

            webhooks = await repository.ListNotificationWebhooksAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load notification webhooks for event {Category}", eventCategory);
            return;
        }

        foreach (var webhook in webhooks)
        {
            if (!webhook.IsEnabled || !IsMatchingEvent(webhook.EventFilters, eventCategory))
            {
                continue;
            }

            await DeliverAsync(
                webhook.Id,
                webhook.Url,
                eventCategory,
                title,
                message,
                detailsJson,
                existingDelivery: null,
                resetAttempts: false,
                cancellationToken);
        }
    }

    public async Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(
        string webhookId,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        if (!await repository.AreOutboundNotificationsEnabledAsync(cancellationToken))
        {
            var failure = IntegrationFailureFactory.FromLegacy(
                "notification",
                webhookId,
                "Outbound notifications",
                "dispatch",
                "paused",
                "Outbound notifications are paused.");
            return new NotificationWebhookDeliveryResult(
                Sent: false,
                DeliveryId: null,
                Status: "paused",
                Attempts: 0,
                Error: failure.Message,
                Failure: failure);
        }

        var webhook = (await repository.ListNotificationWebhooksAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, webhookId, StringComparison.OrdinalIgnoreCase));
        if (webhook is null)
        {
            return null;
        }

        if (!webhook.IsEnabled)
        {
            var failure = IntegrationFailureFactory.FromLegacy(
                "notification-webhook",
                webhook.Id,
                webhook.Name,
                "dispatch",
                "disabled",
                "This webhook is disabled.");
            return new NotificationWebhookDeliveryResult(
                Sent: false,
                DeliveryId: null,
                Status: "disabled",
                Attempts: 0,
                Error: failure.Message,
                Failure: failure);
        }

        return await DeliverAsync(
            webhook.Id,
            webhook.Url,
            eventCategory,
            title,
            message,
            detailsJson,
            existingDelivery: null,
            resetAttempts: false,
            cancellationToken);
    }

    public async Task<NotificationWebhookDeliveryResult?> ReplayAsync(
        string deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await repository.GetNotificationWebhookDeliveryAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(delivery.WebhookUrl))
        {
            const string error = "The webhook no longer exists, so this delivery cannot be replayed.";
            var failure = IntegrationFailureFactory.FromLegacy(
                "notification-webhook",
                delivery.Item.WebhookId,
                "Notification webhook",
                "replay",
                "configuration",
                error,
                externalId: delivery.Item.Id);
            await repository.RecordNotificationWebhookDeliveryAttemptAsync(
                delivery.Item.Id,
                NotificationWebhookDeliveryStatuses.DeadLetter,
                delivery.Item.AttemptCount,
                delivery.Item.LastStatusCode,
                error,
                nextAttemptUtc: null,
                cancellationToken,
                failure);
            return new NotificationWebhookDeliveryResult(
                Sent: false,
                DeliveryId: delivery.Item.Id,
                Status: NotificationWebhookDeliveryStatuses.DeadLetter,
                Attempts: delivery.Item.AttemptCount,
                Error: error,
                Failure: failure);
        }

        // A replay is an explicit new delivery attempt for the same event. It
        // resets the bounded automatic retry budget while retaining the row,
        // identifier, and original payload for an auditable trace.
        await repository.RecordNotificationWebhookDeliveryAttemptAsync(
            delivery.Item.Id,
            NotificationWebhookDeliveryStatuses.Pending,
            attemptCount: 0,
            statusCode: null,
            error: null,
            nextAttemptUtc: null,
            cancellationToken);

        var reset = await repository.GetNotificationWebhookDeliveryAsync(delivery.Item.Id, cancellationToken) ?? delivery;
        return await DeliverAsync(
            reset.Item.WebhookId,
            delivery.WebhookUrl,
            reset.Item.EventCategory,
            reset.Item.Title,
            reset.Message,
            reset.DetailsJson,
            reset,
            resetAttempts: false,
            cancellationToken);
    }

    private async Task<NotificationWebhookDeliveryResult> DeliverAsync(
        string webhookId,
        string url,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        NotificationWebhookDeliveryRecord? existingDelivery,
        bool resetAttempts,
        CancellationToken cancellationToken)
    {
        NotificationWebhookDeliveryRecord? delivery = existingDelivery;
        try
        {
            if (delivery is null)
            {
                delivery = await repository.CreateNotificationWebhookDeliveryAsync(
                    webhookId,
                    eventCategory,
                    title,
                    message,
                    detailsJson,
                    cancellationToken);
            }

            if (resetAttempts)
            {
                await repository.RecordNotificationWebhookDeliveryAttemptAsync(
                    delivery.Item.Id,
                    NotificationWebhookDeliveryStatuses.Pending,
                    attemptCount: 0,
                    statusCode: null,
                    error: null,
                    nextAttemptUtc: null,
                    cancellationToken);
            }

            var attempts = await SendWithRetryAsync(delivery, url, cancellationToken);
            await repository.RecordNotificationWebhookFiredAsync(webhookId, error: null, cancellationToken);
            return new NotificationWebhookDeliveryResult(
                Sent: true,
                DeliveryId: delivery.Item.Id,
                Status: NotificationWebhookDeliveryStatuses.Delivered,
                Attempts: attempts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var current = delivery is null
                ? null
                : await repository.GetNotificationWebhookDeliveryAsync(delivery.Item.Id, CancellationToken.None);
            var attempts = current?.Item.AttemptCount ?? delivery?.Item.AttemptCount ?? 0;
            var error = ex.Message;
            var failure = current?.Item.Failure ?? IntegrationFailureFactory.FromException(
                "notification-webhook",
                webhookId,
                "Notification webhook",
                "deliver",
                ex,
                attempts: attempts,
                externalId: delivery?.Item.Id);
            logger.LogWarning(
                ex,
                "Webhook {WebhookId} ({Url}) failed for event {Category}; it is available for replay.",
                webhookId,
                url,
                eventCategory);

            try
            {
                await repository.RecordNotificationWebhookFiredAsync(webhookId, error, CancellationToken.None);
            }
            catch (Exception recordException)
            {
                logger.LogWarning(recordException, "Failed to record webhook fire result for {Id}", webhookId);
            }

            return new NotificationWebhookDeliveryResult(
                Sent: false,
                DeliveryId: current?.Item.Id ?? delivery?.Item.Id,
                Status: current?.Item.Status ?? NotificationWebhookDeliveryStatuses.DeadLetter,
                Attempts: attempts,
                Error: error,
                Failure: failure);
        }
    }

    private async Task<int> SendWithRetryAsync(
        NotificationWebhookDeliveryRecord delivery,
        string url,
        CancellationToken cancellationToken)
    {
        var attempts = delivery.Item.AttemptCount;
        Exception? lastError = null;

        for (var retry = 0; retry < RetryDelays.Length; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (RetryDelays[retry] > TimeSpan.Zero)
            {
                await Task.Delay(RetryDelays[retry], cancellationToken);
            }

            attempts++;
            try
            {
                var statusCode = await SendAsync(
                    url,
                    delivery.Item.EventCategory,
                    delivery.Item.Title,
                    delivery.Message,
                    delivery.DetailsJson,
                    cancellationToken);
                await repository.RecordNotificationWebhookDeliveryAttemptAsync(
                    delivery.Item.Id,
                    NotificationWebhookDeliveryStatuses.Delivered,
                    attempts,
                    statusCode,
                    error: null,
                    nextAttemptUtc: null,
                    cancellationToken);
                return attempts;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                var hasAnotherAttempt = retry + 1 < RetryDelays.Length;
                var nextAttempt = hasAnotherAttempt ? DateTimeOffset.UtcNow.Add(RetryDelays[retry + 1]) : (DateTimeOffset?)null;
                var failure = IntegrationFailureFactory.FromException(
                    "notification-webhook",
                    delivery.Item.WebhookId,
                    "Notification webhook",
                    "deliver",
                    ex,
                    retryScheduled: hasAnotherAttempt,
                    retryAfterUtc: nextAttempt,
                    attempts: attempts,
                    externalId: delivery.Item.Id);
                if (!hasAnotherAttempt)
                {
                    // Dead-letter means the bounded automatic budget is over.
                    // The transport problem may be transient, but Deluno is no
                    // longer scheduled to do anything: the safe next step is
                    // an explicit replay after the endpoint is repaired.
                    failure = failure with
                    {
                        RetryState = IntegrationRetryState.ManualAction,
                        RetryAfterUtc = null
                    };
                }
                await repository.RecordNotificationWebhookDeliveryAttemptAsync(
                    delivery.Item.Id,
                    hasAnotherAttempt
                        ? NotificationWebhookDeliveryStatuses.Retrying
                        : NotificationWebhookDeliveryStatuses.DeadLetter,
                    attempts,
                    (ex as HttpRequestException)?.StatusCode is { } code ? (int)code : null,
                    ex.Message,
                    nextAttempt,
                    cancellationToken,
                    failure);

                if (hasAnotherAttempt)
                {
                    logger.LogWarning(
                        ex,
                        "Webhook delivery attempt {Attempt}/{TotalAttempts} failed for {Url}. Retrying.",
                        attempts,
                        delivery.Item.MaxAttempts,
                        url);
                }
            }
        }

        throw lastError ?? new InvalidOperationException("Webhook delivery failed for an unknown reason.");
    }

    private async Task<int> SendAsync(
        string url,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("notifications");

        object payload;
        if (url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase))
        {
            payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title,
                        description = message,
                        color = GetDiscordColor(eventCategory),
                        footer = new { text = $"Deluno • {eventCategory}" },
                        timestamp = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            };
        }
        else
        {
            object? details = null;
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                using var document = JsonDocument.Parse(detailsJson);
                details = document.RootElement.Clone();
            }

            payload = new
            {
                eventCategory,
                title,
                message,
                details,
                firedAt = DateTimeOffset.UtcNow
            };
        }

        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Webhook returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown status"}).",
                inner: null,
                response.StatusCode);
        }

        return (int)response.StatusCode;
    }

    private static bool IsMatchingEvent(string eventFilters, string eventCategory)
    {
        if (string.IsNullOrWhiteSpace(eventFilters))
        {
            return true;
        }

        var filters = eventFilters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return filters.Any(filter =>
            eventCategory.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filter, "*", StringComparison.Ordinal));
    }

    private static int GetDiscordColor(string eventCategory) => eventCategory switch
    {
        var c when c.Contains("error", StringComparison.OrdinalIgnoreCase) || c.Contains("fail", StringComparison.OrdinalIgnoreCase) => 0xED4245,
        var c when c.Contains("health", StringComparison.OrdinalIgnoreCase) => 0x5865F2,
        var c when c.Contains("grab", StringComparison.OrdinalIgnoreCase) || c.Contains("import", StringComparison.OrdinalIgnoreCase) => 0x57F287,
        _ => 0x99AAB5
    };
}
