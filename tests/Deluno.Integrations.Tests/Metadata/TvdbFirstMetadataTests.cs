using System.Net;
using System.Text;
using System.Text.Json;
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
/// TheTVDB as the first source for TV (#989), with TMDb as the fallback field by
/// field. Every payload here is shaped like the real TheTVDB v4, TMDb and
/// gateway answers. Each test uses ids of its own because the provider's small
/// in-memory caches are shared across instances.
/// </summary>
public sealed class TvdbFirstMetadataTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-tvdb-tests", Guid.NewGuid().ToString("N"));

    private const string TmdbTvDetail = """
        {
          "id": 71001, "name": "Some Show (TMDb)", "original_name": "Some Show",
          "first_air_date": "2019-04-01", "overview": "TMDb overview.",
          "poster_path": "/tmdb-poster.jpg", "backdrop_path": "/tmdb-backdrop.jpg",
          "vote_average": 8.1, "vote_count": 500, "status": "Returning Series",
          "genres": [ { "id": 18, "name": "Drama" } ],
          "networks": [ { "name": "TMDb Network" } ],
          "seasons": [ { "season_number": 1, "name": "Season 1", "episode_count": 2 } ],
          "external_ids": { "imdb_id": "tt7100100", "tvdb_id": 81100 }
        }
        """;

    private const string TvdbSeriesExtended = """
        {
          "status": "success",
          "data": {
            "id": 81100, "name": "Some Show", "overview": "TheTVDB overview.",
            "image": "https://artworks.thetvdb.com/banners/posters/81100-1.jpg",
            "year": "2019", "averageRuntime": 45, "originalLanguage": "eng",
            "status": { "name": "Continuing" },
            "genres": [ { "name": "Drama" }, { "name": "Mystery" } ],
            "originalNetwork": { "name": "TheTVDB Network" },
            "remoteIds": [
              { "id": "tt7100100", "sourceName": "IMDB" },
              { "id": "71001", "sourceName": "TheMovieDB.com" }
            ],
            "aliases": [ { "language": "eng", "name": "Some Show Alias" } ],
            "artworks": [ { "type": 3, "image": "/banners/fanart/81100-1.jpg", "score": 5 } ]
          }
        }
        """;

    [Fact]
    public async Task A_shows_details_come_from_TheTVDB_first_and_keep_the_TMDb_identity()
    {
        var requests = new List<string>();
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") },
            request =>
            {
                requests.Add(request.RequestUri!.PathAndQuery);
                return Route(request, new()
                {
                    ["/3/tv/71001"] = TmdbTvDetail,
                    ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                    ["/v4/series/81100/extended"] = TvdbSeriesExtended,
                    ["/v4/series/81100/translations/eng"] = """{ "data": { "name": "Some Show", "overview": "English overview from TheTVDB.", "aliases": [ "Alt Name" ] } }"""
                });
            });

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("Some Show", "tv", 2019, "71001"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        var result = lookup.Result!;
        Assert.Equal("tmdb", result.Provider);
        Assert.Equal("71001", result.ProviderId);
        Assert.Equal("81100", result.TvDbId);
        Assert.Equal("Some Show", result.Title);
        Assert.Equal("English overview from TheTVDB.", result.Overview);
        Assert.Equal(["Drama", "Mystery"], result.Genres);
        Assert.Equal("TheTVDB Network", result.Network);
        // TheTVDB says Continuing; Deluno's vocabulary is TMDb's.
        Assert.Equal("Returning Series", result.Status);
        Assert.Equal(45, result.RuntimeMinutes);
        Assert.Contains("Alt Name", result.AlternateTitles!);
        Assert.Contains("Some Show (TMDb)", result.AlternateTitles!);
        // What TheTVDB has no equivalent for stays TMDb's.
        Assert.Equal(8.1, result.Rating);
        Assert.Equal("tt7100100", result.ImdbId);
        Assert.Contains(requests, path => path.StartsWith("/v4/login", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_field_TheTVDB_leaves_empty_falls_back_to_TMDb()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") },
            request => Route(request, new()
            {
                ["/3/tv/71002"] = TmdbTvDetail.Replace("71001", "71002").Replace("81100", "81102"),
                ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                // Title only: no overview, no genres, no network, no artwork.
                ["/v4/series/81102/extended"] = """{ "data": { "id": 81102, "name": "Sparse Show", "year": "2019" } }"""
            }));

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("Some Show", "tv", 2019, "71002"),
            CancellationToken.None);

        var result = lookup.Result!;
        Assert.Equal("Sparse Show", result.Title);
        Assert.Equal("TMDb overview.", result.Overview);
        Assert.Equal(["Drama"], result.Genres);
        Assert.Equal("TMDb Network", result.Network);
        Assert.Equal("Returning Series", result.Status);
    }

    [Fact]
    public async Task A_show_TheTVDB_does_not_have_is_described_by_TMDb_alone()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") },
            request => Route(request, new()
            {
                ["/3/tv/71003"] = TmdbTvDetail.Replace("71001", "71003").Replace("81100", "81103"),
                ["/v4/login"] = """{ "data": { "token": "t1" } }"""
                // /v4/series/81103/extended is a 404
            }));

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("Some Show", "tv", 2019, "71003"),
            CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        Assert.Equal("Some Show (TMDb)", lookup.Result!.Title);
        // The id TMDb reported is still kept, so the next refresh can fill in the rest.
        Assert.Equal("81103", lookup.Result.TvDbId);
    }

    [Fact]
    public async Task With_no_TheTVDB_key_a_TV_show_is_exactly_what_TMDb_says()
    {
        var requests = new List<string>();
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new(),
            request =>
            {
                requests.Add(request.RequestUri!.Host);
                return Route(request, new() { ["/3/tv/71004"] = TmdbTvDetail.Replace("71001", "71004") });
            });

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("Some Show", "tv", 2019, "71004"),
            CancellationToken.None);

        Assert.Equal("Some Show (TMDb)", lookup.Result!.Title);
        Assert.DoesNotContain(requests, host => host.Contains("thetvdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Movies_never_ask_TheTVDB()
    {
        var requests = new List<string>();
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") },
            request =>
            {
                requests.Add(request.RequestUri!.Host);
                return Route(request, new()
                {
                    ["/3/movie/603"] = """{ "id": 603, "title": "The Matrix", "release_date": "1999-03-30", "external_ids": { "imdb_id": "tt0133093" } }"""
                });
            });

        var lookup = await provider.ResolveProviderRecordAsync(
            new MetadataLookupRequest("The Matrix", "movies", 1999, "603"),
            CancellationToken.None);

        Assert.Equal("The Matrix", lookup.Result!.Title);
        Assert.DoesNotContain(requests, host => host.Contains("thetvdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_subscriber_PIN_is_sent_only_when_one_is_configured()
    {
        var bodies = new List<string>();
        Func<Dictionary<string, string?>, int, Task> run = async (config, n) =>
        {
            var provider = await CreateProviderAsync(
                "direct",
                string.Empty,
                config,
                request =>
                {
                    if (request.RequestUri!.AbsolutePath == "/v4/login")
                    {
                        bodies.Add(request.Content!.ReadAsStringAsync().Result);
                    }

                    return Route(request, new()
                    {
                        [$"/3/tv/{71050 + n}"] = TmdbTvDetail.Replace("71001", (71050 + n).ToString()).Replace("81100", (81150 + n).ToString()),
                        ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                        [$"/v4/series/{81150 + n}/extended"] = $$"""{ "data": { "id": {{81150 + n}}, "name": "Pin Show" } }"""
                    });
                });
            await provider.ResolveProviderRecordAsync(new MetadataLookupRequest("Pin", "tv", 2019, (71050 + n).ToString()), CancellationToken.None);
        };

        await run(new() { ["TVDB_API_KEY"] = "pin-key-a" }, 0);
        await run(new() { ["TVDB_API_KEY"] = "pin-key-b", ["TVDB_PIN"] = "1234ABCD" }, 1);

        using var withoutPin = JsonDocument.Parse(bodies[0]);
        Assert.False(withoutPin.RootElement.TryGetProperty("pin", out _));
        using var withPin = JsonDocument.Parse(bodies[1]);
        Assert.Equal("1234ABCD", withPin.RootElement.GetProperty("pin").GetString());
    }

    [Fact]
    public async Task The_catalogue_is_TheTVDBs_aired_list_with_absolute_numbers_and_TMDb_fills_the_blanks()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N") },
            request => Route(request, new()
            {
                ["/3/tv/71006"] = TmdbTvDetail.Replace("71001", "71006").Replace("81100", "81106"),
                ["/3/tv/71006/season/1"] = """
                    { "episodes": [
                      { "episode_number": 1, "name": "TMDb One", "overview": "TMDb overview one", "air_date": "2019-04-01" },
                      { "episode_number": 2, "name": "TMDb Two", "overview": "TMDb overview two", "air_date": "2019-04-08" } ] }
                    """,
                ["/v4/login"] = """{ "data": { "token": "t1" } }""",
                ["/v4/series/81106/episodes/official/eng"] = """
                    { "data": { "episodes": [
                        { "id": 1, "seasonNumber": 1, "number": 1, "absoluteNumber": 1, "name": "TVDB One", "overview": "TVDB overview one", "aired": "2019-04-01" },
                        { "id": 2, "seasonNumber": 1, "number": 2, "absoluteNumber": 2, "name": "TVDB Two", "aired": "2019-04-08" } ] },
                      "links": { "next": null } }
                    """
            }));

        var seasons = await provider.GetSeriesCatalogueAsync("71006", CancellationToken.None);

        var season = Assert.Single(seasons);
        var one = season.Episodes[0];
        var two = season.Episodes[1];
        Assert.Equal("TVDB One", one.Title);
        Assert.Equal(1, one.AbsoluteNumber);
        Assert.Equal("tvdb", one.Source);
        Assert.Equal("TVDB Two", two.Title);
        Assert.Equal(2, two.AbsoluteNumber);
        Assert.Equal("TMDb overview two", two.Overview);
    }

    [Fact]
    public async Task The_catalogue_falls_back_to_TMDb_when_TheTVDB_has_no_episodes()
    {
        var provider = await CreateProviderAsync(
            "direct",
            string.Empty,
            new() { ["TVDB_API_KEY"] = "placeholder-tvdb-key-" + Guid.NewGuid().ToString("N"), ["Deluno:Metadata:TMDbApiKey"] = "tmdb-key" },
            request => Route(request, new()
            {
                ["/3/tv/71007"] = TmdbTvDetail.Replace("71001", "71007").Replace("81100", "81107"),
                ["/3/tv/71007/external_ids"] = """{ "imdb_id": "tt7100700" }""",
                ["/3/tv/71007/season/1"] = """{ "episodes": [ { "episode_number": 1, "name": "TMDb One", "air_date": "2019-04-01" } ] }""",
                ["/v4/login"] = """{ "data": { "token": "t1" } }"""
            }));

        var seasons = await provider.GetSeriesCatalogueAsync("71007", CancellationToken.None);

        var episode = Assert.Single(Assert.Single(seasons).Episodes);
        Assert.Equal("TMDb One", episode.Title);
        Assert.Null(episode.Source);
        Assert.Null(episode.AbsoluteNumber);
    }

    [Fact]
    public async Task Through_the_broker_a_TV_search_lists_TheTVDBs_hits_first_and_keeps_TMDb_ids()
    {
        var provider = await CreateProviderAsync(
            "broker",
            "https://gateway.test",
            new(),
            request => Route(request, new()
            {
                ["/metadata/tvdb/search"] = """
                    { "provider": "tvdb", "results": [
                        { "tvdbId": "81200", "tmdbId": "72002", "title": "Second On TMDb", "year": 2020, "overview": "From TheTVDB", "network": "Net", "genres": ["Drama"] },
                        { "tvdbId": "81201", "tmdbId": null, "title": "Has No TMDb Id", "year": 2020 } ] }
                    """,
                ["/metadata/search"] = """
                    { "provider": "deluno-broker", "mode": "broker", "resultCount": 2, "results": [
                        { "provider": "tmdb", "providerId": "72001", "mediaType": "tv", "title": "First On TMDb", "year": 2020, "ratings": [], "genres": [] },
                        { "provider": "tmdb", "providerId": "72002", "mediaType": "tv", "title": "Second On TMDb", "year": 2020, "ratings": [], "genres": [], "overview": "From TMDb", "rating": 7.5 } ] }
                    """
            }));

        var results = await provider.SearchAsync(new MetadataLookupRequest("Second On TMDb", "tv", null, null), CancellationToken.None);

        Assert.Equal(["72002", "72001"], results.Select(result => result.ProviderId));
        Assert.All(results, result => Assert.Equal("tmdb", result.Provider));
        Assert.Equal("From TheTVDB", results[0].Overview);
        Assert.Equal("81200", results[0].TvDbId);
        Assert.Equal("tvdb", results[0].DataSource);
        Assert.Null(results[1].DataSource);
        Assert.Equal(7.5, results[0].Rating);
        Assert.Null(results[1].TvDbId);
    }

    [Fact]
    public async Task A_gateway_without_a_TheTVDB_key_leaves_TV_search_on_TMDb_and_does_not_report_an_outage()
    {
        var provider = await CreateProviderAsync(
            "broker",
            "https://gateway.test",
            new(),
            request => request.RequestUri!.AbsolutePath == "/metadata/tvdb/search"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Route(request, new()
                {
                    ["/metadata/search"] = """
                        { "provider": "deluno-broker", "mode": "broker", "resultCount": 1, "results": [
                            { "provider": "tmdb", "providerId": "73001", "mediaType": "tv", "title": "Only On TMDb", "year": 2021, "ratings": [], "genres": [] } ] }
                        """
                }));

        var results = await provider.SearchAsync(new MetadataLookupRequest("Only On TMDb", "tv", null, null), CancellationToken.None);

        Assert.Equal("73001", Assert.Single(results).ProviderId);
        Assert.Null(provider.LastFailure);
    }

    [Fact]
    public async Task Through_the_broker_the_catalogue_and_episode_orders_are_TheTVDBs()
    {
        var provider = await CreateProviderAsync(
            "broker",
            "https://gateway.test",
            new(),
            request => Route(request, new()
            {
                ["/metadata/search"] = """
                    { "provider": "deluno-broker", "mode": "broker", "resultCount": 1, "results": [
                        { "provider": "tmdb", "providerId": "74001", "mediaType": "tv", "title": "Anime", "year": 2022, "ratings": [], "genres": [], "tvDbId": "84001" } ] }
                    """,
                ["/metadata/tvdb/tv/84001/catalogue"] = """
                    { "provider": "tvdb", "seasons": [ { "seasonNumber": 1, "episodes": [
                        { "episodeNumber": 1, "title": "One", "overview": "o", "airDate": "2022-01-01", "absoluteNumber": 25 } ] } ] }
                    """,
                ["/metadata/tvdb/tv/84001/episode-groups"] = """
                    { "provider": "tvdb", "groups": [ { "type": 2, "providerGroupId": "tvdb:84001:absolute", "name": "Absolute order",
                        "episodes": [ { "originalSeasonNumber": 1, "originalEpisodeNumber": 1, "groupSeasonNumber": 1, "groupEpisodeNumber": 25 } ] } ] }
                    """
            }));

        var seasons = await provider.GetSeriesCatalogueAsync("74001", CancellationToken.None);
        var groups = await provider.GetSeriesEpisodeGroupsAsync("74001", CancellationToken.None);

        var episode = Assert.Single(Assert.Single(seasons).Episodes);
        Assert.Equal(25, episode.AbsoluteNumber);
        Assert.Equal("tvdb", episode.Source);
        var group = Assert.Single(groups);
        Assert.Equal(TmdbEpisodeGroupTypes.Absolute, group.TmdbType);
    }

    [Fact]
    public async Task A_show_TheTVDB_would_renumber_stays_on_TMDbs_numbering()
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season(("tvdb", 1, 1), ("tvdb", 1, 2))]);
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.TmdbOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season((null, 1, 1), (null, 1, 2), (null, 1, 3))]);

        // An episode already imported as S01E03 has no place in TheTVDB's list.
        var stranded = await MetadataCatalogueChoice.GetAsync(
            provider.Object,
            "1",
            [new(1, 1, true), new(1, 3, true)],
            CancellationToken.None);
        Assert.Equal("tmdb", MetadataCatalogueChoice.SourceName(stranded));

        // When every existing episode is present, TheTVDB's numbering is used.
        var aligned = await MetadataCatalogueChoice.GetAsync(
            provider.Object,
            "1",
            [new(1, 1, true), new(1, 2, false)],
            CancellationToken.None);
        Assert.Equal("tvdb", MetadataCatalogueChoice.SourceName(aligned));

        // A brand-new show has nothing to strand.
        var fresh = await MetadataCatalogueChoice.GetAsync(provider.Object, "1", [], CancellationToken.None);
        Assert.Equal("tvdb", MetadataCatalogueChoice.SourceName(fresh));
    }

    [Fact]
    public async Task A_TheTVDB_numbered_show_keeps_its_episodes_while_TheTVDB_is_unreachable()
    {
        var provider = new Mock<IMetadataProvider>();
        // TheTVDB is down, so "Preferred" quietly comes back as TMDb's list with extra episodes.
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season((null, 1, 1), (null, 1, 2), (null, 1, 3))]);

        var choice = await MetadataCatalogueChoice.ChooseAsync(
            provider.Object,
            "1",
            [new(1, 1, true, "One", null, "tvdb"), new(1, 2, false, "Two", null, "tvdb")],
            CancellationToken.None);

        Assert.Equal(MetadataCatalogueHold.TvdbUnavailable, choice.Hold);
        Assert.Empty(choice.Seasons);
    }

    [Fact]
    public async Task A_TMDb_numbered_show_still_refreshes_from_TMDb_when_TheTVDB_is_unreachable()
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season((null, 1, 1), (null, 1, 2))]);

        var choice = await MetadataCatalogueChoice.ChooseAsync(
            provider.Object,
            "1",
            [new(1, 1, true, "One", null, "tmdb")],
            CancellationToken.None);

        Assert.Equal(MetadataCatalogueHold.None, choice.Hold);
        Assert.Equal(2, choice.Seasons.Sum(season => season.Episodes.Count));
    }

    [Fact]
    public async Task A_wrong_TheTVDB_id_with_the_same_numbers_but_other_episodes_is_rejected()
    {
        var air = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var wrong = new MetadataSeason(1, null, 4, null,
        [
            new(1, 1, "Something Else", null, air.AddYears(5), null, "tvdb"),
            new(1, 2, "Other Show", null, air.AddYears(5).AddDays(7), null, "tvdb"),
            new(1, 3, "Unrelated", null, air.AddYears(5).AddDays(14), null, "tvdb"),
            new(1, 4, "Nope", null, air.AddYears(5).AddDays(21), null, "tvdb")
        ]);
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync([wrong]);
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.TmdbOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Season((null, 1, 1), (null, 1, 2), (null, 1, 3))]);
        MetadataEpisodeIdentity[] Existing(string source) =>
        [
            new(1, 1, true, "Pilot", air, source),
            new(1, 2, true, "Second", air.AddDays(7), source),
            new(1, 3, false, "Third", air.AddDays(14), source)
        ];

        // Already TheTVDB-numbered: kept, nothing synced.
        var held = await MetadataCatalogueChoice.ChooseAsync(provider.Object, "1", Existing("tvdb"), CancellationToken.None);
        Assert.Equal(MetadataCatalogueHold.TvdbDisagrees, held.Hold);
        Assert.Empty(held.Seasons);

        // TMDb-numbered: stays on TMDb's own list.
        var stays = await MetadataCatalogueChoice.ChooseAsync(provider.Object, "1", Existing("tmdb"), CancellationToken.None);
        Assert.Equal("tmdb", MetadataCatalogueChoice.SourceName(stays.Seasons));
    }

    [Fact]
    public async Task The_right_TheTVDB_id_is_accepted_even_when_titles_and_dates_are_a_little_different()
    {
        var air = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var right = new MetadataSeason(1, null, 3, null,
        [
            new(1, 1, "Pilot", null, air.AddDays(1), null, "tvdb"),
            new(1, 2, "The Second One", null, air.AddDays(7), null, "tvdb"),
            new(1, 3, "Third", null, air.AddDays(14), null, "tvdb")
        ]);
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesCatalogueAsync("1", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync([right]);

        var choice = await MetadataCatalogueChoice.ChooseAsync(
            provider.Object,
            "1",
            [
                new(1, 1, true, "Pilot", air, "tvdb"),
                new(1, 2, true, "Second", air.AddDays(7), "tvdb"),
                new(1, 3, false, "Third", air.AddDays(14), "tvdb")
            ],
            CancellationToken.None);

        Assert.Equal(MetadataCatalogueHold.None, choice.Hold);
        Assert.Equal("tvdb", MetadataCatalogueChoice.SourceName(choice.Seasons));
    }

    private static MetadataSeason Season(params (string? Source, int Season, int Episode)[] episodes)
        => new(
            1,
            null,
            episodes.Length,
            null,
            episodes.Select(entry => new MetadataEpisode(entry.Season, entry.Episode, null, null, null, null, entry.Source)).ToArray());

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
        Dictionary<string, string?> configValues,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        Directory.CreateDirectory(_dataRoot);
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(mode, brokerUrl));
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

    private static PlatformSettingsSnapshot Snapshot(string mode, string brokerUrl)
        => new(
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
            MetadataLanguage: "en",
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
