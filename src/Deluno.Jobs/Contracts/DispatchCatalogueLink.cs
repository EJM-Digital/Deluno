namespace Deluno.Jobs.Contracts;

/// <summary>
/// Ties a dispatched release back to the catalogue item it was grabbed for.
/// The import path uses it to name files from the catalogue title rather than
/// from the release name a download client reports.
/// </summary>
/// <param name="DispatchId">The dispatch record, for import-outcome reporting.</param>
/// <param name="EntityType">"movie" or "series".</param>
/// <param name="EntityId">The catalogue id within that engine.</param>
public sealed record DispatchCatalogueLink(
    string DispatchId,
    string EntityType,
    string EntityId);
