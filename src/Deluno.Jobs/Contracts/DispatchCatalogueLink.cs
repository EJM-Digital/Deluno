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
/// <param name="LibraryId">
/// The library the release was grabbed for. Needed to tell whether the download
/// client's copy and the library's are one set of file data or two — the
/// difference between sharing costing nothing and sharing filling a drive
/// (#288). Empty where the dispatch recorded no library.
/// </param>
public sealed record DispatchCatalogueLink(
    string DispatchId,
    string EntityType,
    string EntityId,
    string IndexerName = "",
    string LibraryId = "",
    bool ReplacementAuthorized = false,
    bool ForceReplacementAuthorized = false,
    string? ReplacementExpectedPath = null,
    IReadOnlyList<DispatchReplacementTarget>? ReplacementTargets = null);

/// <summary>
/// One catalogue entity and the exact file it owned when acquisition was
/// authorized. Multi-file TV replacements persist one target per episode;
/// several episodes may legitimately name the same multi-episode file.
/// </summary>
public sealed record DispatchReplacementTarget(
    string EntityId,
    string ExpectedPath);
