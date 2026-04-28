namespace Deluno.Platform.Contracts;

public sealed record CreateLibraryRequest(
    string? Name,
    string? MediaType,
    string? Purpose,
    string? RootPath,
    string? DownloadsPath,
    string? QualityProfileId,
    string? ImportWorkflow,
    string? ProcessorName,
    string? ProcessorOutputPath,
    int? ProcessorTimeoutMinutes,
    string? ProcessorFailureMode,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int? SearchIntervalHours,
    int? RetryDelayHours,
    int? MaxItemsPerRun);
