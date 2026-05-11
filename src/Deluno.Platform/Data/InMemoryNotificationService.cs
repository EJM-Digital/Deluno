using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public class InMemoryNotificationService : INotificationService
{
    private readonly Dictionary<string, NotificationItem> _notifications = new();
    private readonly Dictionary<string, NotificationPreferences> _preferences = new();
    private readonly object _lock = new();

    public Task<NotificationItem> CreateNotificationAsync(
        string type,
        string title,
        string message,
        string severity,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var notification = new NotificationItem
            {
                Id = Guid.NewGuid().ToString(),
                Type = type,
                Title = title,
                Message = message,
                Severity = severity,
                CreatedUtc = DateTime.UtcNow,
                Metadata = metadata
            };

            _notifications[notification.Id] = notification;
            return Task.FromResult(notification);
        }
    }

    public Task<List<NotificationItem>> GetNotificationsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var result = _notifications.Values
                .OrderByDescending(n => n.CreatedUtc)
                .Skip(offset)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var count = _notifications.Values.Count(n => !n.IsRead);
            return Task.FromResult(count);
        }
    }

    public Task MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_notifications.TryGetValue(notificationId, out var notification))
            {
                notification.ReadUtc = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }
    }

    public Task MarkAllAsReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var notification in _notifications.Values)
            {
                if (!notification.IsRead)
                {
                    notification.ReadUtc = now;
                }
            }
            return Task.CompletedTask;
        }
    }

    public Task DeleteNotificationAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _notifications.Remove(notificationId);
            return Task.CompletedTask;
        }
    }

    public Task ClearAllNotificationsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _notifications.Clear();
            return Task.CompletedTask;
        }
    }

    public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        lock (_lock)
        {
            if (!_preferences.TryGetValue(userId, out var prefs))
            {
                prefs = new NotificationPreferences();
                _preferences[userId] = prefs;
            }
            return Task.FromResult(prefs);
        }
    }

    public Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        lock (_lock)
        {
            _preferences[userId] = preferences;
            return Task.CompletedTask;
        }
    }

    private string GetCurrentUserId()
    {
        // In a real implementation, this would come from the HTTP context
        // For now, use a default user ID
        return "default-user";
    }
}
