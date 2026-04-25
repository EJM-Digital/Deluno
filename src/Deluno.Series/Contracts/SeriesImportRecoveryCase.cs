namespace Deluno.Series.Contracts;

public sealed record SeriesImportRecoveryCase(
    string Id,
    string Title,
    string FailureKind,
    string Summary,
    string RecommendedAction,
    string? DetailsJson,
    DateTimeOffset DetectedUtc);
