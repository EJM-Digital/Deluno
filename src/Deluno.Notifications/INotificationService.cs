using Deluno.Notifications.Contracts;

namespace Deluno.Notifications;

/// <summary>
/// Which kinds of event may reach the outbound notification connections.
/// Deluno does not keep an in-app list of notifications: the bell and the
/// Dashboard read Activity and the attention rules, so there is nothing here
/// to create, list or mark read.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Get notification preferences for the current user
    /// </summary>
    Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update notification preferences
    /// </summary>
    Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default);
}
