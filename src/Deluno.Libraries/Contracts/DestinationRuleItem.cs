namespace Deluno.Libraries.Contracts;

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
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// The library this rule routes a title to (#1033), when it is used for
    /// library routing rather than only folder placement. Null on a rule that
    /// only overrides a root folder without choosing a library.
    /// </summary>
    string? LibraryId = null);
