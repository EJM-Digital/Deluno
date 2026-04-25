namespace Deluno.Platform.Contracts;

public sealed record LibraryViewItem(
    string Id,
    string UserId,
    string Variant,
    string Name,
    string QuickFilter,
    string SortField,
    string SortDirection,
    string ViewMode,
    string CardSize,
    string DisplayOptionsJson,
    string RulesJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
