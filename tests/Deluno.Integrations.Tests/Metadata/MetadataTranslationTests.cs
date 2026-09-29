using System.Net;
using System.Text;
using Deluno.Contracts;
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
/// Localized title, overview and tagline (#1018): TheTVDB, then TMDb, then the
/// original. Each test uses ids of its own because the provider's in-memory
/// caches are shared across instances.
/// </summary>
public sealed class MetadataTranslationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-translation-tests", Guid.NewGuid().ToString("N"));

    private static MetadataSearchResult Result(string title = "The Matrix", string overview = "A hacker learns the truth.")
        => new("tmdb", "603", "movies", title, "The Matrix", 1999, overview, null, null, null, [], [], null, null,
            Tagline: "Welcome to the real world.", AlternateTitles: ["Matrix Reloaded Alias"]);

    private static readonly MetadataTranslation French = new("fr", "FR", "Matrice", "Un pirate apprend la verite.", "Bienvenue.");
    private static readonly MetadataTranslation Brazilian = new("pt", "BR", "Matrix BR", "Um hacker.", null);
    private static readonly MetadataTranslation Portuguese = new("pt", "PT", "Matrix PT", "Um pirata.", null);

    [Fact]
    public void The_title_is_kept_and_the_localized_name_is_carried_beside_it_and_remembered_for_matching()
    {
        var localized = TmdbMetadataProvider.ApplyTranslation(Result(), "fr", null, [French]);

        Assert.Equal("The Matrix", localized.Title);
        Assert.Equal("The Matrix", localized.OriginalTitle);
        Assert.Equal("Matrice", localized.LocalizedTitle);
        Assert.Equal("fr", localized.LocalizedLanguage);
        Assert.Equal("tmdb", localized.LocalizedSource);
        Assert.Equal("Un pirate apprend la verite.", localized.Overview);
        Assert.Equal("Bienvenue.", localized.Tagline);
        Assert.Contains("Matrice", localized.AlternateTitles!);
        Assert.Contains("Matrix Reloaded Alias", localized.AlternateTitles!);
    }

    [Fact]
    public void TheTVDB_answers_first_and_TMDb_fills_what_it_leaves_out_and_the_original_fills_the_rest()
    {
        var tmdb = new MetadataTranslation("fr", "FR", "Titre TMDb", "Resume TMDb.", "Accroche TMDb.");
        var both = TmdbMetadataProvider.ApplyTranslation(Result(), "fr", new TvdbTranslation("Titre TVDB", null), [tmdb]);
        Assert.Equal("Titre TVDB", both.LocalizedTitle);
        Assert.Equal("tvdb", both.LocalizedSource);
        Assert.Equal("Resume TMDb.", both.Overview);
        Assert.Equal("Accroche TMDb.", both.Tagline);

        // Neither has the language: the original text stays, untouched.
        var none = TmdbMetadataProvider.ApplyTranslation(Result(), "ja", null, [French]);
        Assert.Null(none.LocalizedTitle);
        Assert.Equal("A hacker learns the truth.", none.Overview);
        Assert.Equal("Welcome to the real world.", none.Tagline);
    }

    [Fact]
    public void A_regional_language_prefers_its_own_region_and_otherwise_any_region_of_the_language()
    {
        Assert.Equal("Matrix BR", TmdbMetadataProvider.PickTranslation([Portuguese, Brazilian], "pt-BR")!.Title);
        Assert.Equal("Matrix PT", TmdbMetadataProvider.PickTranslation([Portuguese, Brazilian], "pt-PT")!.Title);
        Assert.Equal("Matrix PT", TmdbMetadataProvider.PickTranslation([Portuguese], "pt-BR")!.Title);
        Assert.Null(TmdbMetadataProvider.PickTranslation([French], "pt"));
        Assert.Null(TmdbMetadataProvider.PickTranslation(null, "fr"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("fr", false)]
    [InlineData("pt-BR", false)]
    public void English_or_blank_is_the_unchanged_default(string? language, bool isDefault)
        => Assert.Equal(isDefault, TmdbMetadataProvider.IsDefaultLanguage(language));

    [Fact]
    public async Task A_movie_in_French_is_translated_from_TMDb_and_keeps_its_own_title()
    {
        var provider = await CreateProviderAsync("direct", string.Empty, "fr", request => Route(request, new()
        {
            ["/3/movie/88001"] = MovieDetail(88001),
            ["/3/movie/88001/translations"] = """
                { "translations": [
                  { "iso_3166_1": "FR", "iso_639_1": "fr", "data": { "title": "Le Film", "overview": "Un resume.", "tagline": "Une accroche." } },
                  { "iso_3166_1": "DE", "iso_639_1": "de", "data": { "title": "Der Film", "overview": "", "tagline": "" } }
                ] }
                """
        }));

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Film", "movies", 2001, "88001"), CancellationToken.None);

        var result = lookup.Result!;
        Assert.Equal("Some Film", result.Title);
        Assert.Equal("Le Film", result.LocalizedTitle);
        Assert.Equal("Un resume.", result.Overview);
        Assert.Equal("Une accroche.", result.Tagline);
        Assert.Equal("Some Film Original", result.OriginalTitle);
    }

    [Fact]
    public async Task English_asks_for_no_translation_at_all()
    {
        var paths = new List<string>();
        var provider = await CreateProviderAsync("direct", string.Empty, "en", request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Route(request, new() { ["/3/movie/88002"] = MovieDetail(88002) });
        });

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Film", "movies", 2001, "88002"), CancellationToken.None);

        Assert.Null(lookup.Result!.LocalizedTitle);
        Assert.DoesNotContain(paths, path => path.EndsWith("/translations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_title_with_no_translation_available_keeps_the_original_wording()
    {
        var provider = await CreateProviderAsync("direct", string.Empty, "fr", request => Route(request, new()
        {
            ["/3/movie/88003"] = MovieDetail(88003)
            // no /translations route: TMDb answers 404
        }));

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Film", "movies", 2001, "88003"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Null(lookup.Result!.LocalizedTitle);
        Assert.Equal("The original overview.", lookup.Result.Overview);
    }

    [Fact]
    public async Task Through_the_broker_the_gateways_compact_list_is_used()
    {
        var provider = await CreateProviderAsync("broker", "https://broker.example.test", "pt-BR", request => Route(request, new()
        {
            ["/metadata/search"] = $$"""{ "provider": "deluno-broker", "mode": "broker", "resultCount": 1, "results": [ {{Broker(88004)}} ] }""",
            ["/metadata/movie/88004/translations"] = """
                { "provider": "tmdb", "providerId": "88004", "translations": [
                  { "language": "pt", "region": "PT", "title": "Filme PT", "overview": "Sinopse PT.", "tagline": null },
                  { "language": "pt", "region": "BR", "title": "Filme BR", "overview": "Sinopse BR.", "tagline": "Frase BR." }
                ] }
                """
        }));

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Film", "movies", 2001, "88004"), CancellationToken.None);

        var result = lookup.Result!;
        Assert.Equal("Filme BR", result.LocalizedTitle);
        Assert.Equal("Sinopse BR.", result.Overview);
        Assert.Equal("Frase BR.", result.Tagline);
    }

    [Fact]
    public async Task A_show_falls_back_from_TheTVDB_to_TMDb_when_TheTVDB_has_no_translation()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            "fr",
            request => Route(request, new()
            {
                ["/3/tv/88005"] = """
                    { "id": 88005, "name": "Some Show", "original_name": "Some Show", "first_air_date": "2019-04-01",
                      "overview": "TMDb overview.", "status": "Returning Series",
                      "external_ids": { "imdb_id": "tt8800500", "tvdb_id": 88500 } }
                    """,
                ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                ["/v4/series/88500/extended"] = """{ "data": { "id": 88500, "name": "Some Show", "overview": "TheTVDB overview.", "year": "2019" } }""",
                ["/v4/series/88500/translations/eng"] = """{ "data": { "name": "Some Show", "overview": "TheTVDB overview." } }""",
                // no /v4/series/88500/translations/fra: TheTVDB has no French
                ["/3/tv/88005/translations"] = """{ "translations": [ { "iso_3166_1": "FR", "iso_639_1": "fr", "data": { "name": "Une serie", "overview": "Un resume TMDb." } } ] }"""
            }),
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") });

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Show", "tv", 2019, "88005"), CancellationToken.None);

        var result = lookup.Result!;
        Assert.Equal("Some Show", result.Title);
        Assert.Equal("Une serie", result.LocalizedTitle);
        Assert.Equal("tmdb", result.LocalizedSource);
        Assert.Equal("Un resume TMDb.", result.Overview);
    }

    [Fact]
    public async Task A_show_takes_its_title_from_TheTVDB_first_when_it_has_the_language()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            "fr",
            request => Route(request, new()
            {
                ["/3/tv/88006"] = """
                    { "id": 88006, "name": "Some Show", "original_name": "Some Show", "first_air_date": "2019-04-01",
                      "overview": "TMDb overview.", "status": "Returning Series",
                      "external_ids": { "imdb_id": "tt8800600", "tvdb_id": 88600 } }
                    """,
                ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                ["/v4/series/88600/extended"] = """{ "data": { "id": 88600, "name": "Some Show", "year": "2019" } }""",
                ["/v4/series/88600/translations/fra"] = """{ "data": { "name": "Serie TVDB", "overview": "Resume TVDB." } }""",
                ["/3/tv/88006/translations"] = """{ "translations": [ { "iso_3166_1": "FR", "iso_639_1": "fr", "data": { "name": "Serie TMDb", "overview": "Resume TMDb." } } ] }"""
            }),
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") });

        var lookup = await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Some Show", "tv", 2019, "88006"), CancellationToken.None);

        Assert.Equal("Serie TVDB", lookup.Result!.LocalizedTitle);
        Assert.Equal("tvdb", lookup.Result.LocalizedSource);
        Assert.Equal("Resume TVDB.", lookup.Result.Overview);
    }

    private static string MovieDetail(int id)
        => $$"""
            { "id": {{id}}, "title": "Some Film", "original_title": "Some Film Original", "release_date": "2001-05-01",
              "overview": "The original overview.", "tagline": "Original tagline.", "vote_average": 7.0,
              "genres": [ { "id": 18, "name": "Drama" } ], "external_ids": { "imdb_id": "tt{{id}}" } }
            """;

    private static string Broker(int id)
        => $$"""{ "provider": "tmdb", "providerId": "{{id}}", "mediaType": "movies", "title": "Some Film", "originalTitle": "Some Film Original", "year": 2001, "overview": "The original overview.", "ratings": [], "genres": [] }""";

    /// <summary>Answers by request path (TMDb, TheTVDB and gateway paths never collide in these tests).</summary>
    private static HttpResponseMessage Route(HttpRequestMessage request, Dictionary<string, string> routes)
    {
        var path = request.RequestUri!.AbsolutePath;
        var page = request.RequestUri.Query.Contains("page=", StringComparison.Ordinal) ? path : path;
        if (!routes.TryGetValue(page, out var body))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private async Task<TmdbMetadataProvider> CreateProviderAsync(
        string mode,
        string brokerUrl,
        string language,
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        Dictionary<string, string?>? extraConfig = null)
    {
        var configValues = extraConfig ?? new Dictionary<string, string?>();
        Directory.CreateDirectory(_dataRoot);
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(mode, brokerUrl, language));
        settings.Setup(repo => repo.GetMetadataProviderSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T00:00:00Z"));
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, timeProvider),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        if (!configValues.ContainsKey("Deluno:Metadata:TMDbApiKey"))
        {
            configValues["Deluno:Metadata:TMDbApiKey"] = "tmdb-key";
        }

        return new TmdbMetadataProvider(
            new HttpClient(new StubHandler(handler)),
            new ConfigurationBuilder().AddInMemoryCollection(configValues).Build(),
            settings.Object,
            factory,
            storageOptions,
            timeProvider,
            new PassthroughPolicy(),
            new OutboundRequestThrottle(timeProvider, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance);
    }

    private static PlatformSettingsSnapshot Snapshot(string mode, string brokerUrl, string language)
        => new(
            AppInstanceName: "Deluno Test",
            MovieRootPath: null,
            SeriesRootPath: null,
            DownloadsPath: null,
            IncompleteDownloadsPath: null,
            AutoStartJobs: true,
            EnableNotifications: true,
            RenameOnImport: true,
            UseHardlinks: false,
            CleanupEmptyFolders: true,
            MovieFolderFormat: "{Movie Title} ({Release Year})",
            SeriesFolderFormat: "{Series Title} ({Series Year})",
            MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
            EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            HostBindAddress: "127.0.0.1",
            HostPort: 5099,
            UrlBase: string.Empty,
            RequireAuthentication: true,
            UiTheme: "system",
            DefaultMovieView: "grid",
            DefaultShowView: "grid",
            MetadataCertificationCountry: "US",
            MetadataLanguage: language,
            MetadataProviderMode: mode,
            MetadataBrokerUrl: brokerUrl,
            MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(brokerUrl),
            MetadataTmdbApiKeyConfigured: false,
            MetadataOmdbApiKeyConfigured: false,
            ReleaseNeverGrabPatterns: string.Empty,
            SearchScoringMode: SearchScoringModes.Hybrid,
            ImportRecoveryRetentionDays: 30,
            UpdatedUtc: DateTimeOffset.UtcNow);

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
        catch
        {
            // best-effort cleanup of test temp dirs
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class PassthroughPolicy : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
        {
            var value = await operation(cancellationToken);
            _ = classifyResult(value);
            return new IntegrationResilienceResult<T>(value, false, false, 1, null, null);
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
