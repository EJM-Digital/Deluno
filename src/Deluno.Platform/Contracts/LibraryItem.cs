namespace Deluno.Platform.Contracts;

public sealed record LibraryItem(
    string Id,
    string Name,
    string MediaType,
    string Purpose,
    string RootPath,
    string? DownloadsPath,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int SearchIntervalHours,
    int RetryDelayHours,
    int MaxItemsPerRun,
    string AutomationStatus,
    bool SearchRequested,
    DateTimeOffset? LastSearchedUtc,
    DateTimeOffset? NextSearchUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
