using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The per-title read routes of the film and show catalogues (#1329), asked of titles that were really added and, for the rename
/// previews and the libraries cards, of titles with a real file on disk. A fresh install's empty answer is not what these prove:
/// each asserts what the title it was given has.
/// </summary>
public sealed class TheCatalogueReadRoutesAnswerWithRealTitlesTests
{
    private static async Task<string> AddAsync(ApplicationTestHost app, string kind, object body)
    {
        using var response = await app.Client.PostAsJsonAsync($"/api/{kind}", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_genres_present_and_a_films_tags_are_what_the_added_films_carry()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        var arrival = await AddAsync(app, "movies", new { title = "Arrival", releaseYear = 2016, monitored = true, libraryId = library, genres = "Drama, Science Fiction", tags = new[] { "Kids" }, searchForMissingNow = false });
        var sintel = await AddAsync(app, "movies", new { title = "Sintel", releaseYear = 2010, monitored = true, libraryId = library, genres = "Animation, Drama", searchForMissingNow = false });

        var genres = (await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/genres")).EnumerateArray().Select(item => item.GetString()!).ToArray();
        var arrivalTags = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{arrival}/tags")).EnumerateArray().ToArray();
        var sintelTags = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{sintel}/tags");

        Assert.Equal(["Animation", "Drama", "Science Fiction"], genres.Order().ToArray());
        Assert.Equal("Kids", Assert.Single(arrivalTags).GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(arrivalTags[0].GetProperty("tagId").GetString()));
        Assert.Equal(0, sintelTags.GetArrayLength());
    }

    [Fact]
    public async Task The_genres_present_and_a_shows_tags_are_what_the_added_shows_carry()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "tv");
        var dark = await AddAsync(app, "series", new { title = "Dark", startYear = 2017, monitored = true, libraryId = library, genres = "Drama, Mystery", tags = new[] { "Kids" }, searchForMissingNow = false });
        var wire = await AddAsync(app, "series", new { title = "The Wire", startYear = 2002, monitored = true, libraryId = library, genres = "Crime, Drama", searchForMissingNow = false });

        var genres = (await app.Client.GetFromJsonAsync<JsonElement>("/api/series/genres")).EnumerateArray().Select(item => item.GetString()!).ToArray();
        var darkTags = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{dark}/tags")).EnumerateArray().ToArray();
        var wireTags = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{wire}/tags");

        Assert.Equal(["Crime", "Drama", "Mystery"], genres.Order().ToArray());
        Assert.Equal("Kids", Assert.Single(darkTags).GetProperty("name").GetString());
        Assert.Equal(0, wireTags.GetArrayLength());
    }

    [Fact]
    public async Task A_film_and_a_show_with_files_on_disk_list_the_library_that_holds_them_and_say_the_file_is_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app);
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var filmLibraries = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/libraries")).EnumerateArray().ToArray();
        var showLibraries = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/libraries")).EnumerateArray().ToArray();

        var filmCard = Assert.Single(filmLibraries);
        Assert.Equal(film.LibraryId, filmCard.GetProperty("libraryId").GetString());
        Assert.Equal("Films", filmCard.GetProperty("libraryName").GetString());
        Assert.True(filmCard.GetProperty("hasFile").GetBoolean());
        Assert.Equal("covered", filmCard.GetProperty("wantedStatus").GetString());
        var showCard = Assert.Single(showLibraries);
        Assert.Equal(show.LibraryId, showCard.GetProperty("libraryId").GetString());
        Assert.Equal("Shows", showCard.GetProperty("libraryName").GetString());
        Assert.True(showCard.GetProperty("hasFile").GetBoolean());
    }

    [Fact]
    public async Task The_rename_previews_name_the_folder_change_they_would_make_and_touch_nothing_on_disk()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, folderName: "Dune");
        var named = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010, film.LibraryId);
        var show = await ApiScenes.ShowOnDiskAsync(app);

        var willRename = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/rename-preview");
        var alreadyNamed = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{named.MovieId}/rename-preview");
        var showPreview = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/rename-preview");

        Assert.Equal("will-rename", willRename.GetProperty("outcome").GetString());
        Assert.Equal("Dune", willRename.GetProperty("currentFolder").GetString());
        Assert.Equal("Dune (2021)", willRename.GetProperty("proposedFolder").GetString());
        Assert.True(willRename.GetProperty("willChange").GetBoolean());
        Assert.Equal("already-named", alreadyNamed.GetProperty("outcome").GetString());
        Assert.False(alreadyNamed.GetProperty("willChange").GetBoolean());
        Assert.Equal("will-rename", showPreview.GetProperty("outcome").GetString());
        Assert.Equal("The Wire", showPreview.GetProperty("currentFolder").GetString());
        Assert.Equal("The Wire (2002)", showPreview.GetProperty("proposedFolder").GetString());
        Assert.True(Directory.Exists(film.FolderPath), "a preview renames nothing");
        Assert.True(File.Exists(film.FilePath));
        Assert.True(Directory.Exists(show.FolderPath));
    }
}
