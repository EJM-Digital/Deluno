namespace Deluno.Libraries.Contracts;

public sealed record UpdateLibraryViewRequest(
    string? LibraryId,
    string Name,
    string QuickFilter,
    string SortField,
    string SortDirection,
    string ViewMode,
    string CardSize,
    string DisplayOptionsJson,
    string RulesJson);
