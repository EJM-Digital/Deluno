namespace Deluno.Platform.Contracts;

public sealed record PlatformSettingsSnapshot(
    string AppInstanceName,
    string? MovieRootPath,
    string? SeriesRootPath,
    string? DownloadsPath,
    string? IncompleteDownloadsPath,
    bool AutoStartJobs,
    bool EnableNotifications,
    DateTimeOffset UpdatedUtc);
