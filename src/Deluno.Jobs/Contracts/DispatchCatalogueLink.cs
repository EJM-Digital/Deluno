namespace Deluno.Jobs.Contracts;

/// <summary>
/// Ties a dispatched release back to the catalogue item it was grabbed for.
/// The import path uses it to name files from the catalogue title rather than
/// from the release name a download client reports.
/// </summary>
/// <param name="DispatchId">The dispatch record, for import-outcome reporting.</param>
/// <param name="EntityType">"movie" or "series".</param>
/// <param name="EntityId">The catalogue id within that engine.</param>
/// <param name="IndexerName">
/// The search source the release came from. Sharing rules belong to the site
/// rather than the library, so reclaiming a completed download has to know
/// which source's rule applies to it (#288). Empty when the dispatch predates
/// the field or the grab recorded no source.
/// </param>
public sealed record DispatchCatalogueLink(
    string DispatchId,
    string EntityType,
    string EntityId,
    string IndexerName = "");
