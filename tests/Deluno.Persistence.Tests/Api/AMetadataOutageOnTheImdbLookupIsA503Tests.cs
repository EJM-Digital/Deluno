using System.Net;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The find-by-IMDb-id address when the metadata provider is down (a TMDb outage): an honest 503 with a plain sentence,
/// never an unhandled 500 and never a 404 that reads as "no such film". Refs #1296.
/// </summary>
public sealed class AMetadataOutageOnTheImdbLookupIsA503Tests
{
    private sealed class FakeScope(IntegrationFailure? failure) : IMetadataFailureScope
    {
        public IntegrationFailure? Failure => failure;
        public void Dispose() { }
    }

    private sealed class DownProvider(bool throws, IntegrationFailure? scopeFailure) : IMetadataProvider
    {
        public IMetadataFailureScope BeginFailureScope() => new FakeScope(scopeFailure);
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MetadataLookupRequest request, CancellationToken cancellationToken)
            => throws ? throw new HttpRequestException("tmdb unreachable at a private host") : Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(string providerId, MetadataCatalogueSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataCollection?> GetMovieCollectionAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(string mediaType, int page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static Task<ApplicationTestHost> StartAsync(IMetadataProvider provider)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton(provider);
        });

    [Fact]
    public async Task A_provider_that_throws_answers_503_with_a_plain_sentence_and_no_detail()
    {
        await using var app = await StartAsync(new DownProvider(throws: true, scopeFailure: null));

        var response = await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=tt0133093");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("metadata-unavailable", body.GetProperty("error").GetString());
        Assert.Equal("The metadata service isn't answering; try again in a minute.", body.GetProperty("message").GetString());
        Assert.DoesNotContain("private host", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_answers_nothing_and_says_why_is_a_503_not_a_404()
    {
        var outage = IntegrationFailureFactory.FromLegacy(
            "metadata", "metadata:broker:x", "the metadata service", "metadata.broker.search", "connectivity", "The service returned HTTP 503.");
        await using var app = await StartAsync(new DownProvider(throws: false, scopeFailure: outage));

        var response = await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=tt0133093");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("metadata-unavailable", body.GetProperty("error").GetString());
        Assert.Equal("The metadata service isn't answering; try again in a minute.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_provider_that_answered_nothing_without_trouble_is_still_a_404()
    {
        await using var app = await StartAsync(new DownProvider(throws: false, scopeFailure: null));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/lookup/imdb?imdbId=tt0133093")).StatusCode);
    }
}
