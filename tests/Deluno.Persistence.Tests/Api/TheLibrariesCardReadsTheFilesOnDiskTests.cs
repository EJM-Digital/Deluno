using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The card that lists each library a film is in (coverage register, area library), on real files in real library folders: a library
/// whose folder holds the film's file is "Has a file", one that holds none is "No file", and a copy whose file was deleted off the
/// disk is "Not wanted here" while the other copy is still managed. What the card draws from this answer is in
/// library-membership-card.test.tsx.
/// </summary>
public sealed class TheLibrariesCardReadsTheFilesOnDiskTests
{
    private static async Task<JsonElement> CardAsync(ApplicationTestHost app, string movieId)
        => await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{movieId}/libraries");

    private static JsonElement Row(JsonElement card, string libraryId)
        => card.EnumerateArray().Single(item => item.GetProperty("libraryId").GetString() == libraryId);

    [Fact]
    public async Task Each_library_the_film_is_in_says_whether_its_own_folder_holds_a_file_for_it_and_a_deleted_copy_is_not_wanted_there_only()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016);
        var uhd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films 4K", "Films 4K");
        var empty = await ApiScenes.CreateLibraryAsync(app, "movies", "Films spare", "Films spare");
        (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/libraries/", new { libraryId = uhd })).EnsureSuccessStatusCode();
        (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/libraries/", new { libraryId = empty })).EnsureSuccessStatusCode();
        // The 4K library holds a copy of its own, a real file in its own folder.
        var uhdFile = Path.Combine(await ApiScenes.LibraryRootAsync(app, uhd), "Arrival (2016)", "Arrival (2016) 2160p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(uhdFile)!);
        await File.WriteAllBytesAsync(uhdFile, [1, 2, 3, 4, 5]);
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_wanted_state SET has_file = 1, file_path = '{uhdFile.Replace("'", "''")}', file_size_bytes = 5 WHERE movie_id = '{film.MovieId}' AND library_id = '{uhd}';");

        var before = await CardAsync(app, film.MovieId);

        Assert.Equal(3, before.GetArrayLength());
        Assert.True(Row(before, film.LibraryId).GetProperty("hasFile").GetBoolean());
        Assert.True(File.Exists(film.FilePath));
        Assert.True(Row(before, uhd).GetProperty("hasFile").GetBoolean());
        Assert.True(File.Exists(uhdFile));
        var none = Row(before, empty);
        Assert.False(none.GetProperty("hasFile").GetBoolean());
        Assert.False(none.GetProperty("notWantedHere").GetBoolean());
        Assert.Equal("Films 4K", Row(before, uhd).GetProperty("libraryName").GetString());

        File.Delete(film.FilePath);
        var cleared = await app.Services.GetRequiredService<IMovieCatalogRepository>().ClearFileAsync(film.MovieId, film.FilePath, CancellationToken.None);
        Assert.True(cleared.OnlyThatLibrary);
        var after = await CardAsync(app, film.MovieId);

        var gone = Row(after, film.LibraryId);
        Assert.False(gone.GetProperty("hasFile").GetBoolean());
        Assert.True(gone.GetProperty("notWantedHere").GetBoolean());
        var kept = Row(after, uhd);
        Assert.True(kept.GetProperty("hasFile").GetBoolean());
        Assert.False(kept.GetProperty("notWantedHere").GetBoolean());
        Assert.True(File.Exists(uhdFile), "the other copy was not touched");
    }

    [Fact]
    public async Task A_film_in_one_library_lists_that_library_alone_and_a_show_lists_its_own_library_too()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995);
        var show = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022);

        var filmCard = await CardAsync(app, film.MovieId);
        var showCard = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/libraries");

        var only = Assert.Single(filmCard.EnumerateArray());
        Assert.Equal(film.LibraryId, only.GetProperty("libraryId").GetString());
        Assert.True(only.GetProperty("hasFile").GetBoolean());
        Assert.False(only.GetProperty("canRemove").GetBoolean(), "the library that holds the file cannot be removed");
        var showRow = Assert.Single(showCard.EnumerateArray());
        Assert.Equal(show.LibraryId, showRow.GetProperty("libraryId").GetString());
        Assert.True(showRow.GetProperty("hasFile").GetBoolean());
    }
}
