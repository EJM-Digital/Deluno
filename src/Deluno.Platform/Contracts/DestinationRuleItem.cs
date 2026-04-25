namespace Deluno.Platform.Contracts;

public sealed record DestinationRuleItem(
    string Id,
    string Name,
    string MediaType,
    string MatchKind,
    string MatchValue,
    string RootPath,
    string? FolderTemplate,
    int Priority,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
