using System.Net;
using Deluno.Infrastructure.Http;
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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? titleTags = null)
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

            webhooks = await repository.ListNotificationWebhooksWithSecretsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load notification webhooks for event {Category}", eventCategory);
            return;
        }

        // The title's tag names travel in the event details so every channel
        // and a replay carry the same thing (#1045). No title, no tags key.
        detailsJson = WithTags(detailsJson, titleTags);

        foreach (var webhook in webhooks)
        {
            if (!webhook.IsEnabled
                || !IsMatchingEvent(webhook.EventFilters, eventCategory)
                // A health warning goes only to a connection that asked for them (#1016).
                || (!webhook.IncludeHealthWarnings && eventCategory.EndsWith(HealthCheckCategories.OutboundWarningSuffix, StringComparison.Ordinal))
                // Tags only filter events about a title (#1036). null means the
                // event names no title (health, library paused, app update...),
                // so every connection gets it, tagged or not.
                || (titleTags is not null && !Deluno.Contracts.TagRoutingMatch.IsEligible(webhook.Tags, titleTags)))
            {
                continue;
            }

            await DeliverAsync(
                webhook.Id,
                webhook.Url,
                webhook.Provider,
                webhook.ConfigJson,
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

        var webhook = (await repository.ListNotificationWebhooksWithSecretsAsync(cancellationToken))
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
            webhook.Provider,
            webhook.ConfigJson,
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
            reset.WebhookProvider,
            reset.WebhookConfigJson,
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
        string provider,
        string? configJson,
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

            var attempts = await SendWithRetryAsync(delivery, url, provider, configJson, cancellationToken);
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
                "Webhook {WebhookId} ({Host}) failed for event {Category}; it is available for replay.",
                webhookId,
                NotificationSecrets.HostOnly(url),
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
        string provider,
        string? configJson,
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
                    provider,
                    configJson,
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
                        "Webhook delivery attempt {Attempt}/{TotalAttempts} failed for {Host}. Retrying.",
                        attempts,
                        delivery.Item.MaxAttempts,
                        NotificationSecrets.HostOnly(url));
                }
            }
        }

        throw lastError ?? new InvalidOperationException("Webhook delivery failed for an unknown reason.");
    }

    private async Task<int> SendAsync(
        string url,
        string provider,
        string? configJson,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("notifications");
        var config = ParseConfig(configJson);

        // A URL still auto-detected as Discord routes to the embed shape even
        // when the row predates the Provider field (#855 keeps that working).
        var effectiveProvider = provider == NotificationProviders.Generic &&
            url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                ? NotificationProviders.Discord
                : provider;

        // Chat channels have no structured fields, so the tags ride as a line.
        // The generic webhook carries them as a top-level "tags" array instead.
        if (effectiveProvider != NotificationProviders.Generic)
        {
            var tags = ReadTags(detailsJson);
            if (tags.Count > 0)
            {
                message = $"{message}\nTags: {string.Join(", ", tags)}";
            }
        }

        using var request = effectiveProvider switch
        {
            NotificationProviders.Discord => BuildDiscordRequest(url, eventCategory, title, message),
            NotificationProviders.Slack => BuildSlackRequest(url, title, message),
            NotificationProviders.Telegram => BuildTelegramRequest(config, title, message),
            NotificationProviders.Pushover => BuildPushoverRequest(config, title, message),
            NotificationProviders.Ntfy => BuildNtfyRequest(url, config, title, message),
            NotificationProviders.Gotify => BuildGotifyRequest(url, config, title, message),
            NotificationProviders.Apprise => BuildAppriseRequest(url, title, message),
            _ => BuildGenericRequest(url, eventCategory, title, message, detailsJson)
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Webhook returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown status"}).",
                inner: null,
                response.StatusCode);
        }

        return (int)response.StatusCode;
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    private static NotificationProviderConfig ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new NotificationProviderConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationProviderConfig>(configJson, ConfigJsonOptions) ?? new NotificationProviderConfig();
        }
        catch (JsonException)
        {
            return new NotificationProviderConfig();
        }
    }

    private static HttpRequestMessage BuildGenericRequest(
        string url, string eventCategory, string title, string message, string? detailsJson)
    {
        object? details = null;
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            using var document = JsonDocument.Parse(detailsJson);
            details = document.RootElement.Clone();
        }

        var payload = new
        {
            instanceName = Deluno.Contracts.DelunoHostName.Current,
            eventCategory,
            title,
            message,
            tags = ReadTagsOrNull(detailsJson),
            details,
            firedAt = DateTimeOffset.UtcNow
        };

        return new HttpRequestMessage(HttpMethod.Post, url) { Content = SizedJson.Create(payload) };
    }

    private static HttpRequestMessage BuildDiscordRequest(string url, string eventCategory, string title, string message)
    {
        var payload = new
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

        return new HttpRequestMessage(HttpMethod.Post, url) { Content = SizedJson.Create(payload) };
    }

    // https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks
    // POST application/json, body { "text": "..." }.
    private static HttpRequestMessage BuildSlackRequest(string url, string title, string message)
    {
        var payload = new { text = $"*{title}*\n{message}" };
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = SizedJson.Create(payload) };
    }

    // https://core.telegram.org/bots/api#sendmessage
    // POST https://api.telegram.org/bot<token>/sendMessage, JSON { chat_id, text }.
    private static HttpRequestMessage BuildTelegramRequest(NotificationProviderConfig config, string title, string message)
    {
        var botToken = config.BotToken?.Trim() ?? string.Empty;
        var chatId = config.ChatId?.Trim() ?? string.Empty;
        // The token's colon is part of Telegram's documented bot<token> path
        // segment, unescaped (https://api.telegram.org/bot<token>/METHOD).
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = new { chat_id = chatId, text = $"{title}\n\n{message}" };
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = SizedJson.Create(payload) };
    }

    // https://pushover.net/api
    // POST https://api.pushover.net/1/messages.json, application/x-www-form-urlencoded.
    private static HttpRequestMessage BuildPushoverRequest(NotificationProviderConfig config, string title, string message)
    {
        var form = new Dictionary<string, string>
        {
            ["token"] = config.AppToken?.Trim() ?? string.Empty,
            ["user"] = config.UserKey?.Trim() ?? string.Empty,
            ["title"] = title,
            ["message"] = message
        };

        return new HttpRequestMessage(HttpMethod.Post, "https://api.pushover.net/1/messages.json")
        {
            Content = new FormUrlEncodedContent(form)
        };
    }

    // https://docs.ntfy.sh/publish/
    // POST {server}/{topic}, plain-text body, title via the Title header.
    private static HttpRequestMessage BuildNtfyRequest(string url, NotificationProviderConfig config, string title, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message, System.Text.Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Title", NtfyHeaderValue(title));
        if (!string.IsNullOrWhiteSpace(config.AccessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AccessToken.Trim());
        }

        return request;
    }

    // .NET refuses a header value outside ASCII, so an accent or a dash in a title
    // never left the machine. ntfy reads an RFC 2047 encoded-word (UTF-8, base64)
    // in its Title header, which is what it is sent as when plain ASCII will not do.
    internal static string NtfyHeaderValue(string title)
    {
        if (title.All(c => c >= 0x20 && c < 0x7f))
        {
            return title;
        }

        var flat = string.Join(" ", title.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
        return "=?UTF-8?B?" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(flat)) + "?=";
    }

    // https://gotify.net/docs/pushmsg
    // POST {server}/message, X-Gotify-Key header, JSON { title, message, priority }.
    private static HttpRequestMessage BuildGotifyRequest(string url, NotificationProviderConfig config, string title, string message)
    {
        var baseUrl = url.TrimEnd('/');
        var payload = new { title, message, priority = 5 };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/message")
        {
            Content = SizedJson.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(config.AppToken))
        {
            request.Headers.Add("X-Gotify-Key", config.AppToken.Trim());
        }

        return request;
    }

    // https://github.com/caronc/apprise/wiki/Notify_Custom_JSON
    // POST application/json, body { title, message } (Apprise's default JSON shape).
    private static HttpRequestMessage BuildAppriseRequest(string url, string title, string message)
    {
        var payload = new { title, message, type = "info" };
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = SizedJson.Create(payload) };
    }

    private static string? WithTags(string? detailsJson, IReadOnlyList<string>? titleTags)
    {
        if (titleTags is null)
        {
            return detailsJson;
        }

        var names = titleTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToArray();
        try
        {
            var node = string.IsNullOrWhiteSpace(detailsJson)
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonNode.Parse(detailsJson);
            if (node is not System.Text.Json.Nodes.JsonObject obj)
            {
                return detailsJson;
            }

            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (var name in names)
            {
                array.Add(name);
            }

            obj["tags"] = array;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return detailsJson;
        }
    }

    private static IReadOnlyList<string> ReadTags(string? detailsJson) => ReadTagsOrNull(detailsJson) ?? [];

    private static string[]? ReadTagsOrNull(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tags", out var tags)
                && tags.ValueKind == JsonValueKind.Array)
            {
                return tags.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool IsMatchingEvent(string eventFilters, string eventCategory)
    {
        if (string.IsNullOrWhiteSpace(eventFilters))
        {
            return true;
        }

        // "all" is what the settings screen stores for All events; it is not a
        // prefix of any category, so those webhooks never fired (#524).
        var filters = eventFilters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return filters.Any(filter =>
            eventCategory.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filter, "*", StringComparison.Ordinal) ||
            string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetDiscordColor(string eventCategory) => eventCategory switch
    {
        var c when c.Contains("error", StringComparison.OrdinalIgnoreCase) || c.Contains("fail", StringComparison.OrdinalIgnoreCase) => 0xED4245,
        var c when c.Contains("health", StringComparison.OrdinalIgnoreCase) => 0x5865F2,
        var c when c.Contains("grab", StringComparison.OrdinalIgnoreCase) || c.Contains("import", StringComparison.OrdinalIgnoreCase) => 0x57F287,
        _ => 0x99AAB5
    };
}
