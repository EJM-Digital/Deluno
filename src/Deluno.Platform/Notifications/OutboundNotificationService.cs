using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Notifications;

public sealed class OutboundNotificationService(
    IPlatformSettingsRepository repository,
    IHttpClientFactory httpClientFactory,
    ILogger<OutboundNotificationService> logger) : IOutboundNotificationService
{
    public async Task DispatchAsync(
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Contracts.NotificationWebhookItem> webhooks;
        try
        {
            webhooks = await repository.ListNotificationWebhooksAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load notification webhooks for event {Category}", eventCategory);
            return;
        }

        foreach (var webhook in webhooks)
        {
            if (!webhook.IsEnabled)
            {
                continue;
            }

            if (!IsMatchingEvent(webhook.EventFilters, eventCategory))
            {
                continue;
            }

            string? error = null;
            try
            {
                await SendAsync(webhook.Url, eventCategory, title, message, detailsJson, cancellationToken);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "Webhook {Name} ({Url}) failed for event {Category}", webhook.Name, webhook.Url, eventCategory);
            }

            try
            {
                await repository.RecordNotificationWebhookFiredAsync(webhook.Id, error, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record webhook fire result for {Id}", webhook.Id);
            }
        }
    }

    private async Task SendAsync(
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
            payload = new
            {
                eventCategory,
                title,
                message,
                details = detailsJson is not null ? JsonDocument.Parse(detailsJson).RootElement : (object?)null,
                firedAt = DateTimeOffset.UtcNow
            };
        }

        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
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
        var c when c.Contains("error") || c.Contains("fail") => 0xED4245,
        var c when c.Contains("health") => 0x5865F2,
        var c when c.Contains("grab") || c.Contains("import") => 0x57F287,
        _ => 0x99AAB5
    };
}
