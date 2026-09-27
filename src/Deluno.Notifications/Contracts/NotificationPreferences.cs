namespace Deluno.Notifications.Contracts;

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

    /// <summary>
    /// Covers both "health.degraded" and "health.restored" for every
    /// provider type (indexers, download clients, media servers,
    /// processors) with one decision, matching Deluno's single-user,
    /// one-decision-per-concept style rather than per-provider-type
    /// toggles (#859).
    /// </summary>
    public bool ProviderHealthChangedEnabled { get; set; } = true;

    // Subtitles (#839): nothing fired for subtitle activity before this.
    public bool SubtitleDownloadedEnabled { get; set; } = true;
    public bool SubtitleUpgradedEnabled { get; set; } = true;
    public bool SubtitleFailedEnabled { get; set; } = true;

    // Delivery methods
    public bool InAppNotificationsEnabled { get; set; } = true;
    public bool EmailNotificationsEnabled { get; set; } = false;
    public bool WebhookNotificationsEnabled { get; set; } = false;

    // Email settings (only used if EmailNotificationsEnabled is true)
    public string? EmailAddress { get; set; }

    // Webhook settings (only used if WebhookNotificationsEnabled is true)
    public string? WebhookUrl { get; set; }
}
