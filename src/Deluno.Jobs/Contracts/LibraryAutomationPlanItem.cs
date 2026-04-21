namespace Deluno.Jobs.Contracts;

public sealed record LibraryAutomationPlanItem(
    string LibraryId,
    string LibraryName,
    string MediaType,
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int SearchIntervalHours,
    int RetryDelayHours,
    int MaxItemsPerRun);
