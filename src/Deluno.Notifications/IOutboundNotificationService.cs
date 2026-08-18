namespace Deluno.Notifications;

public interface IOutboundNotificationService
{
    Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken);
}
