namespace Deluno.Notifications.Contracts;

public sealed record NotificationWebhookItem(
    string Id,
    string Name,
    string Url,
    string EventFilters,
    bool IsEnabled,
    DateTimeOffset? LastFiredUtc,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string Provider = NotificationProviders.Generic,
    string? ConfigJson = null);

public sealed record CreateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled,
    string? Provider = null,
    string? ConfigJson = null);

public sealed record UpdateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled,
    string? Provider = null,
    string? ConfigJson = null);
