using Deluno.Platform.Contracts;
using Deluno.Realtime;
using Microsoft.Extensions.Hosting;

namespace Deluno.Platform.Data;

/// <summary>
/// Publishes notifications to clients when significant events occur in the system.
/// Listens to realtime events and converts them into user-facing notifications.
/// </summary>
public class NotificationEventPublisher : IHostedService
{
    private readonly INotificationService _notificationService;
    private readonly IRealtimeEventPublisher _realtimeEventPublisher;

    public NotificationEventPublisher(
        INotificationService notificationService,
        IRealtimeEventPublisher realtimeEventPublisher)
    {
        _notificationService = notificationService;
        _realtimeEventPublisher = realtimeEventPublisher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Notification creation is explicitly invoked by integration/workflow services.
        // Startup is currently a no-op because no event-bus subscription is required here.
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a search completes
    /// </summary>
    public async Task PublishSearchCompletedAsync(
        string title,
        int resultsCount,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.SearchCompletionEnabled)
            return;

        var message = $"Search completed for {title}. Found {resultsCount} results.";
        await _notificationService.CreateNotificationAsync(
            "search_completed",
            "Search Completed",
            message,
            "success",
            new Dictionary<string, object> { { "title", title }, { "resultsCount", resultsCount } },
            cancellationToken);
    }

    /// <summary>
    /// Called when a download starts
    /// </summary>
    public async Task PublishDownloadStartedAsync(
        string releaseName,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.DownloadStartedEnabled)
            return;

        var message = $"Download started: {releaseName}";
        await _notificationService.CreateNotificationAsync(
            "download_started",
            "Download Started",
            message,
            "info",
            new Dictionary<string, object> { { "releaseName", releaseName } },
            cancellationToken);
    }

    /// <summary>
    /// Called when download progress updates (every 25%)
    /// </summary>
    public async Task PublishDownloadProgressAsync(
        string releaseName,
        double percentComplete,
        string? eta = null,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.DownloadProgressEnabled)
            return;

        var etaText = eta != null ? $" (ETA: {eta})" : "";
        var message = $"Download progress: {releaseName} - {percentComplete:F0}%{etaText}";
        await _notificationService.CreateNotificationAsync(
            "download_progress",
            "Download Progress",
            message,
            "info",
            new Dictionary<string, object> { { "releaseName", releaseName }, { "percentComplete", percentComplete }, { "eta", eta ?? string.Empty } },
            cancellationToken);
    }

    /// <summary>
    /// Called when a download completes successfully
    /// </summary>
    public async Task PublishDownloadCompletedAsync(
        string releaseName,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.DownloadCompletedEnabled)
            return;

        var message = $"Download completed: {releaseName}";
        await _notificationService.CreateNotificationAsync(
            "download_completed",
            "Download Completed",
            message,
            "success",
            new Dictionary<string, object> { { "releaseName", releaseName } },
            cancellationToken);
    }

    /// <summary>
    /// Called when a download fails
    /// </summary>
    public async Task PublishDownloadFailedAsync(
        string releaseName,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);

        var message = $"Download failed: {releaseName}";
        if (!string.IsNullOrWhiteSpace(reason))
            message += $" - {reason}";

        await _notificationService.CreateNotificationAsync(
            "download_failed",
            "Download Failed",
            message,
            "error",
            new Dictionary<string, object> { { "releaseName", releaseName }, { "reason", reason ?? string.Empty } },
            cancellationToken);
    }

    /// <summary>
    /// Called when import starts
    /// </summary>
    public async Task PublishImportStartedAsync(
        string releaseName,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.ImportStartedEnabled)
            return;

        var message = $"Import started: {releaseName} ({mediaType})";
        await _notificationService.CreateNotificationAsync(
            "import_started",
            "Import Started",
            message,
            "info",
            new Dictionary<string, object> { { "releaseName", releaseName }, { "mediaType", mediaType } },
            cancellationToken);
    }

    /// <summary>
    /// Called when import completes successfully
    /// </summary>
    public async Task PublishImportCompletedAsync(
        string releaseName,
        string importedPath,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.ImportCompletedEnabled)
            return;

        var message = $"Import completed: {releaseName}";
        await _notificationService.CreateNotificationAsync(
            "import_completed",
            "Import Completed",
            message,
            "success",
            new Dictionary<string, object> { { "releaseName", releaseName }, { "importedPath", importedPath } },
            cancellationToken);
    }

    /// <summary>
    /// Called when import fails
    /// </summary>
    public async Task PublishImportFailedAsync(
        string releaseName,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.ImportFailedEnabled)
            return;

        var message = $"Import failed: {releaseName}";
        if (!string.IsNullOrWhiteSpace(reason))
            message += $" - {reason}";

        await _notificationService.CreateNotificationAsync(
            "import_failed",
            "Import Failed",
            message,
            "error",
            new Dictionary<string, object> { { "releaseName", releaseName }, { "reason", reason ?? string.Empty } },
            cancellationToken);
    }

    /// <summary>
    /// Called when automation encounters an error
    /// </summary>
    public async Task PublishAutomationErrorAsync(
        string automationName,
        string error,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.AutomationErrorEnabled)
            return;

        var message = $"Automation error in {automationName}: {error}";
        await _notificationService.CreateNotificationAsync(
            "automation_error",
            "Automation Error",
            message,
            "error",
            new Dictionary<string, object> { { "automationName", automationName }, { "error", error } },
            cancellationToken);
    }

    /// <summary>
    /// Called for system warnings
    /// </summary>
    public async Task PublishSystemWarningAsync(
        string warning,
        CancellationToken cancellationToken = default)
    {
        var prefs = await _notificationService.GetPreferencesAsync(cancellationToken);
        if (!prefs.SystemWarningsEnabled)
            return;

        await _notificationService.CreateNotificationAsync(
            "system_warning",
            "System Warning",
            warning,
            "warning",
            null,
            cancellationToken);
    }
}
