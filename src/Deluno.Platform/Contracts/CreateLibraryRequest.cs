namespace Deluno.Platform.Contracts;

public sealed record CreateLibraryRequest(
    string? Name,
    string? MediaType,
    string? Purpose,
    string? RootPath,
    string? DownloadsPath,
    string? QualityProfileId,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int? SearchIntervalHours,
    int? RetryDelayHours,
    int? MaxItemsPerRun);
