using Deluno.Notifications.Contracts;
using Deluno.Contracts;

namespace Deluno.Notifications.Data;

/// <summary>
/// Notification webhook storage. Carved out of
/// <c>IPlatformSettingsRepository</c> by ADR-001 Step 1; signatures unchanged.
/// </summary>
public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationWebhookItem>> ListNotificationWebhooksAsync(CancellationToken cancellationToken);

    Task<NotificationWebhookItem> CreateNotificationWebhookAsync(CreateNotificationWebhookRequest request, CancellationToken cancellationToken);

    Task<NotificationWebhookItem?> UpdateNotificationWebhookAsync(string id, UpdateNotificationWebhookRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteNotificationWebhookAsync(string id, CancellationToken cancellationToken);

    Task RecordNotificationWebhookFiredAsync(string id, string? error, CancellationToken cancellationToken);

    Task<Contracts.NotificationWebhookDeliveryRecord> CreateNotificationWebhookDeliveryAsync(
        string webhookId,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken);

    Task<Contracts.NotificationWebhookDeliveryRecord?> GetNotificationWebhookDeliveryAsync(
        string deliveryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Contracts.NotificationWebhookDeliveryItem>> ListNotificationWebhookDeliveriesAsync(
        string? status,
        string? webhookId,
        int take,
        CancellationToken cancellationToken);

    Task RecordNotificationWebhookDeliveryAttemptAsync(
        string deliveryId,
        string status,
        int attemptCount,
        int? statusCode,
        string? error,
        DateTimeOffset? nextAttemptUtc,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null);

    /// <summary>
    /// The user's master delivery switch ("Send notifications"). When false,
    /// no webhook fires — including tests — which is what the settings page
    /// promises (#253).
    /// </summary>
    Task<bool> AreOutboundNotificationsEnabledAsync(CancellationToken cancellationToken);
}
