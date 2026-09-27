namespace Deluno.Notifications.Contracts;

/// <summary>
/// Dedicated outbound notification provider types (#855), on top of the
/// original generic-webhook-or-sniffed-Discord-URL behaviour. Each value
/// routes OutboundNotificationService to a provider-specific payload shape
/// instead of only branching on "looks like a Discord URL".
/// </summary>
public static class NotificationProviders
{
    public const string Generic = "generic";
    public const string Discord = "discord";
    public const string Slack = "slack";
    public const string Telegram = "telegram";
    public const string Pushover = "pushover";
    public const string Ntfy = "ntfy";
    public const string Gotify = "gotify";
    public const string Apprise = "apprise";

    public static readonly IReadOnlyList<string> All =
    [
        Generic, Discord, Slack, Telegram, Pushover, Ntfy, Gotify, Apprise
    ];

    public static bool IsValid(string? value) => value is not null && All.Contains(value.Trim().ToLowerInvariant());

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Generic;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return All.Contains(trimmed) ? trimmed : Generic;
    }
}

/// <summary>
/// Provider-specific connection fields that do not fit "one URL" (Telegram's
/// bot token + chat id, Pushover's app token + user key, Gotify's app token).
/// Stored as JSON alongside the webhook row rather than widening the table
/// with columns most providers never use.
/// </summary>
public sealed record NotificationProviderConfig(
    string? BotToken = null,
    string? ChatId = null,
    string? AppToken = null,
    string? UserKey = null,
    string? AccessToken = null);
