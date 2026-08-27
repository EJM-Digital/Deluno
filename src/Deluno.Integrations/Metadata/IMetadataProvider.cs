namespace Deluno.Integrations.Metadata;

public interface IMetadataProvider
{
    Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
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
    /// When a movie reached cinemas, digital and physical release. Returns
    /// <see cref="MetadataReleaseDates.None"/> when the provider cannot answer.
    /// </summary>
    Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(
        string providerId,
        CancellationToken cancellationToken);
}
