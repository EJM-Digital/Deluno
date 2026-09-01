namespace Deluno.Notifications.Contracts;

using Deluno.Contracts;

public static class NotificationWebhookDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Retrying = "retrying";
    public const string Delivered = "delivered";
    public const string DeadLetter = "dead-letter";
}

/// <summary>
/// Operational delivery history. The event body is intentionally not exposed
/// by the list endpoint; it is retained internally so an authorised replay can
/// resend exactly what was originally emitted.
/// </summary>
public sealed record NotificationWebhookDeliveryItem(
    string Id,
    string WebhookId,
    string EventCategory,
    string Title,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? LastAttemptUtc,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IntegrationFailure? Failure = null);

public sealed record NotificationWebhookDeliveryResult(
    bool Sent,
    string? DeliveryId,
    string Status,
    int Attempts,
    string? Error = null,
    IntegrationFailure? Failure = null);

/// <summary>
/// Internal replay payload plus the public delivery state.
/// </summary>
public sealed record NotificationWebhookDeliveryRecord(
    NotificationWebhookDeliveryItem Item,
    string? WebhookUrl,
    string Message,
    string? DetailsJson);
