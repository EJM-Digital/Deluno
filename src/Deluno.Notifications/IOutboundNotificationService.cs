namespace Deluno.Notifications;

public interface IOutboundNotificationService
{
    Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken);

    Task<Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(
        string webhookId,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken);

    Task<Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(
        string deliveryId,
        CancellationToken cancellationToken);
}
