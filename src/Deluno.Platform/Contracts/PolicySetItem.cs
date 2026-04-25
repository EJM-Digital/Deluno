namespace Deluno.Platform.Contracts;

public sealed record PolicySetItem(
    string Id,
    string Name,
    string MediaType,
    string? QualityProfileId,
    string? QualityProfileName,
    string? DestinationRuleId,
    string? DestinationRuleName,
    string CustomFormatIds,
    int? SearchIntervalOverrideHours,
    int? RetryDelayOverrideHours,
    bool UpgradeUntilCutoff,
    bool IsEnabled,
    string? Notes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
