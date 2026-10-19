using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The film collection routes (coverage register, area api) through the real metadata provider reading the project's metadata gateway as it
/// really answered (tests/fixtures/recorded/tmdb: The Matrix Collection, TMDb 2344, and an id the gateway has no collection for). The route,
/// the provider's reading of the recorded reply, the catalogue and the database file are all real.
/// </summary>
public sealed class MovieCollectionRoutesOnTheRecordedGatewayTests
{
    private const string Matrix = "2344";

    private sealed class GatewayReplay : HttpMessageHandler
    {
        public int CollectionCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/metadata/movie/{Matrix}/collection")
            {
                Interlocked.Increment(ref CollectionCalls);
                return Task.FromResult(RecordedReplies.Tmdb("collection-movie").Single.Reply.ToResponse());
            }

            if (path == "/metadata/movie/999999999/collection")
            {
                return Task.FromResult(RecordedReplies.Tmdb("collection-missing").Single.Reply.ToResponse());
            }

            throw new InvalidOperationException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    private static async Task<JsonElement> AddMatrixAsync(ApplicationTestHost app, string libraryId, bool monitored = false)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = Matrix, libraryId, monitored, monitorMovies = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> MembersAsync(ApplicationTestHost app, string collectionId)
        => await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{collectionId}/members");

    [Fact]
    public async Task Adding_a_collection_reads_it_from_the_gateway_keeps_its_four_films_as_members_and_a_missing_one_or_a_wrong_library_is_refused()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");

        var collection = await AddMatrixAsync(app, movies);

        Assert.Equal("The Matrix Collection", collection.GetProperty("name").GetString());
        Assert.Equal(4, collection.GetProperty("memberCount").GetInt32());
        Assert.Equal(movies, collection.GetProperty("libraryId").GetString());
        var members = await MembersAsync(app, collection.GetProperty("id").GetString()!);
        Assert.Equal(new[] { "The Matrix", "The Matrix Reloaded", "The Matrix Revolutions", "The Matrix Resurrections" },
            members.EnumerateArray().Select(member => member.GetProperty("title").GetString()).ToArray());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));
        Assert.Equal(4, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collection_members"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries"));

        var missing = await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = "999999999", libraryId = movies });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("TMDb did not return that collection.", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = "not-a-number", libraryId = movies })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = Matrix, libraryId = shows })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = Matrix })).StatusCode);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));
    }

    [Fact]
    public async Task Editing_a_collection_one_at_a_time_or_several_together_changes_what_is_stored_and_an_unknown_one_is_404()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await AddMatrixAsync(app, movies)).GetProperty("id").GetString()!;
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}")).GetProperty("monitored").GetBoolean());

        var one = await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}", new { monitored = true, minimumAvailability = "announced", searchOnAdd = true });

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var edited = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}");
        Assert.True(edited.GetProperty("monitored").GetBoolean());
        Assert.True(edited.GetProperty("searchOnAdd").GetBoolean());
        Assert.Equal("announced", edited.GetProperty("minimumAvailability").GetString());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", $"SELECT monitored FROM movie_collections WHERE id = '{id}'"));

        var bulk = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = new[] { id }, monitored = false, searchOnAdd = false });

        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movie-collections/{id}");
        Assert.False(after.GetProperty("monitored").GetBoolean());
        Assert.False(after.GetProperty("searchOnAdd").GetBoolean());
        Assert.Equal("announced", after.GetProperty("minimumAvailability").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/movie-collections/no-such-collection", new { monitored = true })).StatusCode);
    }

    [Fact]
    public async Task Refreshing_a_monitored_collection_adds_every_film_not_excluded_as_wanted_and_leaves_an_excluded_one_out()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await AddMatrixAsync(app, movies, monitored: true)).GetProperty("id").GetString()!;
        var callsAfterAdd = replay.CollectionCalls;

        var excluded = await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/624860/exclusion", new { excluded = true });

        Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);
        var member = (await MembersAsync(app, id)).EnumerateArray().Single(item => item.GetProperty("providerId").GetString() == "624860");
        Assert.True(member.GetProperty("isExcluded").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/1/exclusion", new { excluded = true })).StatusCode);

        var refreshed = await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", content: null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var result = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(callsAfterAdd + 1, replay.CollectionCalls);
        Assert.Equal(3, result.GetProperty("addedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("excludedCount").GetInt32());
        Assert.Equal("Collection refreshed and added 3 new movies.", result.GetProperty("message").GetString());
        var titles = (await app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Select(item => item.Title).Order().ToArray();
        Assert.Equal(new[] { "The Matrix", "The Matrix Reloaded", "The Matrix Revolutions" }, titles);
        Assert.Equal(3, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries WHERE monitored = 1"));

        var included = await app.Client.PutAsJsonAsync($"/api/movie-collections/{id}/members/624860/exclusion", new { excluded = false });
        Assert.Equal(HttpStatusCode.OK, included.StatusCode);
        var again = await (await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, again.GetProperty("addedCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/movie-collections/no-such-collection/refresh", content: null)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_collection_forgets_it_and_its_members_but_keeps_the_films_already_added_and_saying_it_twice_is_404()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await AddMatrixAsync(app, movies, monitored: true)).GetProperty("id").GetString()!;
        await app.Client.PostAsync($"/api/movie-collections/{id}/refresh", content: null);
        Assert.Equal(4, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries"));
        Assert.True(Directory.Exists(await ApiScenes.LibraryRootAsync(app, movies)));

        var removed = await app.Client.DeleteAsync($"/api/movie-collections/{id}");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(1, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collection_members"));
        Assert.Equal(4, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/movie-collections/{id}")).StatusCode);
    }

    [Fact]
    public async Task Removing_several_collections_at_once_says_how_many_went_and_an_empty_list_removes_nothing()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var movies = await ApiScenes.CreateLibraryAsync(app, "movies");
        var id = (await AddMatrixAsync(app, movies)).GetProperty("id").GetString()!;

        var none = await app.Client.PostAsJsonAsync("/api/movie-collections/remove", new { collectionIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one collection.", await none.Content.ReadAsStringAsync());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));

        var removed = await app.Client.PostAsJsonAsync("/api/movie-collections/remove", new { collectionIds = new[] { id, "no-such-collection" } });

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(1, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));
    }
}
