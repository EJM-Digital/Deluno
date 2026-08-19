namespace Deluno.Quality.Contracts;

public sealed record ReorderQualityProfilesRequest(
    IReadOnlyList<string>? Ids);
