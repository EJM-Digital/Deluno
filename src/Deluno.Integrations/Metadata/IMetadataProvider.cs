namespace Deluno.Integrations.Metadata;

public interface IMetadataProvider
{
    Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken);
}
