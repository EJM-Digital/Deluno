namespace Deluno.Quality.Contracts;

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
    string? Notes,
    MediaPlanAutomationIntent? AutomationIntent = null,
    /// <summary>
    /// The plan hash the change was reviewed against (<c>basePlanHash</c> of the preview). When given, a save is refused
    /// with a conflict if the plan has changed since; when left out the save is made as before.
    /// </summary>
    string? ExpectedPlanHash = null);
