namespace Deluno.Libraries.Contracts;

public sealed record CreateLibraryViewRequest(
    string Variant,
    string? LibraryId,
    string Name,
    string QuickFilter,
    /// <summary>
    /// The monitoring axis: <c>any</c>, <c>monitored</c> or <c>unmonitored</c>.
    ///
    /// Separate from <see cref="QuickFilter"/> because a state and an intent
    /// multiply — "missing" and "unmonitored" are both true of the same title.
    /// Null means <c>any</c>, which is what every view saved before the split
    /// meant.
    /// </summary>
    string? Monitoring,
    string SortField,
    string SortDirection,
    string ViewMode,
    string CardSize,
    string DisplayOptionsJson,
    string RulesJson,
    /// <summary>
    /// An optional action performed by the existing library automation cycle.
    /// Null means this remains a presentation-only view; <c>search</c> scopes
    /// the normal library search pass to this view's saved narrowing.
    /// </summary>
    string? AutomationAction = null);
