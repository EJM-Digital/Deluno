using Deluno.Contracts;

namespace Deluno.Jobs.Contracts;

/// <summary>
/// One saved library view attached to the existing scheduled search cycle.
/// The conditions are already validated against the catalogue registry before
/// they enter the planner; an invalid scope is retained only to suppress an
/// unsafe unfiltered fallback and to make the reason visible in logs.
/// </summary>
public sealed record LibraryAutomationScope(
    string Id,
    string Name,
    string QuickFilter,
    string Monitoring,
    CatalogueFilters Filters,
    bool IsValid = true);
