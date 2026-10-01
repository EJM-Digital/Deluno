using System.Net;
using System.Text.Json;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>#1104: a processor is told which title a hand-off is, from what Deluno has stored.</summary>
public sealed class AHandoffSaysWhichTitleItIsTests
{
    private const string CacheKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string[] OriginalKeys =
        ["eventType", "handoffId", "libraryId", "mediaType", "sourcePath", "releaseName", "queueItemId", "callbackPath"];

    [Fact]
    public async Task A_movie_handoff_carries_its_title_year_ids_and_poster()
    {
        var resolver = Resolver(Dispatch("movie", "movie-1"), movie: Movie());

        var payload = await Submit(resolver);

        Assert.Equal("Blade Runner 2049", payload.GetProperty("title").GetString());
        Assert.Equal(2017, payload.GetProperty("year").GetInt32());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("year").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("tmdbId").ValueKind);
        Assert.Equal(335984, payload.GetProperty("tmdbId").GetInt32());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("imdbId").ValueKind);
        Assert.Equal("tt1856101", payload.GetProperty("imdbId").GetString());
        Assert.Equal("https://image.example.test/poster.jpg", payload.GetProperty("posterUrl").GetString());
        Assert.False(payload.TryGetProperty("tvdbId", out _));
        Assert.False(payload.TryGetProperty("season", out _));
        Assert.False(payload.TryGetProperty("episode", out _));
        AssertOriginalKeysFirst(payload);
    }

    [Fact]
    public async Task An_episode_handoff_carries_the_show_its_tvdb_id_season_and_episode()
    {
        var resolver = Resolver(Dispatch("episode", "episode-1"), show: Show(), episode: ("episode-1", "series-1", 3, 7));

        var payload = await Submit(resolver);

        Assert.Equal("Severance", payload.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("tvdbId").ValueKind);
        Assert.Equal(371980, payload.GetProperty("tvdbId").GetInt32());
        Assert.Equal(95396, payload.GetProperty("tmdbId").GetInt32());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("season").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("episode").ValueKind);
        Assert.Equal(3, payload.GetProperty("season").GetInt32());
        Assert.Equal(7, payload.GetProperty("episode").GetInt32());
        Assert.Equal("https://image.example.test/show.jpg", payload.GetProperty("posterUrl").GetString());
        AssertOriginalKeysFirst(payload);
    }

    [Fact]
    public async Task A_handoff_for_a_title_that_is_not_matched_carries_none_of_it_and_every_original_key()
    {
        var resolver = Resolver(dispatch: null);

        var payload = await Submit(resolver);

        Assert.Equal(OriginalKeys, payload.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain(payload.EnumerateObject(), p => p.Value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Unknown_values_are_left_out_rather_than_guessed()
    {
        var resolver = Resolver(Dispatch("movie", "movie-1"), movie: Movie() with { ImdbId = null, PosterUrl = null, MetadataProvider = "manual", MetadataProviderId = null });

        var payload = await Submit(resolver);

        Assert.True(payload.TryGetProperty("title", out _));
        Assert.False(payload.TryGetProperty("tmdbId", out _));
        Assert.False(payload.TryGetProperty("imdbId", out _));
        Assert.False(payload.TryGetProperty("posterUrl", out _));
    }

    [Fact]
    public async Task A_poster_cached_by_deluno_is_sent_as_the_public_address_it_came_from()
    {
        var resolver = Resolver(Dispatch("movie", "movie-1"), movie: Movie() with { PosterUrl = "/api/metadata/artwork/" + CacheKey });

        var payload = await Submit(resolver);

        Assert.Equal("https://image.tmdb.org/t/p/w780/abc.jpg", payload.GetProperty("posterUrl").GetString());
    }

    [Theory]
    [InlineData("/api/metadata/artwork/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("/images/poster.jpg")]
    [InlineData("http://image.example.test/poster.jpg")]
    public async Task A_poster_a_processor_could_not_fetch_on_its_own_is_left_out(string stored)
    {
        var resolver = Resolver(Dispatch("movie", "movie-1"), movie: Movie() with { PosterUrl = stored });

        var payload = await Submit(resolver);

        Assert.False(payload.TryGetProperty("posterUrl", out _));
    }

    [Fact]
    public async Task A_non_numeric_provider_id_is_left_out_rather_than_sent_as_text()
    {
        var resolver = Resolver(Dispatch("movie", "movie-1"), movie: Movie() with { MetadataProviderId = "tt-not-a-number" });

        var payload = await Submit(resolver);

        Assert.False(payload.TryGetProperty("tmdbId", out _));
    }

    private static void AssertOriginalKeysFirst(JsonElement payload)
        => Assert.Equal(OriginalKeys, payload.EnumerateObject().Select(p => p.Name).Take(OriginalKeys.Length).ToArray());

    private static async Task<JsonElement> Submit(IProcessorHandoffTitleResolver resolver)
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var service = new ProcessorConnectionService(new StubHttpClientFactory(handler), resolver);
        var connection = new ProcessorConnectionItem(
            "connection-1", "Processor", "generic-webhook", "https://processor.example.test/deluno",
            "Authorization", "token", true, "unknown", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "library-1", "movies", "client-1", "queue-1", "Release.Name",
            "/downloads/release", "Processor", "waiting", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await service.SubmitAsync(connection, handoff, CancellationToken.None);

        Assert.True(result.IsAccepted);
        return JsonDocument.Parse(body!).RootElement.Clone();
    }

    private static IProcessorHandoffTitleResolver Resolver(
        DownloadDispatchItem? dispatch,
        MovieListItem? movie = null,
        SeriesListItem? show = null,
        (string EpisodeId, string SeriesId, int Season, int Episode)? episode = null)
    {
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(r => r.FindAnyDispatchByHashAsync("client-1", "queue-1", It.IsAny<CancellationToken>())).ReturnsAsync(dispatch);
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(r => r.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(show);
        if (episode is { } e)
        {
            series.Setup(r => r.GetEpisodeSeriesIdAsync(e.EpisodeId, It.IsAny<CancellationToken>())).ReturnsAsync(e.SeriesId);
            series.Setup(r => r.ListEpisodeNumbersAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([(e.EpisodeId, e.SeriesId, e.Season, e.Episode)]);
        }

        var artwork = new Mock<IArtworkRemoteUrls>();
        artwork.Setup(r => r.FindRemoteUrlAsync(CacheKey, It.IsAny<CancellationToken>())).ReturnsAsync("https://image.tmdb.org/t/p/w780/abc.jpg");
        return new ProcessorHandoffTitleResolver(dispatches.Object, movies.Object, series.Object, artwork.Object);
    }

    private static DownloadDispatchItem Dispatch(string entityType, string entityId)
        => new(
            "dispatch-1", "library-1", entityType == "movie" ? "movies" : "tv", entityType, entityId, "Release.Name", "Indexer",
            "client-1", "Client", "queued", null, DateTimeOffset.UtcNow,
            null, null, null, null, null, null,
            null, "queue-1", null,
            null, null, null, null, null, null,
            null);

    private static MovieListItem Movie()
        => new(
            "movie-1", "Blade Runner 2049", 2017, "tt1856101", true, false, "tmdb", "335984", null, null,
            "https://image.example.test/poster.jpg", null, null, [], null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static SeriesListItem Show()
        => new(
            "series-1", "Severance", 2022, "tt11280740", true, false, "tmdb", "95396", null, null,
            "https://image.example.test/show.jpg", null, null, [], null, null,
            "{\"TvDbId\":\"371980\"}", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
