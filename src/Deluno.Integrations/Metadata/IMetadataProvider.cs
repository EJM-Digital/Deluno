namespace Deluno.Integrations.Metadata;

public interface IMetadataProvider
{
    Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Actually reaches out to whichever service is configured (the broker,
    /// then a direct-TMDb fallback in hybrid mode, or direct TMDb alone) and
    /// returns the status this produced. Unlike <see cref="GetStatusAsync"/>,
    /// which only reports configuration and whatever the last unrelated call
    /// happened to leave behind, this is the probe the health-check schedule
    /// calls every fifteen minutes so a metadata outage is caught even when
    /// nothing else has asked the provider anything in that window (#909).
    /// </summary>
    Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts watching the metadata calls made by the work that follows (until
    /// the scope is disposed) for the failure that made it come back empty, so
    /// "the service did not answer" is never mistaken for "nothing found"
    /// (#1090). Providers that cannot fail return a scope that never reports one.
    /// </summary>
    IMetadataFailureScope BeginFailureScope() => NoMetadataFailureScope.Instance;

    /// <summary>
    /// Forgets the last failure and closes the metadata circuits, so the next
    /// call goes out instead of waiting for a pause that may already be over.
    /// Check now and saving the metadata settings call this (#1090).
    /// </summary>
    Task ClearServiceTroubleAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an existing provider identity without falling back to a title
    /// search. Missing and temporarily unavailable are distinct outcomes.
    /// </summary>
    Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// The full season/episode catalogue for a series, from the provider rather
    /// than from disk, with the source chosen by the caller. Returns an empty
    /// list when the provider cannot answer. There is deliberately no call
    /// without a source: callers reach this only through
    /// <see cref="MetadataCatalogueChoice"/>, which holds a show back when
    /// TheTVDB does not answer or answers with another show's episodes. A show
    /// whose episodes already exist on TMDb's numbering asks for
    /// <see cref="MetadataCatalogueSource.TmdbOnly"/> when TheTVDB's aired
    /// numbering would not line up with them.
    /// </summary>
    Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
        string providerId,
        MetadataCatalogueSource source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every alternate episode ordering TMDb's episode_groups endpoint
    /// publishes for a series (#863) - aired order is not included, since it
    /// is simply <see cref="GetSeriesCatalogueAsync"/>'s own plain
    /// season/episode list. Returns an empty list when the provider cannot
    /// answer or the title has no episode groups at all.
    /// </summary>
    Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same orderings with the source chosen by the caller. Orderings are
    /// keyed by the aired numbering of the provider they came from, so a show
    /// still on TMDb's numbering asks for TMDb's orderings only.
    /// </summary>
    Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(
        string providerId,
        MetadataCatalogueSource source,
        CancellationToken cancellationToken)
        => GetSeriesEpisodeGroupsAsync(providerId, cancellationToken);

    /// <summary>
    /// When a movie reached cinemas, digital and physical release. Returns
    /// <see cref="MetadataReleaseDates.None"/> when the provider cannot answer.
    /// </summary>
    Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The complete TMDb collection a movie belongs to. The returned members
    /// include titles that are not in Deluno's catalogue yet, which is what
    /// lets a monitored collection discover sequels on the normal library
    /// automation cycle.
    /// </summary>
    Task<MetadataCollection?> GetMovieCollectionAsync(
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The provider's own "popular" list for a media kind — Discover's TMDb
    /// grid (#785). Returns an empty list rather than throwing when the
    /// provider cannot answer (broker-only mode with no popular route, or no
    /// TMDb key configured), the same rule every other lookup here follows.
    /// </summary>
    Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(
        string mediaType,
        int page,
        CancellationToken cancellationToken);

    /// <summary>
    /// Any of the provider's recommendation lists (trending, top rated, upcoming, now playing) or a filtered discover
    /// (#1429), for Discover's grid. Empty when the provider cannot answer. A provider that has none of these answers empty.
    /// </summary>
    Task<IReadOnlyList<MetadataSearchResult>> GetDiscoverListAsync(
        string mediaType,
        DiscoverListQuery query,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
}
