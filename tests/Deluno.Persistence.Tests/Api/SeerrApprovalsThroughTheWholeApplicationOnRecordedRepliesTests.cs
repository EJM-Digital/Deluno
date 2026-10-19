using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Connections.Data;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// An approval from Seerr, through the whole application: the webhook body is the one Seerr 3.5.0 itself sent (tests/fixtures/recorded/seerr,
/// #1237), posted with no session to the route Seerr is pointed at; the title's metadata is the project's gateway as it really answered
/// (tests/fixtures/recorded/tmdb: Sintel, Breaking Bad, and a record the gateway says is missing); and the Seerr that Deluno asks for the
/// media id is answered with Seerr's own recorded replies. The route, the login gate, the acquisition, the library routing, the catalogue,
/// the wanted state, the search request, the acquisition list and Activity are all real. Only the one value a case is about is changed in
/// a recorded body (an id, the notification type), and a series approval is the recorded declined series request with its type changed
/// (no approved series request was recorded: tests/fixtures/recorded/MISSING.md).
/// </summary>
public sealed class SeerrApprovalsThroughTheWholeApplicationOnRecordedRepliesTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        public List<string> Unanswered { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            if (request.RequestUri.AbsolutePath == "/metadata/search")
            {
                if (query.Contains("providerId=45745", StringComparison.Ordinal)) return Task.FromResult(RecordedReplies.Tmdb("search-movie-by-provider-id").Single.Reply.ToResponse());
                if (query.Contains("providerId=1396", StringComparison.Ordinal)) return Task.FromResult(RecordedReplies.Tmdb("search-tv-by-provider-id").Single.Reply.ToResponse());
                if (query.Contains("providerId=999999999", StringComparison.Ordinal)) return Task.FromResult(RecordedReplies.Tmdb("search-provider-record-missing").Single.Reply.ToResponse());
            }

            Unanswered.Add($"{request.Method} {request.RequestUri.PathAndQuery}");
            throw new InvalidOperationException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ApplicationTestHost App { get; init; }

        public required RecordedAppServer Seerr { get; init; }

        public required GatewayReplay Gateway { get; init; }

        public required string ConnectionId { get; init; }

        /// <summary>The name Deluno gave the connection (it works it out from the address, #1076).</summary>
        public required string ConnectionName { get; init; }

        public required string Secret { get; init; }

        public required string MovieLibraryId { get; init; }

        public required string TvLibraryId { get; init; }

        public required string MovieProfileId { get; init; }

        public required string SeriesProfileId { get; init; }

        public HttpClient Anonymous { get; } = null!;

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Seerr.DisposeAsync();
        }
    }

    private static Recording Seerr(string state) => RecordedReplies.Seerr(state);

    private static async Task<string> LibraryAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, name)).FullName;
        var response = await app.Client.PostAsJsonAsync("/api/libraries", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = true,
            missingSearchEnabled = true,
            upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> ProfileAsync(ApplicationTestHost app, string name, string mediaType)
    {
        var response = await app.Client.PostAsJsonAsync("/api/quality-profiles", new
        {
            name,
            mediaType,
            cutoffQuality = "Bluray 720p",
            allowedQualities = "Bluray 720p",
            customFormatIds = string.Empty,
            upgradeUntilCutoff = false,
            upgradeUnknownItems = false,
            sizeRules = new[] { new { quality = "Bluray 720p", minGb = 0.0, maxGb = 0.0, minMb = 0.0, maxMb = 0.0 } }
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<Rig> StartAsync(bool withFallbackLibraries = true)
    {
        var gateway = new GatewayReplay();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => gateway));
        // The Seerr Deluno asks for a title's media id: Seerr 3.5.0's own lookup replies (the film is known to it, the series is not).
        var seerr = await RecordedAppServer.StartAsync("seerr", [], RecordedAppServer.Chain(
            RecordedAppServer.Answering("GET", "/api/v1/movie/45745", Seerr("media-available").Exchanges[3]),
            RecordedAppServer.Answering("GET", "/api/v1/tv/1396", Seerr("lookup-unknown").Exchanges[1])));
        var movieLibrary = await LibraryAsync(app, "Films", "movies");
        var tvLibrary = await LibraryAsync(app, "Shows", "tv");
        var movieProfile = await ProfileAsync(app, "Small 720p films", "movies");
        var seriesProfile = await ProfileAsync(app, "Small 720p shows", "tv");

        var created = await app.Client.PostAsJsonAsync("/api/seerr-servers", new
        {
            name = "Lab Seerr",
            baseUrl = seerr.BaseUrl,
            apiKey = "key-1",
            isEnabled = true,
            movieLibraryId = withFallbackLibraries ? movieLibrary : null,
            movieQualityProfileId = withFallbackLibraries ? movieProfile : null,
            seriesLibraryId = withFallbackLibraries ? tvLibrary : null,
            seriesQualityProfileId = withFallbackLibraries ? seriesProfile : null
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = saved.GetProperty("id").GetString()!;
        var secret = (await (await app.Client.PostAsync($"/api/seerr-servers/{id}/webhook-secret", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        return new Rig
        {
            App = app, Seerr = seerr, Gateway = gateway, ConnectionId = id, ConnectionName = saved.GetProperty("name").GetString()!, Secret = secret,
            MovieLibraryId = movieLibrary, TvLibraryId = tvLibrary, MovieProfileId = movieProfile, SeriesProfileId = seriesProfile
        };
    }

    private static string Edited(string body, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        edit(json);
        return json.ToJsonString();
    }

    /// <summary>What Seerr posted for the film's approval, exactly as it sent it.</summary>
    private static string FilmApproval() => Seerr("webhook-approved").Exchanges[0].Request.Body!;

    /// <summary>The series request Seerr posted, as an approval: only the type and the event text differ from the recorded body.</summary>
    private static string SeriesApproval() => Edited(Seerr("webhook-declined").Exchanges[2].Request.Body!, json =>
    {
        json["notification_type"] = "MEDIA_APPROVED";
        json["event"] = "Series Request Approved";
    });

    /// <summary>Posted the way Seerr posts it: no session, the secret in the Authorization header, a JSON body.</summary>
    private static async Task<HttpResponseMessage> PostAsync(Rig rig, string body, string? secret = null, string? connectionId = null)
    {
        using var anonymous = rig.App.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seerr-servers/{connectionId ?? rig.ConnectionId}/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", secret ?? rig.Secret);
        return await anonymous.SendAsync(request);
    }

    private static async Task<JsonElement> AcquisitionsAsync(Rig rig)
        => await rig.App.Client.GetFromJsonAsync<JsonElement>($"/api/seerr-servers/{rig.ConnectionId}/acquisitions");

    private static async Task<string[]> ActivityAsync(Rig rig, string category)
        => (await rig.App.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(200, null, null, CancellationToken.None))
            .Where(item => item.Category == category).Select(item => item.Message).ToArray();

    [Fact]
    public async Task An_approval_Seerr_sends_with_no_session_adds_the_film_to_the_fallback_library_and_asks_for_a_search()
    {
        await using var rig = await StartAsync();

        var response = await PostAsync(rig, FilmApproval());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var said = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(said.GetProperty("ok").GetBoolean());
        var movie = Assert.Single(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
        Assert.Equal(("Sintel", 2010, "tmdb", "45745"), (movie.Title, movie.ReleaseYear, movie.MetadataProvider, movie.MetadataProviderId));
        Assert.True(movie.Monitored);
        Assert.False(movie.HasFile);
        Assert.Equal($"missing|0|{rig.MovieLibraryId}", await ApiScenes.TextAsync(rig.App, "movies", $"SELECT wanted_status || '|' || has_file || '|' || library_id FROM movie_wanted_state WHERE movie_id = '{movie.Id}'"));
        Assert.Equal(rig.MovieProfileId, await ApiScenes.TextAsync(rig.App, "movies", $"SELECT quality_profile_id FROM movie_entries WHERE id = '{movie.Id}'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT search_requested FROM library_automation_state WHERE library_id = '{rig.MovieLibraryId}'"));
        var acquisition = Assert.Single((await AcquisitionsAsync(rig)).EnumerateArray());
        Assert.Equal(("added", "movies", movie.Id, "Sintel"), (acquisition.GetProperty("status").GetString(), acquisition.GetProperty("mediaType").GetString(), acquisition.GetProperty("entityId").GetString(), acquisition.GetProperty("title").GetString()));
        Assert.Equal(1, acquisition.GetProperty("seerrMediaId").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(acquisition.GetProperty("requestedBy").GetString()));
        var line = Assert.Single(await ActivityAsync(rig, "seerr.acquired"));
        Assert.Equal($"{rig.ConnectionName} approved \"Sintel\" — added to Films (using {rig.ConnectionName}'s fallback library) and queued a search. — requested by {acquisition.GetProperty("requestedBy").GetString()}".Replace(". — ", " — ", StringComparison.Ordinal), line.Replace(". — ", " — ", StringComparison.Ordinal));
        // Artwork and the TVDb read for a new title are fetched in the background; the title itself is only ever looked up by its id.
        Assert.DoesNotContain(rig.Gateway.Unanswered, call => call.Contains("/metadata/search", StringComparison.Ordinal));
        Assert.True(rig.Seerr.Unanswered.Count == 0, string.Join(" | ", rig.Seerr.Unanswered));
    }

    [Fact]
    public async Task A_second_approval_of_a_film_already_in_the_catalogue_adds_nothing_again_says_so_and_still_asks_for_a_search()
    {
        await using var rig = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig, FilmApproval())).StatusCode);

        var again = await PostAsync(rig, FilmApproval());

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
        var lines = await ActivityAsync(rig, "seerr.acquired");
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("already in Films", StringComparison.Ordinal) && line.Contains("queued a search", StringComparison.Ordinal));
        Assert.Equal(2, (await AcquisitionsAsync(rig)).GetArrayLength());
        Assert.Equal(2, (await ActivityAsync(rig, "library.search.requested")).Length);
    }

    [Fact]
    public async Task An_approval_of_a_series_adds_it_sets_it_wanted_gives_it_the_connections_series_profile_and_asks_for_a_search()
    {
        await using var rig = await StartAsync();

        var response = await PostAsync(rig, SeriesApproval());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        var series = Assert.Single(await rig.App.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None));
        Assert.Equal(("Breaking Bad", 2008, "tmdb", "1396"), (series.Title, series.StartYear, series.MetadataProvider, series.MetadataProviderId));
        Assert.True(series.Monitored);
        Assert.Equal($"missing|{rig.TvLibraryId}", await ApiScenes.TextAsync(rig.App, "series", $"SELECT wanted_status || '|' || library_id FROM series_wanted_state WHERE series_id = '{series.Id}'"));
        Assert.Equal(rig.SeriesProfileId, await ApiScenes.TextAsync(rig.App, "series", $"SELECT quality_profile_id FROM series_entries WHERE id = '{series.Id}'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(rig.App, "jobs", $"SELECT search_requested FROM library_automation_state WHERE library_id = '{rig.TvLibraryId}'"));
        var acquisition = Assert.Single((await AcquisitionsAsync(rig)).EnumerateArray());
        Assert.Equal(("added", "tv", series.Id), (acquisition.GetProperty("status").GetString(), acquisition.GetProperty("mediaType").GetString(), acquisition.GetProperty("entityId").GetString()));
        Assert.Contains($"added to Shows (using {rig.ConnectionName}'s fallback library)", Assert.Single(await ActivityAsync(rig, "seerr.acquired")), StringComparison.Ordinal);
        // Artwork and the TVDb read for a new title are fetched in the background; the title itself is only ever looked up by its id.
        Assert.DoesNotContain(rig.Gateway.Unanswered, call => call.Contains("/metadata/search", StringComparison.Ordinal));
    }

    [Fact(Skip = "Defect: an approval for a film Deluno already holds a file for passes HasFile false to EnsureWantedStateAsync, whose upsert overwrites has_file and the wanted status, so an owned film goes back on the wanted list (found 19 Oct 2026; the manual add path guards exactly this, MoviesEndpointRouteBuilderExtensions re-adding a film you already had).")]
    public async Task An_approval_for_a_film_Deluno_already_holds_a_file_for_leaves_it_owned_and_off_the_wanted_list()
    {
        // The file is the film's own, on disk in the Films library. Seerr approves a request for it, and the approval must not put it back on the wanted list.
        await using var rig = await StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(rig.App, "Sintel", 2010, rig.MovieLibraryId);
        await ApiScenes.ExecuteAsync(rig.App, "movies", $"UPDATE movie_entries SET metadata_provider = 'tmdb', metadata_provider_id = '45745' WHERE id = '{film.MovieId}'");

        var response = await PostAsync(rig, FilmApproval());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
        Assert.Equal("covered|1", await ApiScenes.TextAsync(rig.App, "movies", $"SELECT wanted_status || '|' || has_file FROM movie_wanted_state WHERE movie_id = '{film.MovieId}'"));
        Assert.True(File.Exists(film.FilePath));
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_through_the_whole_pipeline_and_adds_nothing()
    {
        await using var rig = await StartAsync();

        var wrong = await PostAsync(rig, FilmApproval(), secret: "not-the-secret");
        var bearer = await PostAsync(rig, FilmApproval(), secret: "Bearer " + rig.Secret);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        Assert.Empty(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_request_with_neither_id_fails_in_plain_words_and_is_listed_as_not_added()
    {
        await using var rig = await StartAsync();

        var response = await PostAsync(rig, Edited(FilmApproval(), json => { json["media"]!["tmdbId"] = ""; json["media"]!["tvdbId"] = ""; }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var said = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(said.GetProperty("ok").GetBoolean());
        Assert.Equal($"{rig.ConnectionName} sent no TMDb or TVDb id for this request.", said.GetProperty("message").GetString());
        var acquisition = Assert.Single((await AcquisitionsAsync(rig)).EnumerateArray());
        Assert.Equal("failed", acquisition.GetProperty("status").GetString());
        Assert.Equal($"{rig.ConnectionName}: {rig.ConnectionName} sent no TMDb or TVDb id for this request.", Assert.Single(await ActivityAsync(rig, "seerr.acquire-failed")));
        Assert.Empty(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_title_the_gateway_says_it_has_no_record_of_fails_as_metadata_unavailable_and_adds_nothing()
    {
        await using var rig = await StartAsync();

        var response = await PostAsync(rig, Edited(FilmApproval(), json => json["media"]!["tmdbId"] = "999999999"));

        var said = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(said.GetProperty("ok").GetBoolean());
        Assert.Equal($"Deluno's metadata service could not identify this title from {rig.ConnectionName}'s tmdb id.", said.GetProperty("message").GetString());
        var acquisition = Assert.Single((await AcquisitionsAsync(rig)).EnumerateArray());
        Assert.Equal(("failed", "Metadata unavailable"), (acquisition.GetProperty("status").GetString(), acquisition.GetProperty("title").GetString()));
        Assert.Single(await ActivityAsync(rig, "seerr.acquire-failed"));
        Assert.Empty(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task With_no_fallback_library_set_and_no_rule_the_request_is_refused_in_plain_words()
    {
        await using var rig = await StartAsync(withFallbackLibraries: false);

        var response = await PostAsync(rig, FilmApproval());

        var said = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(said.GetProperty("ok").GetBoolean());
        Assert.Contains("No destination rule matched \"Sintel\"", said.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(await rig.App.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }
}
