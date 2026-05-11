namespace Deluno.Platform.Contracts;

public class NotificationPreferences
{
    public bool SearchCompletionEnabled { get; set; } = true;
    public bool DownloadStartedEnabled { get; set; } = true;
    public bool DownloadProgressEnabled { get; set; } = true;
    public bool DownloadCompletedEnabled { get; set; } = true;
    public bool ImportStartedEnabled { get; set; } = true;
    public bool ImportCompletedEnabled { get; set; } = true;
    public bool ImportFailedEnabled { get; set; } = true;
    public bool AutomationErrorEnabled { get; set; } = true;
    public bool SystemWarningsEnabled { get; set; } = true;

    // Delivery methods
    public bool InAppNotificationsEnabled { get; set; } = true;
    public bool EmailNotificationsEnabled { get; set; } = false;
    public bool WebhookNotificationsEnabled { get; set; } = false;

    // Email settings (only used if EmailNotificationsEnabled is true)
    public string? EmailAddress { get; set; }

    // Webhook settings (only used if WebhookNotificationsEnabled is true)
    public string? WebhookUrl { get; set; }
}
