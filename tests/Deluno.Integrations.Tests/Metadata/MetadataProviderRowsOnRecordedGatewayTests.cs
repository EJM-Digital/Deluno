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
public sealed class MetadataProviderRowsOnRecordedGatewayTests : IDisposable
{
    private const string BrokerUrl = "https://metadata.deluno.test";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-metadata-rows-recorded-tests", Guid.NewGuid().ToString("N"));

    public MetadataProviderRowsOnRecordedGatewayTests() => Directory.CreateDirectory(_dataRoot);

    private Mock<IPlatformSettingsRepository>? LastSettings { get; set; }

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

    private static HttpResponseMessage DirectRefusal() => RecordedReplies.Tmdbapi("wrong-key").Exchanges[0].Reply.ToResponse();

    private static bool IsGateway(HttpRequestMessage request) => request.RequestUri!.Host == "metadata.deluno.test";

    private static bool IsTvdbApi(HttpRequestMessage request) => request.RequestUri!.Host == "api4.thetvdb.com";

    private static bool IsTmdbApi(HttpRequestMessage request) => request.RequestUri!.Host == "api.themoviedb.org";

    // ---- nothing configured: nothing asked, nothing thrown ----

    [Fact]
    public async Task With_no_TMDb_key_and_no_broker_every_lookup_is_empty_and_nothing_is_asked()
    {
        var asked = new List<string>();
        var rig = await RigAsync(request => { asked.Add(request.RequestUri!.ToString()); return Gateway(request); }, mode: "direct", broker: false);

        Assert.Empty(await rig.GetSeriesCatalogueAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None));
        Assert.Empty(await rig.GetSeriesEpisodeGroupsAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None));
        Assert.Equal(MetadataReleaseDates.None, await rig.GetMovieReleaseDatesAsync("603", CancellationToken.None));
        Assert.Null(await rig.GetMovieCollectionAsync("2344", CancellationToken.None));
        Assert.Empty(await rig.GetPopularAsync("movies", 1, CancellationToken.None));
        Assert.Empty(await rig.GetPopularAsync("tv", 1, CancellationToken.None));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Direct_mode_with_no_key_cannot_look_up_a_record_by_id_and_says_it_is_not_configured_without_asking_anyone()
    {
        var asked = 0;
        var rig = await RigAsync(request => { asked++; return Gateway(request); }, mode: "direct", broker: false);

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Unavailable, lookup.Status);
        Assert.Null(lookup.Result);
        Assert.Equal("TMDb is not configured for exact provider-id lookup.", lookup.Failure?.Message);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Direct_search_with_no_TMDb_and_no_OMDb_key_is_an_empty_list_and_asks_nobody()
    {
        var asked = 0;
        var rig = await RigAsync(request => { asked++; return Gateway(request); }, mode: "direct", broker: false);

        var results = await rig.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, asked);
    }

    // ---- hybrid: the broker's Found or Missing is final, only Unavailable reaches the local key ----

    [Fact]
    public async Task Hybrid_takes_a_broker_found_and_a_broker_missing_as_final_and_never_asks_TMDb()
    {
        var direct = 0;
        var rig = await RigAsync(request => { if (IsTmdbApi(request)) direct++; return Gateway(request); }, mode: "hybrid", tmdbKey: "local-key");

        var found = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);
        var missing = await rig.ResolveProviderRecordAsync(Lookup("Zzqxjv", "movies", null, "999999999"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, found.Status);
        Assert.Equal(MetadataProviderRecordStatus.Missing, missing.Status);
        Assert.Equal(0, direct);
    }

    [Fact]
    public async Task Hybrid_with_the_broker_down_asks_TMDb_for_the_exact_record_with_the_local_key()
    {
        var direct = new List<Uri>();
        var rig = await RigAsync(request =>
        {
            if (IsGateway(request)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (IsTmdbApi(request)) direct.Add(request.RequestUri!);
            return DirectRefusal();
        }, mode: "hybrid", tmdbKey: "local-key");

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);

        var asked = Assert.Single(direct, uri => uri.AbsolutePath.EndsWith("/movie/45745", StringComparison.Ordinal));
        Assert.Equal("api.themoviedb.org", asked.Host);
        Assert.Contains("api_key=local-key", asked.Query, StringComparison.Ordinal);
        // TMDb's own recorded refusal of a key it does not know is not a missing record.
        Assert.Equal(MetadataProviderRecordStatus.Unavailable, lookup.Status);
    }

    [Fact]
    public async Task Broker_mode_does_not_ask_TMDb_for_a_record_or_a_popular_list_even_when_the_install_holds_a_key_and_the_broker_is_down()
    {
        var direct = 0;
        var rig = await RigAsync(request =>
        {
            if (IsGateway(request)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (IsTmdbApi(request)) direct++;
            return DirectRefusal();
        }, mode: "broker", tmdbKey: "local-key");

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);
        var popular = await rig.GetPopularAsync("movies", 1, CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Unavailable, lookup.Status);
        Assert.Empty(popular);
        Assert.Equal(0, direct);
    }

    // ---- the search cache ----

    [Fact]
    public async Task A_repeat_search_is_served_from_the_cache_and_the_cache_is_per_language_and_ignores_the_case_of_the_query()
    {
        var calls = 0;
        var english = await RigAsync(request => { if (IsGateway(request)) calls++; return Gateway(request); }, language: "en");

        await english.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);
        var afterFirst = calls;
        await english.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);
        await english.SearchAsync(Lookup("SINTEL", "movies", 2010), CancellationToken.None);
        Assert.Equal(afterFirst, calls);

        var portuguese = await RigAsync(request => { if (IsGateway(request)) calls++; return Gateway(request); }, language: "pt-PT");
        await portuguese.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);
        Assert.True(calls > afterFirst, "another language does not share the English row");
    }

    // ---- an answer that is not JSON ----

    [Fact]
    public async Task A_gateway_answer_that_is_cut_short_or_is_a_web_page_is_no_result_and_never_an_exception()
    {
        var whole = R("search-movie").Single.Reply.Body!;
        var truncated = whole[..(whole.Length / 2)];
        foreach (var body in new[] { truncated, "<html><body>Bad gateway</body></html>" })
        {
            var rig = await RigAsync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

            Assert.Empty(await rig.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None));
            var lookup = await rig.ResolveProviderRecordAsync(Lookup("Sintel", "movies", 2010, "45745"), CancellationToken.None);
            Assert.Equal(MetadataProviderRecordStatus.Unavailable, lookup.Status);
            Assert.Empty(await rig.GetPopularAsync("movies", 1, CancellationToken.None));
            Assert.Empty(await rig.GetSeriesCatalogueAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None));
            Assert.Equal(MetadataReleaseDates.None, await rig.GetMovieReleaseDatesAsync("603", CancellationToken.None));
            Assert.Null(await rig.GetMovieCollectionAsync("2344", CancellationToken.None));
        }
    }

    // ---- the cross-language contract: the gateway's real JSON through the app's own reader ----

    [Theory]
    [InlineData("search-movie-by-provider-id", "movies", "45745")]
    [InlineData("search-tv-by-provider-id", "tv", "1396")]
    public async Task Every_field_the_gateway_sends_for_a_title_arrives_in_the_apps_result_under_the_same_name(string state, string mediaType, string providerId)
    {
        var sent = (System.Text.Json.Nodes.JsonObject)R(state).Single.Reply.Json()["results"]!.AsArray()[0]!;
        var rig = await RigAsync(Gateway);

        var lookup = await rig.ResolveProviderRecordAsync(Lookup("x", mediaType, null, providerId), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, lookup.Status);
        var read = lookup.Result!;
        var properties = typeof(MetadataSearchResult).GetProperties().ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in sent)
        {
            // A renamed field on either side shows here: the app has no property of the name the gateway sends.
            Assert.True(properties.ContainsKey(name), $"the gateway sends '{name}' and the app has no field of that name");
            var got = properties[name].GetValue(read);
            if (value is null || value is System.Text.Json.Nodes.JsonArray { Count: 0 }) continue;
            Assert.True(got is not null, $"the gateway sent a value for '{name}' and the app read none");
        }
        Assert.Equal(sent["voteCount"]!.GetValue<int>(), read.VoteCount);
        Assert.Equal(sent["popularity"]!.GetValue<double>(), read.Popularity!.Value, 4);
        Assert.Equal(sent["ratings"]!.AsArray()[0]!["voteCount"]!.GetValue<int>(), read.Ratings[0].VoteCount);
        Assert.All(sent["genres"]!.AsArray().Select(genre => genre!.ToString()), genre => Assert.Contains(genre, read.Genres));
    }

    [Fact]
    public async Task The_episode_groups_answer_is_read_with_every_group_and_episode_number_the_gateway_sent()
    {
        var sent = R("episode-groups-tv").Single.Reply.Json()["groups"]!.AsArray();
        var rig = await RigAsync(Gateway);

        var groups = await rig.GetSeriesEpisodeGroupsAsync("1396", MetadataCatalogueSource.TmdbOnly, CancellationToken.None);

        Assert.Equal(sent.Count, groups.Count);
        for (var index = 0; index < sent.Count; index++)
        {
            var from = sent[index]!;
            Assert.Equal((int)from["type"]!, groups[index].TmdbType);
            Assert.Equal((string)from["providerGroupId"]!, groups[index].ProviderGroupId);
            Assert.Equal((string)from["name"]!, groups[index].Name);
            var episodes = from["episodes"]!.AsArray();
            Assert.Equal(episodes.Count, groups[index].Episodes.Count);
            var last = episodes[^1]!;
            Assert.Equal((int)last["originalSeasonNumber"]!, groups[index].Episodes[^1].OriginalSeasonNumber);
            Assert.Equal((int)last["originalEpisodeNumber"]!, groups[index].Episodes[^1].OriginalEpisodeNumber);
            Assert.Equal((int)last["groupSeasonNumber"]!, groups[index].Episodes[^1].GroupSeasonNumber);
            Assert.Equal((int)last["groupEpisodeNumber"]!, groups[index].Episodes[^1].GroupEpisodeNumber);
        }
    }

    // ---- Discover ----

    [Fact]
    public async Task A_popular_page_is_clamped_to_1_to_500_and_costs_one_call_with_no_detail_fetch_per_title()
    {
        var asked = new List<Uri>();
        var rig = await RigAsync(request => { if (IsGateway(request)) asked.Add(request.RequestUri!); return Gateway(request); });

        var films = await rig.GetPopularAsync("movies", 9999, CancellationToken.None);
        await rig.GetPopularAsync("movies", -4, CancellationToken.None);

        Assert.NotEmpty(films);
        Assert.Equal(2, asked.Count);
        Assert.All(asked, uri => Assert.Equal("/metadata/movies/popular", uri.AbsolutePath));
        Assert.Contains("page=500", asked[0].Query, StringComparison.Ordinal);
        Assert.Contains("page=1", asked[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Popular_with_the_broker_down_is_empty_in_broker_mode_and_goes_to_TMDb_in_hybrid()
    {
        var direct = new List<Uri>();
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (IsGateway(request)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (IsTmdbApi(request)) direct.Add(request.RequestUri!);
            return DirectRefusal();
        }

        var broker = await RigAsync(Respond, mode: "broker", tmdbKey: "local-key");
        Assert.Empty(await broker.GetPopularAsync("tv", 1, CancellationToken.None));
        Assert.Empty(direct);

        var hybrid = await RigAsync(Respond, mode: "hybrid", tmdbKey: "local-key");
        Assert.Empty(await hybrid.GetPopularAsync("tv", 1, CancellationToken.None));
        var asked = Assert.Single(direct);
        Assert.Contains("/tv/popular", asked.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("api_key=local-key", asked.Query, StringComparison.Ordinal);
    }

    // ---- broker mode holds no direct key ----

    [Fact]
    public async Task Broker_mode_never_reads_a_direct_key_or_the_saved_secret_and_never_calls_TMDb_or_TheTVDB_whatever_the_install_holds()
    {
        var direct = new List<Uri>();
        var rig = await RigAsync(request =>
        {
            if (IsTmdbApi(request) || IsTvdbApi(request)) direct.Add(request.RequestUri!);
            return Gateway(request);
        }, mode: "broker", tmdbKey: "local-tmdb-key", tvdbKey: "local-tvdb-key");

        var films = await rig.SearchAsync(Lookup("Sintel", "movies", 2010), CancellationToken.None);
        var shows = await rig.SearchAsync(Lookup("Breaking Bad Broker Only", "tv"), CancellationToken.None);
        var record = await rig.ResolveProviderRecordAsync(Lookup("Breaking Bad", "tv", 2008, "1396"), CancellationToken.None);

        Assert.Equal("Sintel", Assert.Single(films).Title);
        Assert.NotEmpty(shows);
        Assert.Equal(MetadataProviderRecordStatus.Found, record.Status);
        Assert.Empty(direct);
        // The saved (legacy) secrets are not even read in broker mode, so a managed install can never end up using one.
        LastSettings!.Verify(repo => repo.GetMetadataProviderSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- the search cache: limit and lifetime ----

    [Fact]
    public async Task A_broker_answer_with_more_than_12_results_lists_12_and_the_answer_is_kept_for_12_hours_then_asked_again()
    {
        var calls = 0;
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-29T00:00:00Z"));
        // The recorded Sintel search, with its one result copied up to 13 (the only value changed): a gateway answering with more than Deluno shows.
        var thirteen = R("search-movie").Body("", ("+results", 13));
        var rig = await RigAsync(request =>
        {
            if (!IsGateway(request)) return Gateway(request);
            calls++;
            return RecordedReplies.Json(thirteen);
        }, clock: clock);

        var first = await rig.SearchAsync(Lookup("Sintel Twelve", "movies", 2010), CancellationToken.None);
        Assert.Equal(12, first.Count);
        var afterFirst = calls;

        clock.Advance(TimeSpan.FromHours(11));
        await rig.SearchAsync(Lookup("Sintel Twelve", "movies", 2010), CancellationToken.None);
        Assert.Equal(afterFirst, calls);

        clock.Advance(TimeSpan.FromHours(2));
        await rig.SearchAsync(Lookup("Sintel Twelve", "movies", 2010), CancellationToken.None);
        Assert.True(calls > afterFirst, "after 12 hours the broker is asked again");
    }

    // ---- TheTVDB beside TMDb ----

    [Theory]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    public async Task A_TheTVDB_failure_during_a_TV_search_or_a_record_lookup_shows_TMDbs_results_and_is_not_a_metadata_outage(string how)
    {
        var rig = await RigAsync(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/metadata/tvdb/", StringComparison.Ordinal))
            {
                return how == "unavailable"
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : throw new HttpRequestException("timed out", new TimeoutException());
            }

            return Gateway(request);
        });

        var shows = await rig.SearchAsync(Lookup("Breaking Bad TheTVDB " + how, "tv"), CancellationToken.None);
        var record = await rig.ResolveProviderRecordAsync(Lookup("Breaking Bad", "tv", 2008, "1396"), CancellationToken.None);

        Assert.Equal(5, shows.Count);
        Assert.Contains(shows, show => show.ProviderId == "1396" && show.Title == "Breaking Bad");
        Assert.Equal(MetadataProviderRecordStatus.Found, record.Status);
        Assert.Equal("Breaking Bad", record.Result!.Title);
        Assert.Null(rig.LastFailure);
    }

    [Fact]
    public async Task The_IMDb_id_TMDb_gave_finds_the_TheTVDB_id_when_TMDb_sent_none_and_the_pair_is_remembered_for_the_next_question()
    {
        var asked = new List<string>();
        var withoutTvdbId = R("search-tv-by-provider-id").Body("", ("results.0.tvDbId", RecordedReplies.JsonNull));
        var rig = await RigAsync(request =>
        {
            asked.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/metadata/search" ? RecordedReplies.Json(withoutTvdbId) : Gateway(request);
        });

        var record = await rig.ResolveProviderRecordAsync(Lookup("Breaking Bad", "tv", 2008, "1396"), CancellationToken.None);

        Assert.Equal(MetadataProviderRecordStatus.Found, record.Status);
        Assert.Contains("/metadata/tvdb/remote/tt0903747", asked);
        Assert.Equal("81189", record.Result!.TvDbId);
        Assert.Equal("tvdb", record.Result.DataSource);

        // The TMDb to TheTVDB pair is now remembered: the episode orders of the same show need no second remote-id lookup.
        asked.Clear();
        await rig.GetSeriesEpisodeGroupsAsync("1396", CancellationToken.None);
        Assert.DoesNotContain("/metadata/tvdb/remote/tt0903747", asked);
        Assert.Contains("/metadata/tvdb/tv/81189/episode-groups", asked);
    }

    [Fact]
    public async Task TheTVDB_is_asked_through_the_gateway_first_and_this_installs_key_answers_only_in_hybrid_when_the_gateway_cannot()
    {
        var direct = new List<Uri>();
        var gatewayTvdb = 0;

        // Broker mode, key on the install: the gateway answers; the key is never used.
        var broker = await RigAsync(request =>
        {
            if (IsTvdbApi(request)) direct.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.StartsWith("/metadata/tvdb/", StringComparison.Ordinal)) gatewayTvdb++;
            return Gateway(request);
        }, mode: "broker", tvdbKey: "local-tvdb-key");
        var viaGateway = await broker.SearchAsync(Lookup("Breaking Bad Via Gateway", "tv"), CancellationToken.None);
        Assert.NotEmpty(viaGateway);
        Assert.True(gatewayTvdb > 0, "TheTVDB is asked through the gateway first");
        Assert.Empty(direct);

        // Hybrid, the gateway cannot answer TheTVDB (503): this install's key is used, and TheTVDB's real refusal of an unknown key leaves TMDb's results.
        string? login = null;
        var hybrid = await RigAsync(async (request, token) =>
        {
            if (IsTvdbApi(request))
            {
                login = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
                return RecordedReplies.Tvdbapi("wrong-key").Exchanges[0].Reply.ToResponse();
            }

            return request.RequestUri!.AbsolutePath.StartsWith("/metadata/tvdb/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Gateway(request);
        }, mode: "hybrid", tvdbKey: "local-tvdb-key");
        var viaKey = await hybrid.SearchAsync(Lookup("Breaking Bad Via Key", "tv"), CancellationToken.None);

        Assert.NotNull(login);
        Assert.Contains("local-tvdb-key", login, StringComparison.Ordinal);
        Assert.Equal(5, viaKey.Count);
        Assert.Null(hybrid.LastFailure);
    }

    // ---- the rig ----

    private Task<TmdbMetadataProvider> RigAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, string language = "en", string mode = "broker", bool broker = true, string? tmdbKey = null, string? tvdbKey = null, TimeProvider? clock = null)
        => RigAsync((request, _) => Task.FromResult(respond(request)), language, mode, broker, tmdbKey, tvdbKey, clock);

    private async Task<TmdbMetadataProvider> RigAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string language = "en", string mode = "broker", bool broker = true, string? tmdbKey = null, string? tvdbKey = null, TimeProvider? clock = null)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        LastSettings = settings;
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
                MetadataCertificationCountry: "US", MetadataLanguage: language, MetadataProviderMode: mode,
                MetadataBrokerUrl: broker ? BrokerUrl : string.Empty, MetadataBrokerConfigured: broker,
                MetadataTmdbApiKeyConfigured: tmdbKey is not null, MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty, SearchScoringMode: SearchScoringModes.Hybrid,
                ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UtcNow));

        var storageOptions = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var factory = new SqliteDatabaseConnectionFactory(storageOptions);
        clock ??= TimeProvider.System;
        await new CacheSchemaInitializer(
            factory,
            new SqliteDatabaseMigrator(factory, clock),
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var policy = new IntegrationResiliencePolicy(clock, factory, NullLogger<IntegrationResiliencePolicy>.Instance);
        return new TmdbMetadataProvider(
            new HttpClient(new StubHandler(respond)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Metadata:ProviderMode"] = mode, ["Deluno:Metadata:TMDbApiKey"] = tmdbKey, ["Deluno:Metadata:TvdbApiKey"] = tvdbKey }).Build(),
            settings.Object,
            factory,
            storageOptions,
            clock,
            policy,
            new OutboundRequestThrottle(clock, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<TmdbMetadataProvider>.Instance,
            new MetadataServiceHealth(clock));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
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
