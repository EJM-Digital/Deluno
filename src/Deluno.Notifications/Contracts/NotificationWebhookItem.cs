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
    string? ConfigJson = null,
    /// <summary>Comma-separated tag names (#990). Blank fires this connection for any title; a tagged connection fires only for a title sharing one of them.</summary>
    string Tags = "");

public sealed record CreateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled,
    string? Provider = null,
    string? ConfigJson = null,
    string? Tags = null);

public sealed record UpdateNotificationWebhookRequest(
    string? Name,
    string? Url,
    string? EventFilters,
    bool IsEnabled,
    string? Provider = null,
    string? ConfigJson = null,
    string? Tags = null);
