namespace Deluno.Contracts;

/// <summary>
/// Pushed when something on the System or Settings screens changes (#1179), so a
/// screen that shows it updates with no reload and no timer. Each carries only
/// identity, never the new value: the screen asks again for what it shows.
/// They travel on the "dashboard" subject.
/// </summary>
public static class SystemChangeEventNames
{
    public const string BackupChanged = "BackupChanged";
    public const string UpdateStatusChanged = "UpdateStatusChanged";
    public const string RecycleBinChanged = "RecycleBinChanged";
    public const string SystemTaskChanged = "SystemTaskChanged";
    public const string MonitoringChanged = "MonitoringChanged";
    public const string SystemDatabasesChanged = "SystemDatabasesChanged";
    public const string OutboundThrottleChanged = "OutboundThrottleChanged";
    public const string CompanionJobChanged = "CompanionJobChanged";
    public const string BundledComponentChanged = "BundledComponentChanged";
    public const string ConfigChanged = "ConfigChanged";
}

/// <summary>The Settings areas a <c>ConfigChanged</c> event can name. A Settings page revalidates when its own area changes.</summary>
public static class ConfigAreas
{
    public const string Tags = "tags";
    public const string AutoTagging = "auto-tagging";
    public const string CustomFormats = "custom-formats";
    public const string QualityModel = "quality-model";
    public const string Notifications = "notifications";
    public const string Retention = "retention";
    public const string MediaServers = "media-servers";
    public const string Subtitles = "subtitles";
    public const string Security = "security";
    public const string ReleaseRules = "release-rules";
    public const string DestinationRules = "destination-rules";
    public const string DownloadHealth = "download-health";
    public const string Processors = "processors";
    public const string Lists = "lists";
}

public static class SystemChangeEventExtensions
{
    // The nullable receivers let a service that was built without a publisher (tests, tools) call these freely.
    public static Task PublishBackupChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("Backup", "backups", cancellationToken);

    public static Task PublishUpdateStatusChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("UpdateStatus", "updates", cancellationToken);

    public static Task PublishRecycleBinChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("RecycleBin", "recycle-bin", cancellationToken);

    public static Task PublishSystemTaskChangedAsync(this IRealtimeEventPublisher? publisher, string scheduleKey, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("SystemTask", scheduleKey, cancellationToken);

    public static Task PublishMonitoringChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("Monitoring", "monitoring", cancellationToken);

    public static Task PublishSystemDatabasesChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("SystemDatabases", "databases", cancellationToken);

    public static Task PublishOutboundThrottleChangedAsync(this IRealtimeEventPublisher? publisher, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("OutboundThrottle", "outbound", cancellationToken);

    public static Task PublishCompanionJobChangedAsync(this IRealtimeEventPublisher? publisher, string jobId, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("CompanionJob", jobId, cancellationToken);

    public static Task PublishBundledComponentChangedAsync(this IRealtimeEventPublisher? publisher, string componentId, CancellationToken cancellationToken = default)
        => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("BundledComponent", componentId, cancellationToken);

    /// <summary>A Settings area whose data changed (tags, custom formats, notifications and so on).</summary>
    public static Task PublishConfigChangedAsync(this IRealtimeEventPublisher? publisher, string area, CancellationToken cancellationToken = default)
        => publisher is null
            ? Task.CompletedTask
            : publisher.PublishDashboardEventAsync(SystemChangeEventNames.ConfigChanged, new { area }, cancellationToken);
}
