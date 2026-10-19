using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Refresh metadata" on one film and one show, through the real metadata provider reading the project's metadata gateway as it really
/// answered (tests/fixtures/recorded/tmdb: the exact-record lookups of Sintel and Breaking Bad, the series catalogue, and a record the
/// gateway says is gone). What the route writes is read back from the catalogue and from Activity; the files on disk are never touched.
/// </summary>
public sealed class MetadataRefreshOnTheRecordedGatewayTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Asked.Add(path + query);
            if (path == "/metadata/search" && query.Contains("providerId=45745", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-movie-by-provider-id").Single.Reply.ToResponse());
            if (path == "/metadata/search" && query.Contains("providerId=999999999", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-provider-record-missing").Single.Reply.ToResponse());
            if (path == "/metadata/search" && query.Contains("providerId=1396", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-tv-by-provider-id").Single.Reply.ToResponse());
            if (path == "/metadata/tv/1396/catalogue")
                return Task.FromResult(RecordedReplies.Tmdb("catalogue-tv").Single.Reply.ToResponse());
            if (path == "/metadata/tv/1396/episode-groups")
                return Task.FromResult(RecordedReplies.Tmdb("episode-groups-tv").Single.Reply.ToResponse());
            // What the gateway was never recorded answering (a film's release dates for this id, translations) is a failure of that one call,
            // which the refresh already treats as "nothing to add".
            throw new HttpRequestException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    [Fact]
    public async Task Refreshing_a_film_linked_to_a_record_fills_in_its_details_keeps_its_name_and_file_and_says_so_in_Activity()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel Old Name", 2010);
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET metadata_provider = 'tmdb', metadata_provider_id = '45745' WHERE id = '{film.MovieId}';");

        var response = await app.Client.PostAsync($"/api/movies/{film.MovieId}/metadata/refresh", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        // A refresh fills in what the record knows; the name the person has is kept (only "Fix match" renames).
        Assert.Equal("Sintel Old Name", updated.GetProperty("title").GetString());
        Assert.Equal("tt1727587", updated.GetProperty("imdbId").GetString());
        Assert.Contains("wandering warrior", updated.GetProperty("overview").GetString());
        Assert.Equal("tt1727587", await ApiScenes.TextAsync(app, "movies", $"SELECT imdb_id FROM movie_entries WHERE id = '{film.MovieId}'"));
        Assert.True(File.Exists(film.FilePath), "a metadata refresh never touches the film's file");
        Assert.Contains(replay.Asked, asked => asked.StartsWith("/metadata/search", StringComparison.Ordinal) && asked.Contains("providerId=45745", StringComparison.Ordinal));
        Assert.Contains("Sintel Old Name metadata was refreshed from TMDB.", await app.Client.GetStringAsync("/api/activity?take=100"));
    }

    [Fact]
    public async Task Refreshing_a_film_whose_linked_record_the_gateway_says_is_gone_keeps_the_film_and_its_file_and_records_the_notice()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET metadata_provider = 'tmdb', metadata_provider_id = '999999999' WHERE id = '{film.MovieId}';");

        var response = await app.Client.PostAsync($"/api/movies/{film.MovieId}/metadata/refresh", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("metadata-provider-record-missing", body.GetProperty("code").GetString());
        Assert.Contains("Sintel was kept.", body.GetProperty("message").GetString());
        Assert.True(File.Exists(film.FilePath));
        Assert.Equal("Sintel", await ApiScenes.TextAsync(app, "movies", $"SELECT title FROM movie_entries WHERE id = '{film.MovieId}'"));
        var issue = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/metadata/issue");
        Assert.Equal("provider-record-missing", issue.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/movies/no-such-film/metadata/refresh", content: null)).StatusCode);
    }

    [Fact]
    public async Task Refreshing_a_show_linked_to_a_record_reads_the_record_and_its_catalogue_keeps_the_files_and_says_so_in_Activity()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var show = await ApiScenes.ShowOnDiskAsync(app, "Breaking Bad Old Name", 2008);
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_entries SET metadata_provider = 'tmdb', metadata_provider_id = '1396' WHERE id = '{show.SeriesId}';");

        var response = await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/refresh", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Breaking Bad Old Name", refreshed.GetProperty("title").GetString());
        Assert.Contains("Walter White", refreshed.GetProperty("overview").GetString());
        Assert.Equal("Breaking Bad Old Name", await ApiScenes.TextAsync(app, "series", $"SELECT title FROM series_entries WHERE id = '{show.SeriesId}'"));
        Assert.True(File.Exists(show.FilePath), "a metadata refresh never touches an episode file");
        Assert.Contains("/metadata/tv/1396/catalogue", replay.Asked);
        Assert.Contains("Breaking Bad Old Name metadata was refreshed from TMDB.", await app.Client.GetStringAsync("/api/activity?take=100"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/series/no-such-show/metadata/refresh", content: null)).StatusCode);
    }
}
