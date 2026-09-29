namespace Deluno.Platform.Contracts;

/// <summary>Bulk tag change on indexers, download clients or notification connections (#1044).</summary>
public sealed record BulkConnectionTagsRequest(
    string? Kind,
    IReadOnlyList<string>? Ids,
    IReadOnlyList<string>? Tags,
    string? Mode);
