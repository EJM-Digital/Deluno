using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: clearing the combined Indexer History never removes the search history the film and show pages show; those are read back
/// over their own routes before and after the clear and are the same.
/// </summary>
public sealed class ClearingIndexerHistoryKeepsTheTitlesOwnSearchHistoryTests
{
    [Fact]
    public async Task The_film_and_show_search_histories_read_back_the_same_after_the_combined_feed_is_cleared()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movieLibrary = await ApiScenes.CreateLibraryAsync(app, "movies");
        var tvLibrary = await ApiScenes.CreateLibraryAsync(app, "tv");
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, movieLibrary);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002, tvLibrary);
        var now = DateTimeOffset.UtcNow;
        await app.Services.GetRequiredService<IMovieCatalogRepository>().RecordSearchAttemptAsync(
            film.MovieId, movieLibrary, "manual", "matched", now.AddMinutes(-5), null, null, "Dune.2021.1080p", "NZBgeek", null, CancellationToken.None);
        await app.Services.GetRequiredService<ISeriesCatalogRepository>().RecordSearchAttemptAsync(
            show.SeriesId, null, tvLibrary, "manual", "checked", now.AddMinutes(-4), null, null, "The.Wire.S01.1080p", "NZBgeek", null, CancellationToken.None);
        var filmBefore = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/search-history?movieId={film.MovieId}")).GetRawText();
        var showBefore = (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/search-history?seriesId={show.SeriesId}")).GetRawText();
        Assert.Contains("Dune.2021.1080p", filmBefore, StringComparison.Ordinal);
        Assert.Contains("The.Wire.S01.1080p", showBefore, StringComparison.Ordinal);
        Assert.NotEmpty((await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history")).GetProperty("items").EnumerateArray());

        Assert.True((await app.Client.PostAsync("/api/indexer-history/clear", content: null)).IsSuccessStatusCode);

        Assert.Equal(filmBefore, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/search-history?movieId={film.MovieId}")).GetRawText());
        Assert.Equal(showBefore, (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/search-history?seriesId={show.SeriesId}")).GetRawText());
    }
}
