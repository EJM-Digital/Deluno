using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Undo of a library reassignment that moved files (coverage register, area library): the screen's Undo is the same reassignment
/// sent the other way with the same "move the files" choice, so this drives that pair over real folders. The folder, the video, the
/// subtitle beside it and the stored path all return to where they began, and a second film in the same folder is left alone.
/// </summary>
public sealed class AReassignedFilmCanBeMovedBackTests
{
    [Fact]
    public async Task Moving_a_film_to_another_library_and_then_back_returns_its_folder_its_files_and_its_stored_path_to_where_they_began()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var filmsId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var kidsId = await ApiScenes.CreateLibraryAsync(app, "movies", "Kids", "Kids");
        var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: filmsId);
        var subtitle = Path.Combine(film.FolderPath, "Dune.en.srt");
        await File.WriteAllTextAsync(subtitle, "subtitle");
        var videoBytes = await File.ReadAllBytesAsync(film.FilePath);
        var kidsRoot = await ApiScenes.LibraryRootAsync(app, kidsId);

        var forward = await ReassignAsync(app, film.MovieId, filmsId, kidsId);

        Assert.Equal(1, forward.GetProperty("filesMoved").GetInt32());
        Assert.False(Directory.Exists(film.FolderPath), "the folder left the first library");
        Assert.True(Directory.Exists(Path.Combine(kidsRoot, Path.GetFileName(film.FolderPath))), "and arrived in the second");

        var back = await ReassignAsync(app, film.MovieId, kidsId, filmsId);

        Assert.Equal(1, back.GetProperty("filesMoved").GetInt32());
        Assert.True(File.Exists(film.FilePath), "the video is back at its first path");
        Assert.Equal(videoBytes, await File.ReadAllBytesAsync(film.FilePath));
        Assert.True(File.Exists(subtitle), "the subtitle beside it came back too");
        Assert.False(Directory.Exists(Path.Combine(kidsRoot, Path.GetFileName(film.FolderPath))), "nothing is left in the second library");
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}");
        Assert.Equal(film.FilePath, detail.GetProperty("filePath").GetString());
        Assert.Equal(filmsId, detail.GetProperty("libraryId").GetString());
    }

    [Fact]
    public async Task Moving_back_a_film_whose_old_folder_name_is_taken_refuses_and_loses_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var filmsId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var kidsId = await ApiScenes.CreateLibraryAsync(app, "movies", "Kids", "Kids");
        var film = await ApiScenes.FilmOnDiskAsync(app, libraryId: filmsId);
        var kidsRoot = await ApiScenes.LibraryRootAsync(app, kidsId);
        await ReassignAsync(app, film.MovieId, filmsId, kidsId);
        Directory.CreateDirectory(film.FolderPath);
        await File.WriteAllTextAsync(Path.Combine(film.FolderPath, "someone-elses.mkv"), "x");

        var back = await ReassignAsync(app, film.MovieId, kidsId, filmsId);

        Assert.Equal(0, back.GetProperty("filesMoved").GetInt32());
        Assert.True(File.Exists(Path.Combine(kidsRoot, Path.GetFileName(film.FolderPath), Path.GetFileName(film.FilePath))), "the film's files stay where they are");
        Assert.True(File.Exists(Path.Combine(film.FolderPath, "someone-elses.mkv")), "the other folder is untouched");
    }

    private static async Task<JsonElement> ReassignAsync(ApplicationTestHost app, string movieId, string fromId, string toId)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies/bulk/reassign-library", new { movieIds = new[] { movieId }, fromLibraryId = fromId, toLibraryId = toId, moveFiles = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
