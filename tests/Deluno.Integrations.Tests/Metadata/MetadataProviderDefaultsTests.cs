using Deluno.Integrations.Metadata;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// A metadata provider that implements only what it must gets the interface's own answers for the rest: no failure
/// to report, nothing to clear, the plain episode orderings for any source, and an empty Discover list (#1429).
/// </summary>
public sealed class MetadataProviderDefaultsTests
{
    [Fact]
    public async Task A_provider_that_adds_nothing_reports_no_failure_clears_nothing_and_has_no_discover_lists()
    {
        IMetadataProvider provider = new BareProvider();

        using (var scope = provider.BeginFailureScope())
        {
            Assert.Null(scope.Failure);
        }
        await provider.ClearServiceTroubleAsync(CancellationToken.None);

        var lists = await provider.GetDiscoverListAsync("movies", new DiscoverListQuery(DiscoverListQuery.Trending, 1), CancellationToken.None);
        Assert.Empty(lists);
    }

    [Fact]
    public async Task The_episode_orderings_for_a_chosen_source_are_the_providers_plain_orderings_unless_it_knows_better()
    {
        var provider = new BareProvider();
        IMetadataProvider asInterface = provider;

        var groups = await asInterface.GetSeriesEpisodeGroupsAsync("tt0903747", MetadataCatalogueSource.Preferred, CancellationToken.None);

        Assert.Same(BareProvider.Orderings, groups);
        Assert.Equal("tt0903747", provider.AskedFor);
    }

    private sealed class BareProvider : IMetadataProvider
    {
        public static readonly IReadOnlyList<MetadataEpisodeGroup> Orderings = [];
        public string? AskedFor { get; private set; }

        public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(string providerId, CancellationToken cancellationToken)
        {
            AskedFor = providerId;
            return Task.FromResult(Orderings);
        }

        public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(string providerId, MetadataCatalogueSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataCollection?> GetMovieCollectionAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(string mediaType, int page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
