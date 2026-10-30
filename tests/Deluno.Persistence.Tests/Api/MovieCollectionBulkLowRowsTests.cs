using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The bulk editor and "stop following" of movie collections (#1329), over HTTP from collection rows in a real SQLite file: unknown
/// quality profile, library or availability are refused with the server's own sentence and change nothing; a good change is stored
/// and written to Activity in plain words; stopping to follow removes only the collection records, never a movie or its file.
/// </summary>
public sealed class MovieCollectionBulkLowRowsTests
{
    private static async Task SeedCollectionsAsync(ApplicationTestHost app, string libraryId, params (string Id, string Name, string ProviderId)[] collections)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (id, name, providerId) in collections)
        {
            await ApiScenes.ExecuteAsync(app, "movies",
                $"""
                INSERT INTO movie_collections (id, provider, provider_id, library_id, library_name, root_path, name, monitored, created_utc, updated_utc)
                VALUES ('{id}', 'tmdb', '{providerId}', '{libraryId}', 'Films', '/films', '{name}', 0, '{now}', '{now}');
                """);
        }
    }

    private static async Task<string[]> ProblemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return [.. errors.EnumerateObject().SelectMany(property => property.Value.EnumerateArray().Select(item => item.GetString()!))];
    }

    [Fact]
    public async Task The_bulk_editor_refuses_an_unknown_profile_library_or_availability_in_the_servers_sentence_and_a_good_change_is_stored_and_written_to_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var profile = await app.Services.GetRequiredService<IQualityRepository>().CreateQualityProfileAsync(
            new CreateQualityProfileRequest("Collections 1080p", "movies", "WEB 1080p", "WEB 1080p", null, UpgradeUntilCutoff: true, UpgradeUnknownItems: false),
            CancellationToken.None);
        await SeedCollectionsAsync(app, libraryId, ("c1", "Alpha Collection", "101"), ("c2", "Beta Collection", "102"));
        var ids = new[] { "c1", "c2" };

        var noneChosen = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = Array.Empty<string>(), monitored = true });
        var badAvailability = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = ids, minimumAvailability = "someday" });
        var badLibrary = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = ids, libraryId = "no-such-library" });
        var badProfile = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = ids, qualityProfileId = "no-such-profile" });

        Assert.Equal(["Choose at least one collection."], await ProblemsAsync(noneChosen));
        Assert.Equal([$"Choose one of: {string.Join(", ", MovieAvailability.All)}."], await ProblemsAsync(badAvailability));
        Assert.Equal(["Choose a movie library for the collections."], await ProblemsAsync(badLibrary));
        Assert.Single(await ProblemsAsync(badProfile));
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections WHERE monitored = 1 OR minimum_availability <> 'released' OR quality_profile_id IS NOT NULL"));

        var changed = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new
        {
            collectionIds = ids, monitored = true, monitorMovies = false, qualityProfileId = profile.Id, minimumAvailability = "inCinemas", searchOnAdd = true, libraryId
        });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "movies",
            $"SELECT COUNT(*) FROM movie_collections WHERE monitored = 1 AND monitor_movies = 0 AND search_on_add = 1 AND minimum_availability = 'inCinemas' AND quality_profile_id = '{profile.Id}' AND library_id = '{libraryId}'"));
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("You changed 2 movie collections: monitoring on, adding missing movies off", activity, StringComparison.Ordinal);
        Assert.Contains($"quality profile {profile.Name}, minimum availability inCinemas", activity, StringComparison.Ordinal);
        Assert.Contains("search on add on", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_every_field_as_it_is_changes_nothing_and_writes_nothing_to_activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        await SeedCollectionsAsync(app, libraryId, ("c1", "Alpha Collection", "101"));

        var response = await app.Client.PutAsJsonAsync("/api/movie-collections/bulk", new { collectionIds = new[] { "c1" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections WHERE id = 'c1' AND monitored = 0 AND minimum_availability = 'released' AND quality_profile_id IS NULL"));
        Assert.DoesNotContain("collection.edited", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_to_follow_removes_only_the_collection_records_says_so_in_Activity_and_leaves_every_movie_and_file_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Alpha One", 2001);
        await SeedCollectionsAsync(app, film.LibraryId, ("c1", "Alpha Collection", "101"), ("c2", "Beta Collection", "102"), ("c3", "Gamma Collection", "103"));
        var moviesBefore = await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries");

        var none = await app.Client.PostAsJsonAsync("/api/movie-collections/remove", new { collectionIds = Array.Empty<string>() });
        var several = await app.Client.PostAsJsonAsync("/api/movie-collections/remove", new { collectionIds = new[] { "c1", "c2" } });
        var one = await app.Client.DeleteAsync("/api/movie-collections/c3");
        var gone = await app.Client.DeleteAsync("/api/movie-collections/c3");

        Assert.Equal(["Choose at least one collection."], await ProblemsAsync(none));
        Assert.Equal(2, (await several.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_collections"));
        Assert.Equal(moviesBefore, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries"));
        Assert.True(File.Exists(film.FilePath), "stopping to follow never touches a movie's file");
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("You stopped following 2 movie collections (Alpha Collection, Beta Collection). Their movies were left exactly as they are.", activity, StringComparison.Ordinal);
        Assert.Contains("You stopped following the movie collection Gamma Collection. Its movies were left exactly as they are.", activity, StringComparison.Ordinal);
    }
}
