namespace Deluno.Platform.Contracts;

public class NotificationItem
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "info"; // info, success, warning, error
    public DateTime CreatedUtc { get; set; }
    public DateTime? ReadUtc { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    public bool IsRead => ReadUtc.HasValue;
}

public enum NotificationType
{
    SearchCompleted,
    DownloadStarted,
    DownloadProgress,
    DownloadCompleted,
    DownloadFailed,
    ImportStarted,
    ImportCompleted,
    ImportFailed,
    AutomationError,
    SystemWarning,
    Custom
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}
