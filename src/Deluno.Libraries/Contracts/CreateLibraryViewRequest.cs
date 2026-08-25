namespace Deluno.Libraries.Contracts;

public sealed record CreateLibraryViewRequest(
    string Variant,
    string? LibraryId,
    string Name,
    string QuickFilter,
    string SortField,
    string SortDirection,
    string ViewMode,
    string CardSize,
    string DisplayOptionsJson,
    string RulesJson);
