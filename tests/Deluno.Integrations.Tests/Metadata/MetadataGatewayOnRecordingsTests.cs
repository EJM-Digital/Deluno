using System.Net;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Metadata;

/// <summary>
/// Deluno's metadata provider in broker mode against the replies of the project's deployed metadata gateway (a Cloudflare Worker in
/// front of TMDb and TheTVDB, which holds the keys), recorded with real unauthenticated GETs (tests/fixtures/recorded/tmdb, #1237): title
/// search, the lookup of one exact record that a refresh and the metadata link preview use, the series catalogue, episode groups, release
/// dates, collections, translations, Discover's popular lists and the TheTVDB routes. A call is answered by the recorded reply to the same
/// address.
/// </summary>
public sealed class MetadataGatewayOnRecordingsTests : IDisposable
{
    private const string BrokerUrl = "https://metadata.deluno.test";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-gateway-recorded-tests", Guid.NewGuid().ToString("N"));

    public MetadataGatewayOnRecordingsTests() => Directory.CreateDirectory(_dataRoot);

    private static Recording R(string state) => RecordedReplies.Tmdb(state);

    private static HttpResponseMessage Reply(string state) => R(state).Single.Reply.ToResponse();

    /// <summary>The recorded reply to the kind of address Deluno asked, from the gateway's own routes.</summary>
    private static HttpResponseMessage Gateway(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        string? state = null;
        if (path == "/metadata/search")
        {
            var tv = query.Contains("mediaType=tv", StringComparison.Ordinal);
            var exact = query.Contains("providerId=", StringComparison.Ordinal);
            state = query.Contains("providerId=999999999", StringComparison.Ordinal) ? "search-provider-record-missing"
                : query.Contains("Zzqxjv", StringComparison.Ordinal) ? "search-no-results"
                : tv ? (exact ? "search-tv-by-provider-id" : "search-tv")
                : exact ? "search-movie-by-provider-id" : "search-movie";
        }
        else if (path == "/metadata/tv/1396/catalogue") state = "catalogue-tv";
        else if (path == "/metadata/tv/999999999/catalogue") state = "catalogue-tv-missing";
        else if (path == "/metadata/tv/1396/episode-groups") state = "episode-groups-tv";
        else if (path == "/metadata/movie/603/release-dates") state = "release-dates-movie";
        else if (path == "/metadata/movie/2344/collection") state = "collection-movie";
        else if (path == "/metadata/movie/999999999/collection") state = "collection-missing";
        else if (path == "/metadata/movie/45745/translations") state = "translations-movie";
        else if (path == "/metadata/tv/1396/translations") state = "translations-tv";
        else if (path == "/metadata/movies/popular") state = "popular-movies";
        else if (path == "/metadata/tv/popular") state = "popular-tv";
        else if (path == "/metadata/tvdb/search") state = "tvdb-search";
        else if (path == "/metadata/tvdb/tv/81189") state = "tvdb-series";
        else if (path == "/metadata/tvdb/tv/81189/catalogue") state = "tvdb-catalogue";
        else if (path == "/metadata/tvdb/tv/81189/episode-groups") state = "tvdb-episode-groups";
        else if (path == "/metadata/tvdb/tv/81189/translations/fra") state = "tvdb-translation";
        else if (path == "/metadata/tvdb/remote/tt0903747") state = "tvdb-remote";
        else if (path.StartsWith("/metadata/tvdb/tv/", StringComparison.Ordinal)) state = "tvdb-series-missing";
        return state is null ? throw new InvalidOperationException($"No gateway recording answers {request.Method} {path}{query}.") : Reply(state);
    }

    private static MetadataLookupRequest Lookup(string title, string mediaType = "movies", int? year = null, string? providerId = null)
        => new(title, mediaType, year, providerId);

    // ---- title search ----

    [Fact]
    public async Task A_film_searched_by_title_and_year_comes_back_with_its_ids_year_and_artwork()
    {
        var rig = await RigAsync(Gateway);

        var results = await rig.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);

        var film = Assert.Single(results);
        Assert.Equal("Sintel", film.Title);
        Assert.Equal("45745", film.ProviderId);
        Assert.Equal(2010, film.Year);
        Assert.False(string.IsNullOrWhiteSpace(film.Overview));
    }

    [Fact]
    public async Task A_series_searched_by_title_comes_back_with_its_matches_in_the_order_the_gateway_gave()
    {
        var rig = await RigAsync(Gateway);

        var results = await rig.SearchAsync(Lookup("Breaking Bad", "tv"), CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Contains(results, result => result.Title == "Breaking Bad" && result.ProviderId == "1396");
    }

    [Fact]
    public async Task A_title_nothing_matches_is_an_empty_list_and_not_a_failure()
    {
        var rig = await RigAsync(Gateway);

        using var scope = rig.BeginFailureScope();
        var results = await rig.SearchAsync(Lookup("Zzqxjv Nonexistent Title Qwerty"), CancellationToken.None);

        Assert.Empty(results);
        Assert.Null(scope.Failure);
        Assert.Equal(0, (int)R("search-no-results").Single.Reply.Json()["resultCount"]!);
    }

    [Fact]
    public void A_media_type_the_gateway_does_not_serve_is_refused_400_with_its_reason()
    {
        var refused = R("search-bad-media-type").Single.Reply;

        Assert.Equal(400, refused.Status);
        Assert.Equal("invalid_request", (string?)refused.Json()["error"]);
    }

    // ---- one exact record: refresh and the metadata link preview ----

    [Fact]
    public async Task One_exact_film_is_read_by_its_provider_id_the_way_a_refresh_and_the_link_preview_read_it()
    {
        var rig = await RigAsync(Gateway);

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Equal("45745", lookup.ProviderId);
        Assert.Equal("Sintel", lookup.Result!.Title);
        Assert.Equal("tt1727587", lookup.Result.ImdbId);
    }

    [Fact]
    public async Task A_provider_record_the_gateway_says_is_missing_is_missing_and_never_the_first_similar_title()
    {
        var rig = await RigAsync(Gateway);

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "999999999"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Missing, lookup.Status);
        Assert.Null(lookup.Result);
        Assert.Equal("provider_record_missing", (string?)R("search-provider-record-missing").Single.Reply.Json()["error"]);
    }

    [Fact]
    public async Task One_exact_series_is_read_by_its_provider_id_and_TheTVDB_adds_its_id()
    {
        var rig = await RigAsync(Gateway);

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Breaking Bad", "tv", null, "1396"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Equal("1396", lookup.Result!.ProviderId);
        Assert.Equal("81189", lookup.Result.TvDbId);
    }

    // ---- the series catalogue, episode groups, release dates, collections ----

    [Fact]
    public async Task The_catalogue_of_a_series_is_read_from_TheTVDB_first_and_from_TMDb_when_asked_for_TMDb()
    {
        var rig = await RigAsync(Gateway);

        var tvdb = await rig.GetSeriesCatalogueAsync("1396", MetadataCatalogueSource.Preferred, CancellationToken.None);
        var tmdb = await rig.GetSeriesCatalogueAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None);

        Assert.Equal(6, tvdb.Count);
        Assert.Equal(6, tmdb.Count);
        Assert.All(tvdb.Concat(tmdb), season => Assert.NotEmpty(season.Episodes));
        Assert.Equal(71, (int)R("catalogue-tv").Single.Reply.Json()["episodeCount"]!);
        Assert.Equal(80, (int)R("tvdb-catalogue").Single.Reply.Json()["episodeCount"]!);
    }

    [Fact]
    public async Task A_series_the_gateway_has_no_catalogue_for_has_no_seasons_not_a_failure()
    {
        var rig = await RigAsync(Gateway);

        using var scope = rig.BeginFailureScope();
        var seasons = await rig.GetSeriesCatalogueAsync("999999999", MetadataCatalogueSource.TmdbOnly, CancellationToken.None);

        Assert.Empty(seasons);
        Assert.Null(scope.Failure);
    }

    [Fact]
    public async Task The_alternate_episode_orderings_of_a_series_are_the_groups_TMDb_publishes()
    {
        var rig = await RigAsync(Gateway);

        var groups = await rig.GetSeriesEpisodeGroupsAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None);

        var dvd = Assert.Single(groups);
        Assert.Equal("DVD / PVOD", dvd.Name);
        Assert.NotEmpty(dvd.Episodes);
        Assert.Equal(["DVD order", "Absolute order"], R("tvdb-episode-groups").Single.Reply.Json()["groups"]!.AsArray().Select(group => group!["name"]!.ToString()));
    }

    [Fact]
    public async Task The_release_dates_of_a_film_are_in_cinemas_digital_and_physical()
    {
        var rig = await RigAsync(Gateway);

        var dates = await rig.GetMovieReleaseDatesAsync("603", CancellationToken.None);

        Assert.Equal(new DateOnly(1999, 3, 31), dates.InCinemas);
        Assert.Equal(new DateOnly(2016, 1, 7), dates.Digital);
        Assert.Equal(new DateOnly(1999, 11, 25), dates.Physical);
    }

    [Fact]
    public async Task A_collection_is_read_with_its_films_and_a_collection_TMDb_has_not_is_none()
    {
        var rig = await RigAsync(Gateway);

        var collection = await rig.GetMovieCollectionAsync("2344", CancellationToken.None);
        var none = await rig.GetMovieCollectionAsync("999999999", CancellationToken.None);

        Assert.Equal("The Matrix Collection", collection!.Name);
        Assert.NotEmpty(collection.Movies);
        Assert.Null(none);
        Assert.Equal(404, R("collection-missing").Single.Reply.Status);
    }

    [Fact]
    public async Task Discovers_popular_lists_are_read_for_films_and_series()
    {
        var rig = await RigAsync(Gateway);

        var films = await rig.GetPopularAsync("movies", 1, CancellationToken.None);
        var shows = await rig.GetPopularAsync("tv", 1, CancellationToken.None);

        Assert.NotEmpty(films);
        Assert.NotEmpty(shows);
        Assert.All(films, film => Assert.Equal("movies", film.MediaType));
        Assert.All(shows, show => Assert.Equal("tv", show.MediaType));
    }

    // ---- translations and the TheTVDB routes ----

    [Fact]
    public async Task A_film_searched_in_French_takes_its_overview_from_the_gateways_translation_when_it_has_one()
    {
        var rig = await RigAsync(Gateway, language: "fr");

        var film = Assert.Single(await rig.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None));

        var french = R("translations-movie").Single.Reply.Json()["translations"]!.AsArray().First(t => (string?)t!["language"] == "fr")!;
        Assert.Equal("Sintel", (string?)french["title"]);
        Assert.False(string.IsNullOrWhiteSpace(film.Title));
    }

    [Fact]
    public void The_gateway_gives_the_translation_of_a_series_for_a_language_TheTVDB_has_and_the_language_codes_are_three_letters()
    {
        var translation = R("tvdb-translation").Single.Reply.Json();

        Assert.Equal("fra", (string?)translation["language"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)translation["overview"]));
        Assert.Equal("provider-tmdb", "provider-" + (string?)R("translations-tv").Single.Reply.Json()["provider"]);
    }

    [Fact]
    public void TheTVDBs_routes_answer_a_search_a_series_a_catalogue_an_id_for_an_IMDb_id_and_a_404_for_an_unknown_series()
    {
        Assert.Equal("81189", (string?)R("tvdb-search").Single.Reply.Json()["results"]![0]!["tvdbId"]);
        Assert.Equal("1396", (string?)R("tvdb-series").Single.Reply.Json()["series"]!["tmdbId"]);
        Assert.Equal("81189", (string?)R("tvdb-remote").Single.Reply.Json()["tvdbId"]);
        Assert.Equal(404, R("tvdb-series-missing").Single.Reply.Status);
        Assert.Equal("not_found", (string?)R("tvdb-series-missing").Single.Reply.Json()["error"]);
    }

    // ---- the rig ----

    private async Task<TmdbMetadataProvider> RigAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, string language = "en")
    {
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
                MetadataCertificationCountry: "US", MetadataLanguage: language, MetadataProviderMode: "broker",
                MetadataBrokerUrl: BrokerUrl, MetadataBrokerConfigured: true,
                MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        var clock = TimeProvider.System;
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, clock),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var policy = new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance);
        return new TmdbMetadataProvider(
            new HttpClient(new StubHandler(respond)),
            new ConfigurationBuilder().Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            policy,
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
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
        catch
        {
            // best-effort cleanup for test temp dirs
        }
    }
}
