namespace Deluno.Platform.Contracts;

public sealed record UpdateLibraryAutomationRequest(
    bool AutoSearchEnabled,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int? SearchIntervalHours,
    int? RetryDelayHours,
    int? MaxItemsPerRun,
    int? SearchWindowStartHour,
    int? SearchWindowEndHour);
