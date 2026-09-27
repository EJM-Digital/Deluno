namespace Deluno.Platform.Contracts;

public sealed record CreateSharingRuleRequest(
    int? Priority,
    string? CategoryPattern,
    string? TrackerPattern,
    string? TagsCsv,
    string? TagMatchMode,
    string? Mode,
    int? ForHours,
    double? UntilRatio,
    string? StuckAction,
    int? StuckAfterDays,
    string? CompletionAction,
    int? MinSeeders,
    int? MaxInactiveDays,
    bool IsEnabled = true);
