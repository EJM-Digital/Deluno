namespace Deluno.Contracts;

/// <summary>
/// What the Dashboard's library panel needs from one media engine, in two cheap
/// reads: the same facet counts the catalogue shelf prints above its list, and
/// the bytes its tracked files take up on disk. Count-only: no rows travel.
/// </summary>
/// <param name="Facets">Every title counted under exactly one rung, as the shelf counts them.</param>
/// <param name="FileBytes">Sum of the tracked file sizes Deluno already stores.</param>
/// <param name="FileBytesByLibrary">The same sum per library id.</param>
/// <param name="FacetsByLibrary">The same facet counts the library's own page prints, per library id (#1103), for every library that holds a title.</param>
public sealed record MediaDashboardCounts(
    CatalogueFacets Facets,
    long FileBytes,
    IReadOnlyDictionary<string, long> FileBytesByLibrary,
    IReadOnlyDictionary<string, CatalogueFacets>? FacetsByLibrary = null);
