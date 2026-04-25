namespace Deluno.Series.Contracts;

public sealed record CreateSeriesImportRecoveryCaseRequest(
    string? Title,
    string? FailureKind,
    string? Summary,
    string? RecommendedAction,
    string? DetailsJson = null);
