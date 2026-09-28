using Deluno.Notifications;
using Deluno.Notifications.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Minimal in-memory INotificationService for tests that only need
/// preferences, e.g. DefaultUpdateOrchestrator's "On Application Update"
/// dispatch gate (#856).
/// </summary>
internal sealed class FakeNotificationService : INotificationService
{
    public NotificationPreferences Preferences { get; set; } = new();

    public Task<NotificationItem> CreateNotificationAsync(
        string type, string title, string message, string severity,
        Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationItem { Type = type, Title = title, Message = message, Severity = severity });

    public Task<List<NotificationItem>> GetNotificationsAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<NotificationItem>());

    public Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MarkAllAsReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteNotificationAsync(string notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAllNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Preferences);

    public Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        Preferences = preferences;
        return Task.CompletedTask;
    }
}

/// <summary>Records every DispatchAsync call; used to assert an outbound trigger fired (or stayed silent).</summary>
internal sealed class FakeOutboundNotificationService : IOutboundNotificationService
{
    public List<string> DispatchedCategories { get; } = [];

    public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
    {
        DispatchedCategories.Add(eventCategory);
        return Task.CompletedTask;
    }

    public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
