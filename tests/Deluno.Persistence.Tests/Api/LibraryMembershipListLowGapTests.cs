using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Low-severity rows (#1329) of the list of libraries a title is in (GET, POST and DELETE on /api/movies/{id}/libraries and the
/// show twin), on the whole application with its own databases and a real video file in a library folder.
/// </summary>
public sealed class LibraryMembershipListLowGapTests
{
    private static async Task<string> AddFilmAsync(ApplicationTestHost app, string title, int year, string libraryId)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static JsonElement Entry(JsonElement list, string libraryId)
        => list.EnumerateArray().Single(item => item.GetProperty("libraryId").GetString() == libraryId);

    [Fact]
    public async Task The_libraries_of_a_film_are_listed_with_whether_each_holds_a_file_how_wanted_it_is_and_whether_it_can_leave_or_move()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var spare = await ApiScenes.CreateLibraryAsync(app, "movies", "Spare", "Spare");
        var id = await AddFilmAsync(app, "Arrival", 2016, films);

        // In one library only: it cannot leave (it would be in none) but, with no file, it can move.
        var one = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}/libraries");
        var only = Assert.Single(one.EnumerateArray());
        Assert.Equal(films, only.GetProperty("libraryId").GetString());
        Assert.False(only.GetProperty("hasFile").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(only.GetProperty("wantedStatus").GetString()));
        Assert.False(only.GetProperty("canRemove").GetBoolean());
        Assert.True(only.GetProperty("canMove").GetBoolean());
        Assert.False(only.GetProperty("notWantedHere").GetBoolean());

        // Kept in a second library: both can leave while neither holds a file.
        var added = await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries", new { libraryId = spare });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var two = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, two.GetArrayLength());
        Assert.True(Entry(two, films).GetProperty("canRemove").GetBoolean());
        Assert.True(Entry(two, spare).GetProperty("canRemove").GetBoolean());
        Assert.False(Entry(two, spare).GetProperty("hasFile").GetBoolean());

        // A file in the first library: that copy can neither leave nor move; the other still can.
        var folder = Path.Combine(await ApiScenes.LibraryRootAsync(app, films), "Arrival (2016)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Arrival (2016) [Bluray-1080p].mkv");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_wanted_state SET has_file = 1 WHERE movie_id = '{id}' AND library_id = '{films}'");
        var withFile = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}/libraries");
        Assert.True(Entry(withFile, films).GetProperty("hasFile").GetBoolean());
        Assert.False(Entry(withFile, films).GetProperty("canRemove").GetBoolean());
        Assert.False(Entry(withFile, films).GetProperty("canMove").GetBoolean());
        Assert.True(Entry(withFile, spare).GetProperty("canRemove").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/no-such-film/libraries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/series/no-such-show/libraries")).StatusCode);
    }

    [Fact]
    public async Task Keeping_a_film_in_a_library_it_is_already_in_does_not_duplicate_it_and_the_new_copy_starts_with_no_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var spare = await ApiScenes.CreateLibraryAsync(app, "movies", "Spare", "Spare");
        var id = await AddFilmAsync(app, "Arrival", 2016, films);

        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries", new { libraryId = spare })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries", new { libraryId = spare })).StatusCode);
        var again = await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries", new { libraryId = films });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var list = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), item => Assert.False(item.GetProperty("hasFile").GetBoolean()));
        Assert.Equal("2", await ApiScenes.TextAsync(app, "movies", $"SELECT COUNT(*) FROM movie_wanted_state WHERE movie_id = '{id}'"));
    }

    [Fact]
    public async Task Taking_a_title_out_of_a_library_it_is_not_in_or_naming_a_title_that_does_not_exist_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var films = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var other = await ApiScenes.CreateLibraryAsync(app, "movies", "Other", "Other");
        var id = await AddFilmAsync(app, "Arrival", 2016, films);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/movies/{id}/libraries/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/movies/{id}/libraries/no-such-library")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/movies/no-such-film/libraries/{films}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/series/no-such-show/libraries/{films}")).StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}/libraries")).EnumerateArray());
    }
}
