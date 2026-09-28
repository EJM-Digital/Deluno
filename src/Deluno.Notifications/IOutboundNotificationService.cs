namespace Deluno.Notifications;

public interface IOutboundNotificationService
{
    /// <summary>
    /// <paramref name="titleTags"/> is the tags of the title the event is
    /// about, when there is one (#990). A webhook carrying tags fires only
    /// for a title sharing one of them; an untagged webhook fires for any
    /// title (or any event that names no title), unchanged from before tags
    /// existed here.
    /// </summary>
    Task DispatchAsync(
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? titleTags = null);

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
