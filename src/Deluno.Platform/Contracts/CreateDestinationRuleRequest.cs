namespace Deluno.Platform.Contracts;

public sealed record CreateDestinationRuleRequest(
    string Name,
    string? MediaType,
    string? MatchKind,
    string? MatchValue,
    string RootPath,
    string? FolderTemplate,
    int Priority,
    bool IsEnabled);
