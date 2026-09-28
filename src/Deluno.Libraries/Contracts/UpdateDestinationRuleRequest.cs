namespace Deluno.Libraries.Contracts;

public sealed record UpdateDestinationRuleRequest(
    string Name,
    string? MediaType,
    string? MatchKind,
    string? MatchValue,
    string RootPath,
    string? FolderTemplate,
    int Priority,
    bool IsEnabled,
    /// <summary>The library this rule routes a title to (#1033). Null keeps it a folder-only rule.</summary>
    string? LibraryId = null);
