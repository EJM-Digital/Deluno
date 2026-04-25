namespace Deluno.Platform.Contracts;

public sealed record UpdatePolicySetRequest(
    string Name,
    string? MediaType,
    string? QualityProfileId,
    string? DestinationRuleId,
    string? CustomFormatIds,
    int? SearchIntervalOverrideHours,
    int? RetryDelayOverrideHours,
    bool UpgradeUntilCutoff,
    bool IsEnabled,
    string? Notes);
