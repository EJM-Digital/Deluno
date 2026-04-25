namespace Deluno.Movies.Contracts;

public sealed record CreateMovieImportRecoveryCaseRequest(
    string? Title,
    string? FailureKind,
    string? Summary,
    string? RecommendedAction,
    string? DetailsJson = null);
