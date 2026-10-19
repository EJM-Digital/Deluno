using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Collections (coverage register, area library): following a TMDb collection, its members, the Monitor and Add missing switches,
/// Exclude, Refresh and the sync that adds the missing films as wanted. The metadata gateway is the project's real one, as it
/// really answered (tests/fixtures/recorded/tmdb: the Matrix collection, and the collection the gateway says does not exist);
/// the route, the provider's parsing of the recorded reply, the service and the catalogue are all real.
/// </summary>
public sealed class MovieCollectionsOnTheRecordedGatewayTests
{
    private const string Matrix = "2344";
    private static readonly string[] MatrixFilms = ["The Matrix", "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix Resurrections"];

    private sealed class GatewayReplay : HttpMessageHandler
    {
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("The gateway did not answer.");
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/metadata/movie/{Matrix}/collection")
                return Task.FromResult(RecordedReplies.Tmdb("collection-movie").Single.Reply.ToResponse());
            if (path == "/metadata/movie/999999999/collection")
                return Task.FromResult(RecordedReplies.Tmdb("collection-missing").Single.Reply.ToResponse());
            throw new InvalidOperationException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    private static async Task<JsonElement> FollowAsync(ApplicationTestHost app, string libraryId, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["providerId"] = Matrix, ["libraryId"] = libraryId };
        foreach (var property in (extra ?? new { }).GetType().GetProperties()) body[property.Name] = property.GetValue(extra);
        var response = await app.Client.PostAsJsonAsync("/api/movie-collections/", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string[]> CatalogueTitlesAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Select(movie => movie.Title).Order().ToArray();

    [Fact]
    public async Task Following_the_Matrix_collection_lists_every_film_the_gateway_named_and_adds_none_while_it_is_not_monitored()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");

        var collection = await FollowAsync(app, libraryId, new { Tags = "franchise, 4k" });

        Assert.Equal("The Matrix Collection", collection.GetProperty("name").GetString());
        Assert.Equal(4, collection.GetProperty("memberCount").GetInt32());
        Assert.Equal(4, collection.GetProperty("missingCount").GetInt32());
        Assert.False(collection.GetProperty("monitored").GetBoolean());
        var id = collection.GetProperty("id").GetString()!;
        var members = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}/members");
        Assert.Equal(MatrixFilms.Order(), members.EnumerateArray().Select(member => member.GetProperty("title").GetString()!).Order());
        Assert.Empty(await CatalogueTitlesAsync(app));
        var listed = await app.Client.GetFromJsonAsync<JsonElement>("/api/movie-collections/");
        Assert.Equal(id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_follow_that_cannot_work_says_why_in_the_sentence_the_screen_shows_and_follows_nothing()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");

        var notNumeric = await app.Client.PostAsJsonAsync("/api/movie-collections/", new { providerId = "matrix", libraryId });
        var noLibrary = await app.Client.PostAsJsonAsync("/api/movie-collections/", new { providerId = Matrix });
        var missing = await app.Client.PostAsJsonAsync("/api/movie-collections/", new { providerId = "999999999", libraryId });

        Assert.Equal(HttpStatusCode.BadRequest, notNumeric.StatusCode);
        Assert.Contains("Enter a numeric TMDb collection id.", await notNumeric.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        Assert.Contains("Choose a movie library for the collection.", await noLibrary.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("TMDb did not return that collection.", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/movie-collections/")).EnumerateArray());
    }

    [Fact]
    public async Task Refreshing_a_monitored_collection_adds_the_missing_films_as_wanted_with_the_library_profile_availability_tags_and_a_search_for_the_library()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var collection = await FollowAsync(app, libraryId, new { Monitored = true, MonitorMovies = true, SearchOnAdd = true, MinimumAvailability = "inCinemas", Tags = "franchise" });
        var id = collection.GetProperty("id").GetString()!;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        Assert.DoesNotContain((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);

        var refreshed = await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var result = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, result.GetProperty("addedCount").GetInt32());
        Assert.True(result.GetProperty("searchRequested").GetBoolean());
        Assert.Equal("Collection refreshed and added 4 new movies.", result.GetProperty("message").GetString());
        Assert.Equal(MatrixFilms.Order(), await CatalogueTitlesAsync(app));
        foreach (var movie in await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None))
        {
            Assert.True(movie.Monitored, movie.Title);
            Assert.Equal(libraryId, await ApiScenes.TextAsync(app, "movies", $"SELECT library_id FROM movie_wanted_state WHERE movie_id = '{movie.Id}';"));
            Assert.Equal("missing", await ApiScenes.TextAsync(app, "movies", $"SELECT wanted_status FROM movie_wanted_state WHERE movie_id = '{movie.Id}';"));
            Assert.Equal("inCinemas", movie.MinimumAvailability);
            var tags = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{movie.Id}/tags");
            Assert.Contains("franchise", tags.EnumerateArray().Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString() : tag.GetProperty("name").GetString()));
        }

        Assert.Contains((await jobs.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values, state => state.SearchRequested);
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}");
        Assert.Equal(4, after.GetProperty("heldCount").GetInt32());
        Assert.Equal(0, after.GetProperty("missingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastSyncError").ValueKind);
    }

    [Fact]
    public async Task An_excluded_member_is_not_added_and_Include_again_lets_the_next_refresh_add_it()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await FollowAsync(app, libraryId, new { Monitored = true })).GetProperty("id").GetString()!;

        var exclude = await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/604/exclusion", new { excluded = true });
        Assert.Equal(HttpStatusCode.OK, exclude.StatusCode);
        Assert.True((await exclude.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("excluded").GetBoolean());
        var first = await (await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, first.GetProperty("addedCount").GetInt32());
        Assert.Equal(1, first.GetProperty("excludedCount").GetInt32());
        Assert.DoesNotContain("The Matrix Reloaded", await CatalogueTitlesAsync(app));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/424242/exclusion", new { excluded = true })).StatusCode);

        await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/604/exclusion", new { excluded = false });
        var second = await (await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetProperty("addedCount").GetInt32());
        Assert.Contains("The Matrix Reloaded", await CatalogueTitlesAsync(app));
    }

    [Fact]
    public async Task The_Monitor_and_Add_missing_switches_decide_whether_a_refresh_adds_anything()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await FollowAsync(app, libraryId)).GetProperty("id").GetString()!;

        var monitored = await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}", new { monitored = true, monitorMovies = false });
        Assert.Equal(HttpStatusCode.OK, monitored.StatusCode);
        var body = await monitored.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("monitored").GetBoolean());
        Assert.False(body.GetProperty("monitorMovies").GetBoolean());
        var none = await (await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, none.GetProperty("addedCount").GetInt32());
        Assert.Empty(await CatalogueTitlesAsync(app));

        await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}", new { monitorMovies = true });
        var all = await (await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, all.GetProperty("addedCount").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/movie-collections/no-such-collection", new { monitored = true })).StatusCode);
    }

    [Fact]
    public async Task A_refresh_the_gateway_cannot_answer_keeps_the_collection_and_records_the_reason_to_show_as_Last_refresh()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await FollowAsync(app, libraryId, new { Monitored = true })).GetProperty("id").GetString()!;
        replay.Down = true;

        var refreshed = await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var result = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, result.GetProperty("addedCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}");
        Assert.False(string.IsNullOrWhiteSpace(after.GetProperty("lastSyncError").GetString()));
        Assert.Equal(4, after.GetProperty("memberCount").GetInt32());
        Assert.Empty(await CatalogueTitlesAsync(app));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/movie-collections/no-such-collection/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Stopping_to_follow_leaves_the_films_it_added()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await FollowAsync(app, libraryId, new { Monitored = true })).GetProperty("id").GetString()!;
        await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", null);

        var removed = await app.Client.PostAsJsonAsync("/api/movie-collections/remove", new { collectionIds = new[] { id } });

        Assert.Equal(1, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/movie-collections/")).EnumerateArray());
        Assert.Equal(MatrixFilms.Order(), await CatalogueTitlesAsync(app));
    }
}
