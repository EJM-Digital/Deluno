namespace Deluno.Movies.Contracts;

public sealed record MetadataRatingItem(
    string Source,
    string Label,
    double? Score,
    double? MaxScore,
    int? VoteCount,
    string? Url,
    string? Kind);
