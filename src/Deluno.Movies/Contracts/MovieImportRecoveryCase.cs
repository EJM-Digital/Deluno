namespace Deluno.Movies.Contracts;

public sealed record MovieImportRecoveryCase(
    string Id,
    string Title,
    string FailureKind,
    string Summary,
    string RecommendedAction,
    string? DetailsJson,
    DateTimeOffset DetectedUtc);
