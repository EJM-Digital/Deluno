using System.Net;
using System.Text;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// Low-severity rows (#1329) of what the metadata status says and where its settings come from. A status is worked out from the host
/// configuration, the saved settings and the stored legacy secrets and asks nobody; the broker address is seen in the search it makes.
/// </summary>
public sealed class MetadataStatusLowGapTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-status-low-tests", Guid.NewGuid().ToString("N"));

    private async Task<TmdbMetadataProvider> ProviderAsync(Dictionary<string, string?> host, string savedMode = "direct", string savedBrokerUrl = "", List<Uri>? asked = null)
    {
        Directory.CreateDirectory(_dataRoot);
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null, IncompleteDownloadsPath: null,
                AutoStartJobs: true, EnableNotifications: true, RenameOnImport: true, UseHardlinks: false,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1", HostPort: 5099, UrlBase: string.Empty,
                RequireAuthentication: true, UiTheme: "system", DefaultMovieView: "grid", DefaultShowView: "grid",
                MetadataCertificationCountry: "US", MetadataLanguage: "en", MetadataProviderMode: savedMode,
                MetadataBrokerUrl: savedBrokerUrl, MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(savedBrokerUrl),
                MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var clock = TimeProvider.System;
        await new CacheSchemaInitializer(factory, new SqliteDatabaseMigrator(factory, clock), NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var policy = new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance);
        return new TmdbMetadataProvider(
            new HttpClient(new Recording(asked)),
            new ConfigurationBuilder().AddInMemoryCollection(host).Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            policy,
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
    }

    /// <summary>Notes every address asked and answers a search with an empty list; a status must never get here.</summary>
    private sealed class Recording(List<Uri>? asked) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (asked is null)
            {
                throw new InvalidOperationException("A status is worked out from settings and must not ask anyone: " + request.RequestUri);
            }

            asked.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("Deluno:Metadata:BrokerUrl", "https://host-config.example.test/", "https://host-config.example.test/metadata/search")]
    [InlineData("DELUNO_METADATA_BROKER_URL", "https://host-env.example.test///", "https://host-env.example.test/metadata/search")]
    [InlineData("Deluno:Metadata:BrokerUrl", "https://gateway.example.test/metadata/broker/", "https://gateway.example.test/metadata/broker/search")]
    [InlineData("Deluno:Metadata:BrokerUrl", "https://gateway.example.test/api/metadata/broker", "https://gateway.example.test/api/metadata/broker/search")]
    public async Task The_broker_address_comes_from_the_host_configuration_before_the_saved_one_with_the_trailing_slash_trimmed_and_the_search_goes_to_its_search_address(string key, string address, string searchAddress)
    {
        var asked = new List<Uri>();
        var provider = await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker", [key] = address }, savedBrokerUrl: "https://saved.example.test", asked: asked);

        await provider.SearchAsync(new MetadataLookupRequest("Dune", "movies", 2021, null), CancellationToken.None);

        var request = Assert.Single(asked);
        Assert.Equal(searchAddress, request.GetLeftPart(UriPartial.Path));
        Assert.Equal("?mediaType=movies&query=Dune&year=2021", request.Query);
    }

    [Fact]
    public async Task A_blank_broker_address_in_the_host_configuration_falls_to_the_saved_one_and_with_neither_the_broker_is_not_configured()
    {
        var asked = new List<Uri>();
        var savedOnly = await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker", ["Deluno:Metadata:BrokerUrl"] = "  " }, savedBrokerUrl: "https://saved.example.test/", asked: asked);
        await savedOnly.SearchAsync(new MetadataLookupRequest("Dune", "movies", null, null), CancellationToken.None);
        Assert.Equal("https://saved.example.test/metadata/search", Assert.Single(asked).GetLeftPart(UriPartial.Path));

        var neither = await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker", ["Deluno:Metadata:BrokerUrl"] = "" }, savedBrokerUrl: "");
        var status = await neither.GetStatusAsync(CancellationToken.None);
        Assert.False(status.IsConfigured);
        Assert.Equal("unconfigured", status.Mode);
    }

    [Fact]
    public async Task In_broker_mode_the_status_is_ready_when_there_is_a_broker_address_and_unconfigured_when_there_is_not()
    {
        var ready = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker", ["Deluno:Metadata:BrokerUrl"] = "https://broker.example.test" })).GetStatusAsync(CancellationToken.None);
        Assert.True(ready.IsConfigured);
        Assert.Equal("broker", ready.Mode);
        Assert.Equal("Deluno's managed metadata service is ready for title matching.", ready.Message);

        var none = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker" })).GetStatusAsync(CancellationToken.None);
        Assert.False(none.IsConfigured);
        Assert.Equal("unconfigured", none.Mode);
        Assert.Equal("Title matching has not been configured for this Deluno installation yet.", none.Message);
    }

    [Fact]
    public async Task In_hybrid_mode_the_status_is_hybrid_with_a_broker_direct_fallback_with_only_a_key_and_unconfigured_with_neither()
    {
        var broker = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "hybrid", ["Deluno:Metadata:BrokerUrl"] = "https://broker.example.test" })).GetStatusAsync(CancellationToken.None);
        Assert.True(broker.IsConfigured);
        Assert.Equal("hybrid", broker.Mode);
        Assert.Equal("Deluno's managed metadata service is ready for title matching.", broker.Message);

        var keyOnly = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "hybrid", ["Deluno:Metadata:TMDbApiKey"] = "key-1" })).GetStatusAsync(CancellationToken.None);
        Assert.True(keyOnly.IsConfigured);
        Assert.Equal("direct-fallback", keyOnly.Mode);
        Assert.Equal("Deluno's metadata service is ready using this installation's configured fallback.", keyOnly.Message);

        var neither = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "hybrid" })).GetStatusAsync(CancellationToken.None);
        Assert.False(neither.IsConfigured);
        Assert.Equal("unconfigured", neither.Mode);
    }

    [Fact]
    public async Task In_direct_mode_the_status_is_ready_with_a_TMDb_key_and_says_so_with_or_without_OMDb_ratings_and_is_unconfigured_without_a_key()
    {
        var withKey = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "direct", ["Deluno:Metadata:TMDbApiKey"] = "key-1" })).GetStatusAsync(CancellationToken.None);
        Assert.True(withKey.IsConfigured);
        Assert.Equal("direct", withKey.Mode);
        Assert.Equal("Deluno's title-matching service is ready.", withKey.Message);

        var withRatings = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "direct", ["Deluno:Metadata:TMDbApiKey"] = "key-1", ["Deluno:Metadata:OMDbApiKey"] = "omdb-1" })).GetStatusAsync(CancellationToken.None);
        Assert.Equal("Deluno's title-matching and ratings service is ready.", withRatings.Message);

        var none = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "direct" })).GetStatusAsync(CancellationToken.None);
        Assert.False(none.IsConfigured);
        Assert.Equal("unconfigured", none.Mode);
        Assert.Equal("Title matching has not been configured for this Deluno installation yet.", none.Message);
    }

    [Fact]
    public async Task The_status_lists_the_broker_TMDb_OMDb_TheTVDB_and_Fanart_sources_each_with_a_plain_explanation()
    {
        var direct = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "direct", ["Deluno:Metadata:TMDbApiKey"] = "key-1", ["Deluno:Metadata:OMDbApiKey"] = "omdb-1" })).GetStatusAsync(CancellationToken.None);
        Assert.Equal(["broker", "tmdb", "omdb", "tvdb", "fanart"], direct.Sources.Select(source => source.Source).ToArray());
        Assert.All(direct.Sources, source => Assert.False(string.IsNullOrWhiteSpace(source.Message)));
        var tmdb = direct.Sources.Single(source => source.Source == "tmdb");
        Assert.Equal(("primary", true, "Direct TMDb key is stored and can resolve title search and artwork."), (tmdb.Mode, tmdb.IsConfigured, tmdb.Message));
        var omdb = direct.Sources.Single(source => source.Source == "omdb");
        Assert.Equal(("enrichment", true, "OMDb ratings enrichment is configured."), (omdb.Mode, omdb.IsConfigured, omdb.Message));
        var tvdb = direct.Sources.Single(source => source.Source == "tvdb");
        Assert.Equal(("unavailable", false, "No TheTVDB key is set, so TV titles use TMDb only."), (tvdb.Mode, tvdb.IsConfigured, tvdb.Message));
        var fanart = direct.Sources.Single(source => source.Source == "fanart");
        Assert.Equal(("planned", false), (fanart.Mode, fanart.IsConfigured));

        var viaBroker = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker", ["Deluno:Metadata:BrokerUrl"] = "https://broker.example.test" })).GetStatusAsync(CancellationToken.None);
        var brokerTvdb = viaBroker.Sources.Single(source => source.Source == "tvdb");
        Assert.Equal(("primary-tv", true), (brokerTvdb.Mode, brokerTvdb.IsConfigured));
        Assert.Contains("through Deluno's managed metadata service", brokerTvdb.Message);
        Assert.Equal("broker", viaBroker.Sources.Single(source => source.Source == "broker").Mode);
        Assert.Equal("fallback", viaBroker.Sources.Single(source => source.Source == "tmdb").Mode);
        Assert.False(viaBroker.Sources.Single(source => source.Source == "tmdb").IsConfigured);

        var localTvdb = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "direct", ["Deluno:Metadata:TMDbApiKey"] = "key-1", ["Deluno:Metadata:TvdbApiKey"] = "tv-key-1" })).GetStatusAsync(CancellationToken.None);
        var localTvdbSource = localTvdb.Sources.Single(source => source.Source == "tvdb");
        Assert.Equal(("primary-tv", true), (localTvdbSource.Mode, localTvdbSource.IsConfigured));
        Assert.StartsWith("A TheTVDB key is set on this installation;", localTvdbSource.Message);

        var noBroker = await (await ProviderAsync(new() { ["Deluno:Metadata:ProviderMode"] = "broker" })).GetStatusAsync(CancellationToken.None);
        var noBrokerSource = noBroker.Sources.Single(source => source.Source == "broker");
        Assert.False(noBrokerSource.IsConfigured);
        Assert.Equal("Not configured. Add a broker URL when hosted metadata is available.", noBrokerSource.Message);
    }

    public void Dispose()
    {
        new SqliteDatabaseConnectionFactory(Options.Create(new StoragePathOptions { DataRoot = _dataRoot })).ClearPools();
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
