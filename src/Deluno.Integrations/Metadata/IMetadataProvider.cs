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
    /// than from disk. Returns an empty list when the provider cannot answer.
    /// </summary>
    Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
        string providerId,
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
}
