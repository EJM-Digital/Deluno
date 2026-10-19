using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Preview rename" for several films or shows (coverage register, area api): it says what each folder would be called, from the saved
/// folder format or the one typed in, and it only says. Run on real films and shows on disk, the folders and files are where they were
/// afterwards, and a title that is not there is left out of the answer.
/// </summary>
public sealed class BulkRenamePreviewsChangeNothingOnDiskTests
{
    [Fact]
    public async Task Previewing_film_folder_names_uses_the_saved_format_or_the_one_typed_skips_a_film_that_is_gone_and_moves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var dune = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var arrival = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, dune.LibraryId);

        var saved = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = new[] { dune.MovieId, arrival.MovieId, "no-such-film" } });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        var previews = body.GetProperty("previews").EnumerateArray().ToDictionary(item => item.GetProperty("title").GetString()!, item => item.GetProperty("proposedName").GetString()!);
        Assert.Equal("Dune (2021)", previews["Dune"]);
        Assert.Equal("Arrival (2016)", previews["Arrival"]);

        var typed = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = new[] { dune.MovieId }, template = "Dune Renamed ({Release Year})" });
        var typedBody = await typed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Dune Renamed (2021)", Assert.Single(typedBody.GetProperty("previews").EnumerateArray()).GetProperty("proposedName").GetString());

        Assert.True(File.Exists(dune.FilePath), "a preview moves nothing");
        Assert.True(File.Exists(arrival.FilePath));
        Assert.True(Directory.Exists(dune.FolderPath));
        Assert.Equal(dune.FilePath, (await app.Services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>().GetByIdAsync(dune.MovieId, CancellationToken.None))!.FilePath);
    }

    [Fact]
    public async Task Previewing_with_no_film_chosen_is_refused_in_plain_words_and_names_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/movies/bulk/rename-preview", new { movieIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose at least one movie to preview rename output.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Previewing_show_folder_names_uses_the_saved_format_or_the_one_typed_skips_a_show_that_is_gone_and_moves_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var wire = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var deadwood = await ApiScenes.ShowOnDiskAsync(app, "Deadwood", 2004, wire.LibraryId);

        var saved = await app.Client.PostAsJsonAsync("/api/series/bulk/rename-preview", new { seriesIds = new[] { wire.SeriesId, deadwood.SeriesId, "no-such-show" } });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        var previews = body.GetProperty("previews").EnumerateArray().ToDictionary(item => item.GetProperty("title").GetString()!, item => item.GetProperty("proposedName").GetString()!);
        Assert.Equal("The Wire (2002)", previews["The Wire"]);
        Assert.Equal("Deadwood (2004)", previews["Deadwood"]);

        var typed = await app.Client.PostAsJsonAsync("/api/series/bulk/rename-preview", new { seriesIds = new[] { wire.SeriesId }, template = "Wire ({Release Year})" });
        var typedBody = await typed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Wire (2002)", Assert.Single(typedBody.GetProperty("previews").EnumerateArray()).GetProperty("proposedName").GetString());

        Assert.True(File.Exists(wire.FilePath), "a preview moves nothing");
        Assert.True(File.Exists(deadwood.FilePath));
        Assert.True(Directory.Exists(wire.FolderPath));
    }

    [Fact]
    public async Task Previewing_with_no_show_chosen_is_refused_in_plain_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/series/bulk/rename-preview", new { seriesIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose at least one series to preview rename output.", await response.Content.ReadAsStringAsync());
    }
}
