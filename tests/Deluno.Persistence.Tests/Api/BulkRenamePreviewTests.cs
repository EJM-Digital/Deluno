using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Rename preview in the bulk dialog (coverage register, area library): the proposed folder name for each chosen title from the
/// template the person typed, or from the saved naming setting when the box is left empty. Nothing on disk changes; applying is a
/// separate call (BulkTitleRoutesDoWhatTheySayTests).
/// </summary>
public sealed class BulkRenamePreviewTests
{
    [Fact]
    public async Task A_typed_template_is_rendered_for_each_chosen_film_and_the_folder_on_disk_is_left_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dune = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, folderName: "dune old name");
        var heat = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995, dune.LibraryId, folderName: "heat old name");

        var response = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = new[] { dune.MovieId, heat.MovieId, "no-such-film" }, template = "{Movie Title} ({Release Year})" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        var previews = body.GetProperty("previews").EnumerateArray().ToDictionary(item => item.GetProperty("movieId").GetString()!, item => item);
        Assert.Equal("Dune (2021)", previews[dune.MovieId].GetProperty("proposedName").GetString());
        Assert.Equal("Heat (1995)", previews[heat.MovieId].GetProperty("proposedName").GetString());
        Assert.Equal("{Movie Title} ({Release Year})", previews[dune.MovieId].GetProperty("template").GetString());
        Assert.True(Directory.Exists(dune.FolderPath) && Directory.Exists(heat.FolderPath), "a preview never renames a folder");
        Assert.True(File.Exists(dune.FilePath));
    }

    [Fact]
    public async Task A_different_template_gives_a_different_name_and_an_empty_one_uses_the_saved_setting()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);

        async Task<string> NameAsync(string? template)
        {
            var response = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = new[] { film.MovieId }, template });
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previews")[0].GetProperty("proposedName").GetString()!;
        }

        Assert.Equal("Dune - 2021", await NameAsync("{Movie Title} - {Release Year}"));
        var saved = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        var expected = saved.GetProperty("movieFolderFormat").GetString()!;
        var fromSetting = await NameAsync("   ");
        Assert.Equal(await NameAsync(expected), fromSetting);
        Assert.Contains("Dune", fromSetting);
    }

    [Fact]
    public async Task A_typed_template_is_rendered_for_each_chosen_show_and_choosing_nothing_is_refused_in_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022, folderName: "severance old name");

        var response = await app.Client.PostAsJsonAsync("/api/series/bulk/rename-preview", new { seriesIds = new[] { show.SeriesId }, template = "{Series Title} ({Series Year})" });
        var none = await app.Client.PostAsJsonAsync("/api/series/bulk/rename-preview", new { seriesIds = Array.Empty<string>() });
        var noFilms = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = Array.Empty<string>() });

        var preview = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previews")[0];
        Assert.Equal("Severance (2022)", preview.GetProperty("proposedName").GetString());
        Assert.Equal(show.SeriesId, preview.GetProperty("seriesId").GetString());
        Assert.True(Directory.Exists(show.FolderPath), "a preview never renames a folder");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Choose at least one series to preview rename output.", await none.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noFilms.StatusCode);
        Assert.Contains("Choose at least one movie to preview rename output.", await noFilms.Content.ReadAsStringAsync());
    }
}
