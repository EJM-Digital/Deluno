namespace Deluno.Platform.Contracts;

public sealed record ReorderQualityProfilesRequest(
    IReadOnlyList<string>? Ids);
