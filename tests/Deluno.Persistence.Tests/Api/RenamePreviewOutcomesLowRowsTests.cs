using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The wording of a title's rename preview for the outcomes besides "will rename" (#1329), on both engines, read over HTTP from
/// real library folders: no file on record yet, the folder already has the right name, the proposed name is taken by another
/// folder, and an unknown id is a 404. The preview never touches the disk.
/// </summary>
public sealed class RenamePreviewOutcomesLowRowsTests
{
    private static async Task<JsonElement> PreviewAsync(ApplicationTestHost app, string route, string id)
        => await app.Client.GetFromJsonAsync<JsonElement>($"/api/{route}/{id}/rename-preview");

    [Fact]
    public async Task A_film_with_no_file_on_record_says_so_and_a_folder_with_the_right_name_or_a_taken_name_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "No File Yet", releaseYear = 2019, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var noFileId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var named = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010, libraryId);
        var taken = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId, folderName: "Dune");
        var blocker = Directory.CreateDirectory(Path.Combine(root, "Dune (2021)")).FullName;

        var noFile = await PreviewAsync(app, "movies", noFileId);
        var alreadyNamed = await PreviewAsync(app, "movies", named.MovieId);
        var nameTaken = await PreviewAsync(app, "movies", taken.MovieId);

        Assert.Equal("no-files", noFile.GetProperty("outcome").GetString());
        Assert.False(noFile.GetProperty("willChange").GetBoolean());
        Assert.Equal("There is no file on record for this title yet, so there is no folder to rename.", noFile.GetProperty("message").GetString());
        Assert.Equal("already-named", alreadyNamed.GetProperty("outcome").GetString());
        Assert.Equal("The folder already has the name your naming settings give it. Nothing would change.", alreadyNamed.GetProperty("message").GetString());
        Assert.Equal("name-taken", nameTaken.GetProperty("outcome").GetString());
        Assert.False(nameTaken.GetProperty("willChange").GetBoolean());
        Assert.Equal("Dune", nameTaken.GetProperty("currentFolder").GetString());
        Assert.Equal("A folder named \"Dune (2021)\" already exists, so Deluno would leave this one alone.", nameTaken.GetProperty("message").GetString());
        Assert.True(Directory.Exists(taken.FolderPath));
        Assert.True(Directory.Exists(blocker));
        Assert.True(File.Exists(taken.FilePath));
    }

    [Fact]
    public async Task A_show_with_no_file_on_record_says_so_and_a_folder_with_the_right_name_or_a_taken_name_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var empty = await series.AddAsync(new CreateSeriesRequest("No Episodes Yet", 2020, null), CancellationToken.None);
        var named = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022, libraryId, folderName: "Severance (2022)");
        var taken = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002, libraryId, folderName: "The Wire");
        var blocker = Directory.CreateDirectory(Path.Combine(root, "The Wire (2002)")).FullName;

        var noFile = await PreviewAsync(app, "series", empty.Id);
        var alreadyNamed = await PreviewAsync(app, "series", named.SeriesId);
        var nameTaken = await PreviewAsync(app, "series", taken.SeriesId);

        Assert.Equal("no-files", noFile.GetProperty("outcome").GetString());
        Assert.False(noFile.GetProperty("willChange").GetBoolean());
        Assert.Equal("already-named", alreadyNamed.GetProperty("outcome").GetString());
        Assert.False(alreadyNamed.GetProperty("willChange").GetBoolean());
        Assert.Equal("name-taken", nameTaken.GetProperty("outcome").GetString());
        Assert.False(nameTaken.GetProperty("willChange").GetBoolean());
        Assert.Equal("A folder named \"The Wire (2002)\" already exists, so Deluno would leave this one alone.", nameTaken.GetProperty("message").GetString());
        Assert.True(Directory.Exists(taken.FolderPath));
        Assert.True(Directory.Exists(blocker));
        Assert.True(File.Exists(taken.FilePath));
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("series")]
    public async Task An_unknown_id_is_a_404_on_both_engines(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync($"/api/{route}/no-such-title/rename-preview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
