using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Prevent automatic re-add" in the Remove dialog (coverage register, area movie-detail): removing a film that an import list
/// added, with the box ticked, writes an exclusion for that list so it does not add the film back; with the box clear it writes none.
/// The film's file stays where it is in both cases, because the dialog's other choice (delete files) was not made.
/// </summary>
public sealed class ARemovedFilmCanBeKeptOutOfItsImportListTests
{
    [Fact]
    public async Task Removing_a_list_added_film_with_prevent_automatic_re_add_ticked_excludes_it_from_that_list_and_leaves_its_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var source = await AddListAsync(app, film.LibraryId);
        await RecordOriginAsync(app, source, film.MovieId, "Dune", 2021);

        var removed = await RemoveAsync(app, film.MovieId, addImportListExclusion: true);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var result = (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal("1", result.GetProperty("metadata").GetProperty("importListExclusionsAdded").GetString());
        var exclusions = (await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=movies")).EnumerateArray().ToArray();
        var excluded = Assert.Single(exclusions);
        Assert.Equal("Dune", excluded.GetProperty("title").GetString());
        Assert.Equal(2021, excluded.GetProperty("year").GetInt32());
        Assert.Equal(source.Id, excluded.GetProperty("sourceId").GetString());
        Assert.True(File.Exists(film.FilePath), "removing the film from Deluno did not touch its file");
        using var gone = await app.Client.GetAsync($"/api/movies/{film.MovieId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Removing_a_list_added_film_with_the_box_clear_adds_no_exclusion()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995);
        var source = await AddListAsync(app, film.LibraryId);
        await RecordOriginAsync(app, source, film.MovieId, "Heat", 1995);

        var removed = await RemoveAsync(app, film.MovieId, addImportListExclusion: false);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=movies")).EnumerateArray());
        Assert.True(File.Exists(film.FilePath));
    }

    [Fact]
    public async Task Ticking_the_box_for_a_film_no_list_added_has_no_list_to_exclude_it_from()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016);

        var removed = await RemoveAsync(app, film.MovieId, addImportListExclusion: true);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/exclusions?mediaType=movies")).EnumerateArray());
        Assert.True(Directory.Exists(film.FolderPath));
    }

    private static Task<HttpResponseMessage> RemoveAsync(ApplicationTestHost app, string movieId, bool addImportListExclusion)
        => app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { movieId }, operation = "remove", deleteFiles = false, addImportListExclusion });

    private static async Task<IntakeSourceItem> AddListAsync(ApplicationTestHost app, string libraryId)
        => await app.Services.GetRequiredService<IIntakeRepository>().CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest("Trending films", "trakt", "https://lists.example.test/trending", "movies", libraryId, null, null, null, null, null, null, null, null, false, true),
            CancellationToken.None);

    private static Task RecordOriginAsync(ApplicationTestHost app, IntakeSourceItem source, string movieId, string title, int year)
        => app.Services.GetRequiredService<IIntakeRepository>().RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(source.Id, source.Name, source.Provider, "movies", movieId, $"{title.ToLowerInvariant()}-{year}", title, year, null),
            CancellationToken.None);
}
