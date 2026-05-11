namespace Deluno.Platform.Contracts;

public sealed record NotificationWebhookItem(
    string Id,
    string Name,
    string Url,
    string EventFilters,
    bool IsEnabled,
    DateTimeOffset? LastFiredUtc,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CreateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled);

public sealed record UpdateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled);
