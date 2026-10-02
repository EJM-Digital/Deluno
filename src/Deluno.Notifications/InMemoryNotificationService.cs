using Deluno.Notifications.Contracts;

namespace Deluno.Notifications;

public class InMemoryNotificationService : INotificationService
{
    private readonly Dictionary<string, NotificationPreferences> _preferences = new();
    private readonly object _lock = new();

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
