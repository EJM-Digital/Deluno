namespace Deluno.Libraries.Contracts;

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
    int? MaxItemsPerRun,
    string? CleanupMode = null,
    bool RemoveEmptySourceFolders = false,
    /// <summary>How many missing-item candidates a search cycle works through, independent of the upgrade cap (#830). Falls back to <see cref="MaxItemsPerRun"/> when unset.</summary>
    int? MaxMissingItemsPerRun = null,
    /// <summary>How many cutoff-unmet (upgrade) candidates a search cycle works through, independent of the missing cap (#830). Falls back to <see cref="MaxItemsPerRun"/> when unset.</summary>
    int? MaxUpgradeItemsPerRun = null);
