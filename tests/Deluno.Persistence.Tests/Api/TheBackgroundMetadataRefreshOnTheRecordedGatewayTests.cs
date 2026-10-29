using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The background metadata refresh jobs (movies.metadata.refresh and series.metadata.refresh) run by the real host over the replies the
/// project's deployed metadata gateway really gave (tests/fixtures/recorded/tmdb): what the refresh writes to Activity, and the
/// auto-tagging rules it re-applies afterwards. Real stores in a real database file; the files on disk are never touched.
/// </summary>
public sealed class TheBackgroundMetadataRefreshOnTheRecordedGatewayTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path == "/metadata/search" && query.Contains("providerId=45745", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-movie-by-provider-id").Single.Reply.ToResponse());
            if (path == "/metadata/search" && query.Contains("providerId=1396", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-tv-by-provider-id").Single.Reply.ToResponse());
            if (path == "/metadata/tv/1396/catalogue")
                return Task.FromResult(RecordedReplies.Tmdb("catalogue-tv").Single.Reply.ToResponse());
            if (path == "/metadata/tv/1396/episode-groups")
                return Task.FromResult(RecordedReplies.Tmdb("episode-groups-tv").Single.Reply.ToResponse());
            throw new HttpRequestException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private static Task<ApplicationTestHost> StartAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => new GatewayReplay()));

    private static JobQueueItem JobFor(string type, string entityType, string entityId)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobQueueItem("job-1", type, "test", "running", null, 1, now, now, now, null, null, null, null, entityType, entityId, null, null, 3, now, null);
    }

    private static async Task<(string FilmId, string ShowId)> LinkedTitlesAsync(ApplicationTestHost app)
    {
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        var show = await ApiScenes.ShowOnDiskAsync(app, "Breaking Bad", 2008);
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET metadata_provider = 'tmdb', metadata_provider_id = '45745' WHERE id = '{film.MovieId}';");
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_entries SET metadata_provider = 'tmdb', metadata_provider_id = '1396' WHERE id = '{show.SeriesId}';");
        return (film.MovieId, show.SeriesId);
    }

    private static IJobHandler Handler(ApplicationTestHost app, string type)
        => app.Services.GetServices<IJobHandler>().Single(candidate => candidate.JobType == type);

    private static async Task<IReadOnlyList<ActivityEventItem>> ActivityOfAsync(ApplicationTestHost app, string entityType, string id)
        => await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, entityType, id, CancellationToken.None);

    [Fact]
    public async Task A_successful_background_refresh_writes_one_Activity_line_per_title_carrying_the_record_it_matched()
    {
        await using var app = await StartAsync();
        var (filmId, showId) = await LinkedTitlesAsync(app);

        var filmMessage = await Handler(app, "movies.metadata.refresh").HandleAsync(JobFor("movies.metadata.refresh", "movie", filmId), CancellationToken.None);
        var showMessage = await Handler(app, "series.metadata.refresh").HandleAsync(JobFor("series.metadata.refresh", "series", showId), CancellationToken.None);

        Assert.StartsWith("Refreshed metadata for Sintel", filmMessage);
        Assert.StartsWith("Refreshed metadata for Breaking Bad", showMessage);
        var film = Assert.Single(await ActivityOfAsync(app, "movie", filmId), item => item.Category == "metadata.movie.refreshed");
        Assert.Equal("Sintel metadata was refreshed by the background worker.", film.Message);
        Assert.Contains("\"ProviderId\":\"45745\"", film.DetailsJson);
        Assert.Contains("\"ImdbId\":\"tt1727587\"", film.DetailsJson);
        var show = Assert.Single(await ActivityOfAsync(app, "series", showId), item => item.Category == "metadata.series.refreshed");
        Assert.Equal("Breaking Bad metadata was refreshed by the background worker.", show.Message);
        Assert.Contains("\"ProviderId\":\"1396\"", show.DetailsJson);
    }

    [Fact]
    public async Task A_background_refresh_applies_the_auto_tagging_rules_again_with_what_the_refresh_learned_and_says_so_in_Activity()
    {
        await using var app = await StartAsync();
        var (filmId, showId) = await LinkedTitlesAsync(app);
        Assert.True((await app.Client.PostAsJsonAsync("/api/auto-tag-rules", new
        {
            name = "Cartoons",
            tags = new[] { "cartoons" },
            specifications = new[] { new { type = "genre", negate = false, value = "Animation" } }
        })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/auto-tag-rules", new
        {
            name = "Crime shows",
            tags = new[] { "crime" },
            appliesTo = "series",
            specifications = new[] { new { type = "genre", negate = false, value = "Crime" } }
        })).IsSuccessStatusCode);
        var tags = app.Services.GetRequiredService<IMediaTagStore>();
        Assert.Empty(await tags.ListAsync(MediaKind.Movie, filmId, CancellationToken.None));
        Assert.Empty(await tags.ListAsync(MediaKind.Series, showId, CancellationToken.None));

        await Handler(app, "movies.metadata.refresh").HandleAsync(JobFor("movies.metadata.refresh", "movie", filmId), CancellationToken.None);
        await Handler(app, "series.metadata.refresh").HandleAsync(JobFor("series.metadata.refresh", "series", showId), CancellationToken.None);

        Assert.Equal(["cartoons"], (await tags.ListAsync(MediaKind.Movie, filmId, CancellationToken.None)).Select(tag => tag.Name).ToArray());
        Assert.Equal(["crime"], (await tags.ListAsync(MediaKind.Series, showId, CancellationToken.None)).Select(tag => tag.Name).ToArray());
        var filmLine = Assert.Single(await ActivityOfAsync(app, "movie", filmId), item => item.Category == "tags.auto");
        Assert.Contains("when its details were refreshed", filmLine.Message);
        Assert.Contains("“Cartoons”", filmLine.Message);
        var showLine = Assert.Single(await ActivityOfAsync(app, "series", showId), item => item.Category == "tags.auto");
        Assert.Contains("“Crime shows”", showLine.Message);
    }
}
