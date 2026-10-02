using Deluno.Notifications.Contracts;

namespace Deluno.Notifications;

public interface INotificationService
{
    /// <summary>
    /// Create and send a notification
    /// </summary>
    Task<NotificationItem> CreateNotificationAsync(
        string type,
        string title,
        string message,
        string severity,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all notifications for the current user
    /// </summary>
    Task<List<NotificationItem>> GetNotificationsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notifications count
    /// </summary>
    Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notification preferences for the current user
    /// </summary>
    Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update notification preferences
    /// </summary>
    Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default);
}
