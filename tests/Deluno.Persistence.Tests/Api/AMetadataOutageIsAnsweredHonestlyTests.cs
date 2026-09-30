using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1090: the Add screen's search and the Check now button, against a metadata
/// service that is not answering. The provider is a stand-in that reports what
/// the real one reports; what is under test is what the endpoints do with it.
/// </summary>
public sealed class AMetadataOutageIsAnsweredHonestlyTests
{
    private static readonly IntegrationFailure Outage = IntegrationFailureFactory.FromLegacy(
        "metadata", "metadata:broker:x", "the metadata service", "metadata.broker.search", "connectivity", "The service returned HTTP 503.");

    private sealed class FakeScope(IntegrationFailure? failure) : IMetadataFailureScope
    {
        public IntegrationFailure? Failure => failure;
        public void Dispose() { }
    }

    private sealed class CountingReset : IMetadataServiceReset
    {
        public int Calls;
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class OutageProvider(IntegrationFailure? scopeFailure, MetadataProviderStatus? status = null) : IMetadataProvider
    {
        public int Cleared;
        public int Checked;

        public IMetadataFailureScope BeginFailureScope() => new FakeScope(scopeFailure);

        public Task ClearServiceTroubleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Cleared);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MetadataLookupRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(status ?? throw new NotSupportedException());

        public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Checked);
            return Task.FromResult(status ?? throw new NotSupportedException());
        }

        public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(string providerId, MetadataCatalogueSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataCollection?> GetMovieCollectionAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(string mediaType, int page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static OutageProvider Provider(IntegrationFailure? scopeFailure) => new(scopeFailure);

    private static Task<ApplicationTestHost> StartAsync(IMetadataProvider provider, CountingReset? reset = null)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton(provider);
            if (reset is not null)
            {
                services.RemoveAll<IMetadataServiceReset>();
                services.AddSingleton<IMetadataServiceReset>(reset);
            }
        });

    [Fact]
    public async Task A_search_during_an_outage_answers_a_typed_failure_not_an_empty_list()
    {
        await using var app = await StartAsync(Provider(Outage));

        var response = await app.Client.GetAsync("/api/metadata/search?mediaType=movies&query=inception");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("metadata-unavailable", body.GetProperty("error").GetString());
        Assert.Equal("The metadata service isn't answering; try again in a minute.", body.GetProperty("message").GetString());
        Assert.Equal("Unavailable", body.GetProperty("failure").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_rate_limited_search_says_the_service_is_busy()
    {
        var busy = IntegrationFailureFactory.FromLegacy(
            "metadata", "metadata:broker:x", "the metadata service", "metadata.broker.search", "rate-limited", "Busy.");
        await using var app = await StartAsync(Provider(busy));

        var response = await app.Client.GetAsync("/api/metadata/search?mediaType=movies&query=inception");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("The metadata service is busy; try again in a minute.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_search_the_service_answered_with_nothing_is_still_an_empty_list()
    {
        await using var app = await StartAsync(Provider(null));

        var response = await app.Client.GetAsync("/api/metadata/search?mediaType=movies&query=zzzzqqq");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Check_now_starts_clean_asks_the_service_for_real_and_reports_its_failure()
    {
        var status = new MetadataProviderStatus("deluno", true, "broker", "The metadata service isn't answering; try again in a minute.", [], Outage);
        var provider = new OutageProvider(null, status);
        await using var app = await StartAsync(provider);

        var response = await app.Client.PostAsJsonAsync("/api/metadata/test", new { query = "The Matrix", mediaType = "movies", year = 1999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Unavailable", body.GetProperty("failure").GetProperty("kind").GetString());
        Assert.Equal(1, provider.Cleared);
        Assert.Equal(1, provider.Checked);
    }

    [Fact]
    public async Task Saving_the_metadata_settings_resets_the_metadata_service()
    {
        var reset = new CountingReset();
        await using var app = await StartAsync(Provider(null), reset);

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new { metadataBrokerUrl = "https://metadata.example.test" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, reset.Calls);
    }

    [Fact]
    public async Task Saving_an_unrelated_setting_leaves_the_metadata_service_alone()
    {
        var reset = new CountingReset();
        await using var app = await StartAsync(Provider(null), reset);

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new { uiTheme = "dark" });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, reset.Calls);
    }
}
