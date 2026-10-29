using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1425: Collections as Radarr does it. The collections the films in the library belong to are listed with each member held, wanted
/// or missing; the real metadata provider reads the members from the project's gateway as it really answered for The Matrix
/// Collection (tests/fixtures/recorded/tmdb) and as it answered for a collection TMDb has none of. One action adds the missing films.
/// </summary>
public sealed class LibraryCollectionsOnTheRecordedGatewayTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Asked.Add(path);
            var state = path == "/metadata/movie/2344/collection" ? "collection-movie"
                : path.EndsWith("/collection", StringComparison.Ordinal) ? "collection-missing"
                : throw new InvalidOperationException($"No gateway recording answers {request.Method} {path}.");
            return Task.FromResult(RecordedReplies.Tmdb(state).Single.Reply.ToResponse());
        }
    }

    private const string Matrix = """{"CollectionProviderId":"2344","Collection":"The Matrix Collection"}""";

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    /// <summary>A film with a file in the library, tied to its TMDb record and the collection its metadata names.</summary>
    private static async Task<string> HeldAsync(ApplicationTestHost app, string title, int year, string providerId, string? metadataJson, string? libraryId = null)
    {
        var film = await ApiScenes.FilmOnDiskAsync(app, title, year, libraryId);
        await ApiScenes.ExecuteAsync(app, "movies",
            $"UPDATE movie_entries SET metadata_provider = 'tmdb', metadata_provider_id = '{providerId}', metadata_json = {(metadataJson is null ? "NULL" : "'" + metadataJson.Replace("'", "''") + "'")} WHERE id = '{film.MovieId}'");
        return film.MovieId;
    }

    private static async Task<string> WantedAsync(ApplicationTestHost app, string title, int year, string providerId, string? metadataJson, bool monitored = true)
    {
        var added = await app.Services.GetRequiredService<IMovieCatalogRepository>().AddAsync(
            new CreateMovieRequest(title, year, null, monitored, "tmdb", providerId, MetadataJson: metadataJson), CancellationToken.None);
        return added.Id;
    }

    private static async Task<JsonElement> ListAsync(ApplicationTestHost app)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/movie-collections/from-library");

    private static JsonElement Member(JsonElement collection, string title)
        => collection.GetProperty("members").EnumerateArray().Single(member => member.GetProperty("title").GetString() == title);

    [Fact]
    public async Task A_collection_a_held_film_belongs_to_lists_every_member_marked_held_wanted_or_missing_and_is_recommended_with_why()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var matrixId = await HeldAsync(app, "The Matrix", 1999, "603", Matrix);
        var reloadedId = await WantedAsync(app, "The Matrix Reloaded", 2003, "604", Matrix);
        await WantedAsync(app, "Not In A Collection", 2010, "999", null);
        await WantedAsync(app, "Broken Metadata", 2011, "998", "{not json");

        var body = await ListAsync(app);

        var collection = Assert.Single(body.GetProperty("collections").EnumerateArray());
        Assert.Equal("2344", collection.GetProperty("providerId").GetString());
        Assert.Equal("The Matrix Collection", collection.GetProperty("name").GetString());
        Assert.False(collection.GetProperty("following").GetBoolean());
        Assert.True(collection.GetProperty("membersKnown").GetBoolean());
        Assert.Equal(4, collection.GetProperty("members").GetArrayLength());
        Assert.Equal(1, collection.GetProperty("heldCount").GetInt32());
        Assert.Equal(1, collection.GetProperty("wantedCount").GetInt32());
        Assert.Equal(2, collection.GetProperty("missingCount").GetInt32());
        Assert.Equal("held", Member(collection, "The Matrix").GetProperty("status").GetString());
        Assert.Equal(matrixId, Member(collection, "The Matrix").GetProperty("localMovieId").GetString());
        Assert.Equal("wanted", Member(collection, "The Matrix Reloaded").GetProperty("status").GetString());
        Assert.Equal(reloadedId, Member(collection, "The Matrix Reloaded").GetProperty("localMovieId").GetString());
        Assert.Equal("missing", Member(collection, "The Matrix Revolutions").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Member(collection, "The Matrix Resurrections").GetProperty("localMovieId").ValueKind);
        Assert.Equal(["/metadata/movie/2344/collection"], replay.Asked);

        var recommended = Assert.Single(body.GetProperty("recommended").EnumerateArray());
        Assert.Equal("2344", recommended.GetProperty("providerId").GetString());
        Assert.Equal("You have 2 of 4 films, so 2 more would complete The Matrix Collection.", recommended.GetProperty("why").GetString());
    }

    [Fact]
    public async Task A_collection_missing_one_film_says_one_more_completes_it_and_comes_before_one_missing_more()
    {
        await using var app = await StartAsync(new GatewayReplay());
        await HeldAsync(app, "The Matrix", 1999, "603", Matrix);
        await WantedAsync(app, "The Matrix Reloaded", 2003, "604", Matrix);
        await WantedAsync(app, "The Matrix Revolutions", 2003, "605", Matrix);

        var body = await ListAsync(app);

        var recommended = Assert.Single(body.GetProperty("recommended").EnumerateArray());
        Assert.Equal("You have 3 of 4 films. One more completes The Matrix Collection.", recommended.GetProperty("why").GetString());
    }

    [Fact]
    public async Task A_collection_the_library_barely_holds_is_recommended_naming_the_film_it_has()
    {
        await using var app = await StartAsync(new GatewayReplay());
        await HeldAsync(app, "The Matrix", 1999, "603", Matrix);

        var recommended = Assert.Single((await ListAsync(app)).GetProperty("recommended").EnumerateArray());

        Assert.Equal("The Matrix is in your library, and 3 more films of The Matrix Collection are not.", recommended.GetProperty("why").GetString());
    }

    [Fact]
    public async Task A_complete_collection_is_listed_but_not_recommended_and_an_unmonitored_film_without_a_file_counts_as_missing()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var films = await ApiScenes.CreateLibraryAsync(app, "movies");
        await HeldAsync(app, "The Matrix", 1999, "603", Matrix, films);
        await HeldAsync(app, "The Matrix Reloaded", 2003, "604", Matrix, films);
        await HeldAsync(app, "The Matrix Revolutions", 2003, "605", Matrix, films);
        await WantedAsync(app, "The Matrix Resurrections", 2021, "624860", Matrix, monitored: false);

        var body = await ListAsync(app);

        var collection = Assert.Single(body.GetProperty("collections").EnumerateArray());
        Assert.Equal(3, collection.GetProperty("heldCount").GetInt32());
        Assert.Equal(1, collection.GetProperty("missingCount").GetInt32());
        Assert.Equal("missing", Member(collection, "The Matrix Resurrections").GetProperty("status").GetString());

        await ApiScenes.ExecuteAsync(app, "movies", "UPDATE movie_entries SET monitored = 1 WHERE title = 'The Matrix Resurrections'");
        var complete = Assert.Single((await ListAsync(app)).GetProperty("collections").EnumerateArray());
        Assert.Equal(1, complete.GetProperty("wantedCount").GetInt32());
        Assert.Equal(0, complete.GetProperty("missingCount").GetInt32());
        Assert.Empty((await ListAsync(app)).GetProperty("recommended").EnumerateArray());
    }

    [Fact]
    public async Task The_collection_a_film_names_in_the_form_a_collection_sync_writes_is_read_too()
    {
        await using var app = await StartAsync(new GatewayReplay());
        await WantedAsync(app, "The Matrix", 1999, "603", """{"provider":"tmdb","providerId":"603","collectionId":"2344","collectionName":"The Matrix Collection"}""");
        await WantedAsync(app, "Numeric", 2000, "700", """{"CollectionProviderId":2344}""");
        await WantedAsync(app, "Not a number", 2001, "701", """{"CollectionProviderId":"abc"}""");
        await WantedAsync(app, "An array", 2002, "702", "[1,2]");
        await WantedAsync(app, "A blank id", 2004, "703", """{"CollectionProviderId":"  "}""");

        var collection = Assert.Single((await ListAsync(app)).GetProperty("collections").EnumerateArray());

        Assert.Equal("2344", collection.GetProperty("providerId").GetString());
        Assert.Equal("The Matrix Collection", collection.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_collection_the_broker_does_not_know_still_lists_the_films_the_library_holds_and_is_not_recommended()
    {
        await using var app = await StartAsync(new GatewayReplay());
        await HeldAsync(app, "Lonely Film", 2005, "4242", """{"CollectionProviderId":"999999999","Collection":"Lost Collection"}""");

        var body = await ListAsync(app);

        var collection = Assert.Single(body.GetProperty("collections").EnumerateArray());
        Assert.Equal("Lost Collection", collection.GetProperty("name").GetString());
        Assert.False(collection.GetProperty("membersKnown").GetBoolean());
        Assert.Equal("Lonely Film", Assert.Single(collection.GetProperty("members").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(1, collection.GetProperty("heldCount").GetInt32());
        Assert.Equal(0, collection.GetProperty("missingCount").GetInt32());
        Assert.Empty(body.GetProperty("recommended").EnumerateArray());
    }

    [Fact]
    public async Task One_action_adds_the_missing_films_and_follows_the_collection_and_a_second_adds_nothing()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        await HeldAsync(app, "The Matrix", 1999, "603", Matrix, libraryId);
        await WantedAsync(app, "The Matrix Reloaded", 2003, "604", Matrix);

        var response = await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/2344/add-missing", new { libraryId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("addedCount").GetInt32());
        Assert.Equal("The Matrix Collection", result.GetProperty("collectionName").GetString());
        Assert.Equal("Added 2 films of The Matrix Collection, and Deluno now follows the collection.", result.GetProperty("message").GetString());

        var movies = await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None);
        Assert.Equal(4, movies.Count);
        Assert.Contains(movies, movie => movie.Title == "The Matrix Resurrections" && movie.Monitored);

        var collection = Assert.Single((await ListAsync(app)).GetProperty("collections").EnumerateArray());
        Assert.True(collection.GetProperty("following").GetBoolean());
        Assert.Equal(0, collection.GetProperty("missingCount").GetInt32());
        Assert.Equal(3, collection.GetProperty("wantedCount").GetInt32());
        Assert.Equal(1, collection.GetProperty("heldCount").GetInt32());
        Assert.Empty((await ListAsync(app)).GetProperty("recommended").EnumerateArray());

        var again = await (await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/2344/add-missing", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("addedCount").GetInt32());
        Assert.Equal("Nothing was missing from The Matrix Collection. Deluno now follows the collection.", again.GetProperty("message").GetString());
        Assert.Equal(4, (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_film_left_out_of_a_followed_collection_is_not_added_and_a_paused_followed_collection_is_switched_on_to_add()
    {
        await using var app = await StartAsync(new GatewayReplay());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        await HeldAsync(app, "The Matrix", 1999, "603", Matrix, libraryId);
        var followed = await app.Client.PostAsJsonAsync("/api/movie-collections", new { providerId = "2344", libraryId, monitored = false, monitorMovies = false });
        Assert.True(followed.IsSuccessStatusCode, await followed.Content.ReadAsStringAsync());
        var collectionId = (await followed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var excluded = await app.Client.PutAsJsonAsync($"/api/movie-collections/{collectionId}/members/605/exclusion", new { excluded = true });
        Assert.True(excluded.IsSuccessStatusCode);

        var before = Assert.Single((await ListAsync(app)).GetProperty("collections").EnumerateArray());
        Assert.True(before.GetProperty("following").GetBoolean());
        Assert.True(Member(before, "The Matrix Revolutions").GetProperty("isExcluded").GetBoolean());
        Assert.Equal(2, before.GetProperty("missingCount").GetInt32());

        var result = await (await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/2344/add-missing", new { })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, result.GetProperty("addedCount").GetInt32());
        var titles = (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Select(movie => movie.Title).ToArray();
        Assert.DoesNotContain("The Matrix Revolutions", titles);
        Assert.Contains("The Matrix Reloaded", titles);
        var after = Assert.Single((await ListAsync(app)).GetProperty("collections").EnumerateArray());
        Assert.Equal(0, after.GetProperty("missingCount").GetInt32());
    }

    [Fact]
    public async Task Adding_the_missing_films_asks_for_a_library_when_there_is_none_or_it_is_unknown_and_says_when_TMDb_has_no_such_collection()
    {
        await using var app = await StartAsync(new GatewayReplay());

        var noLibrary = await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/2344/add-missing", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noLibrary.StatusCode);
        Assert.Contains("Create a movie library first", await noLibrary.Content.ReadAsStringAsync());

        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var unknown = await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/2344/add-missing", new { libraryId = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Choose a movie library", await unknown.Content.ReadAsStringAsync());

        var none = await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/999999999/add-missing", new { });
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        var notNumeric = await app.Client.PostAsJsonAsync("/api/movie-collections/from-library/abc/add-missing", new { });
        Assert.Equal(HttpStatusCode.BadRequest, notNumeric.StatusCode);

        var noBody = await app.Client.PostAsync("/api/movie-collections/from-library/999999999/add-missing", null);
        Assert.Equal(HttpStatusCode.NotFound, noBody.StatusCode);
    }

    [Fact]
    public async Task With_no_films_in_any_collection_both_lists_are_empty()
    {
        await using var app = await StartAsync(new GatewayReplay());
        await WantedAsync(app, "Standalone", 2000, "1", null);

        var body = await ListAsync(app);

        Assert.Empty(body.GetProperty("collections").EnumerateArray());
        Assert.Empty(body.GetProperty("recommended").EnumerateArray());
    }
}
