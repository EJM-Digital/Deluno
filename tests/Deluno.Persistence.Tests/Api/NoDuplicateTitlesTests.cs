using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The same title cannot be added twice.
///
/// <para><b>Why this file exists.</b> Detection (#419) finds duplicates after
/// the fact; this is the half that stops them existing. The product owner's
/// requirement is the plain one — <i>we can't have the same title being added
/// twice</i> — and it has to hold for shows as well as films.</para>
///
/// <para>The lookup matches an IMDb id, a metadata provider and its id, or a
/// title and year. The gap it had was a title arriving <em>without</em> a year:
/// "Big Buck Bunny" and "Big Buck Bunny (2008)" were two different films, so a
/// caller that omitted the year grew a second row for something already held.
/// A yearless title now matches one that has a year — but only in that
/// direction, because two entries that both carry a year and disagree are a
/// remake.</para>
/// </summary>
public sealed class NoDuplicateTitlesTests
{
    [Fact]
    public async Task Adding_the_same_film_twice_returns_the_one_already_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await AddMovieAsync(app, new { title = "Arrival", releaseYear = 2016, imdbId = "tt2543164", monitored = true });
        var second = await AddMovieAsync(app, new { title = "Arrival", releaseYear = 2016, imdbId = "tt2543164", monitored = true });

        Assert.Equal(first, second);
        Assert.Single(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Adding_the_same_film_without_its_id_still_finds_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await AddMovieAsync(app, new { title = "Arrival", releaseYear = 2016, imdbId = "tt2543164", monitored = true });
        var second = await AddMovieAsync(app, new { title = "Arrival", releaseYear = 2016, monitored = true });

        Assert.Equal(first, second);
        Assert.Single(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// The gap that produced two "Big Buck Bunny" rows on the lab: a caller that
    /// leaves the year out.
    /// </summary>
    [Fact]
    public async Task Adding_a_film_without_a_year_matches_the_one_that_has_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await AddMovieAsync(app, new { title = "Big Buck Bunny", releaseYear = 2008, monitored = true });
        var second = await AddMovieAsync(app, new { title = "Big Buck Bunny", monitored = true });

        Assert.Equal(first, second);
        Assert.Single(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// A remake is not a duplicate. Both carry a year and the years disagree, so
    /// collapsing them would lose a film the owner asked for.
    /// </summary>
    [Fact]
    public async Task Two_films_with_the_same_name_and_different_years_are_both_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var original = await AddMovieAsync(app, new { title = "Dune", releaseYear = 1984, monitored = true });
        var remake = await AddMovieAsync(app, new { title = "Dune", releaseYear = 2021, monitored = true });

        Assert.NotEqual(original, remake);
        Assert.Equal(2, (await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Adding_the_same_show_twice_returns_the_one_already_there()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await AddSeriesAsync(app, new { title = "Severance", startYear = 2022, imdbId = "tt11280740", monitored = true });
        var second = await AddSeriesAsync(app, new { title = "Severance", startYear = 2022, imdbId = "tt11280740", monitored = true });

        Assert.Equal(first, second);
        Assert.Single(await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Adding_a_show_without_a_year_matches_the_one_that_has_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var first = await AddSeriesAsync(app, new { title = "Severance", startYear = 2022, monitored = true });
        var second = await AddSeriesAsync(app, new { title = "Severance", monitored = true });

        Assert.Equal(first, second);
        Assert.Single(await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Two_shows_with_the_same_name_and_different_years_are_both_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var original = await AddSeriesAsync(app, new { title = "Doctor Who", startYear = 1963, monitored = true });
        var revival = await AddSeriesAsync(app, new { title = "Doctor Who", startYear = 2005, monitored = true });

        Assert.NotEqual(original, revival);
        Assert.Equal(2, (await app.Services.GetRequiredService<ISeriesCatalogRepository>().ListAsync(CancellationToken.None)).Count);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<string> AddMovieAsync(ApplicationTestHost app, object body)
        => AddAsync(app, "/api/movies/", body);

    private static Task<string> AddSeriesAsync(ApplicationTestHost app, object body)
        => AddAsync(app, "/api/series/", body);

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, object body)
    {
        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
