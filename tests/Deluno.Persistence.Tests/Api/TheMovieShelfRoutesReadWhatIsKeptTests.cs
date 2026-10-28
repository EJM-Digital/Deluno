using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the film shelf's read routes, asked about films that really are in the catalogue (three that differ in title and year, put there
/// through the real import): the paged list filters, sorts, pages and counts them; the compact lists answer for the ids asked and leave a
/// gone one out; the wanted summary counts what is held.
/// </summary>
public sealed class TheMovieShelfRoutesReadWhatIsKeptTests
{
    private static async Task<(ApplicationTestHost App, string Dune, string Alien, string Heat)> ThreeFilmsAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        var dune = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, library);
        var alien = await ApiScenes.FilmOnDiskAsync(app, "Alien", 1979, library);
        var heat = await ApiScenes.FilmOnDiskAsync(app, "Heat", 1995, library);
        return (app, dune.MovieId, alien.MovieId, heat.MovieId);
    }

    private static string[] Titles(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task The_paged_film_list_sorts_filters_pages_and_counts_the_films_in_the_catalogue()
    {
        var (app, _, _, _) = await ThreeFilmsAsync();
        await using var _app = app;

        var byTitle = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?sort=title&direction=asc");
        Assert.Equal(["Alien", "Dune", "Heat"], Titles(byTitle));
        Assert.Equal(3, byTitle.GetProperty("totalCount").GetInt32());
        Assert.False(byTitle.GetProperty("hasMore").GetBoolean());

        var newestFirst = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?sort=year&direction=desc");
        Assert.Equal(["Dune", "Heat", "Alien"], Titles(newestFirst));

        var searched = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?search=dun");
        Assert.Equal(["Dune"], Titles(searched));
        Assert.Equal(1, searched.GetProperty("totalCount").GetInt32());

        var filtered = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?minYear=1990&sort=title&direction=asc");
        Assert.Equal(["Dune", "Heat"], Titles(filtered));

        var first = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/page?sort=title&direction=asc&pageSize=2");
        Assert.Equal(["Alien", "Dune"], Titles(first));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        var second = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/page?sort=title&direction=asc&pageSize=2&pageToken={Uri.EscapeDataString(first.GetProperty("nextPageToken").GetString()!)}");
        Assert.Equal(["Heat"], Titles(second));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task The_compact_lists_answer_for_the_films_asked_and_leave_a_gone_one_out()
    {
        var (app, dune, alien, _) = await ThreeFilmsAsync();
        await using var _app = app;

        var titles = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/titles?ids={dune},{alien},no-such-film");
        Assert.Equal("Dune", titles.GetProperty(dune).GetProperty("title").GetString());
        Assert.Equal(2021, titles.GetProperty(dune).GetProperty("year").GetInt32());
        Assert.Equal("Alien", titles.GetProperty(alien).GetProperty("title").GetString());
        Assert.Equal(1979, titles.GetProperty(alien).GetProperty("year").GetInt32());
        Assert.False(titles.TryGetProperty("no-such-film", out _));

        var posters = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/posters?ids={dune},no-such-film");
        Assert.True(posters.TryGetProperty(dune, out _));
        Assert.False(posters.TryGetProperty("no-such-film", out _));
    }

    [Fact]
    public async Task The_wanted_summary_counts_the_films_held_and_the_one_still_missing()
    {
        var (app, _, _, _) = await ThreeFilmsAsync();
        await using var _app = app;
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var libraryId = (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray().First().GetProperty("id").GetString()!;
        await movies.ImportExistingAsync(libraryId, "Tron", 1982, wantedStatus: "missing", wantedReason: "Not here yet.", currentQuality: null, targetQuality: "Bluray-1080p",
            qualityCutoffMet: false, filePath: null, fileSizeBytes: null, cancellationToken: CancellationToken.None);

        var wanted = await app.Client.GetFromJsonAsync<JsonElement>("/api/movies/wanted");

        Assert.Equal(3, wanted.GetProperty("coveredCount").GetInt32());
        Assert.Equal(1, wanted.GetProperty("missingCount").GetInt32());
        Assert.Equal(0, wanted.GetProperty("upgradeCount").GetInt32());
        Assert.Contains("Tron", wanted.GetProperty("recentItems").EnumerateArray().Select(item => item.GetProperty("title").GetString()));
    }
}
