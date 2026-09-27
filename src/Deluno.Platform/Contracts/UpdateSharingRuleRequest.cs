namespace Deluno.Platform.Contracts;

/// <summary>Patch-style request — null means "leave unchanged", the clear flags drop a field back to "not set".</summary>
public sealed record UpdateSharingRuleRequest(
    int? Priority = null,
    string? CategoryPattern = null,
    bool? ClearCategoryPattern = null,
    string? TrackerPattern = null,
    bool? ClearTrackerPattern = null,
    string? TagsCsv = null,
    bool? ClearTags = null,
    string? TagMatchMode = null,
    string? Mode = null,
    int? ForHours = null,
    double? UntilRatio = null,
    string? StuckAction = null,
    int? StuckAfterDays = null,
    string? CompletionAction = null,
    bool? ClearPolicy = null,
    int? MinSeeders = null,
    bool? ClearMinSeeders = null,
    int? MaxInactiveDays = null,
    bool? ClearMaxInactiveDays = null,
    bool? IsEnabled = null);
