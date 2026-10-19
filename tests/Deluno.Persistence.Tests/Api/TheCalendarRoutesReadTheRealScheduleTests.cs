using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What the Calendar screen reads (coverage register, area library): GET /api/movies/calendar places a film on each of its
/// cinema, digital and physical dates, and GET /api/series/calendar lists the episodes airing in the window, monitored or not,
/// with the stored wanted status. Real host, real databases.
/// </summary>
public sealed class TheCalendarRoutesReadTheRealScheduleTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    private static async Task<JsonElement[]> MoviesAsync(ApplicationTestHost app, string from, string to)
    {
        var response = await app.Client.GetAsync($"/api/movies/calendar?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
    }

    [Fact]
    public async Task A_film_appears_on_its_cinema_digital_and_physical_dates_each_as_its_own_entry_and_only_inside_the_window()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Arrival", releaseYear = 2026, monitored = true, libraryId, searchForMissingNow = false });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await movies.UpdateReleaseDatesAsync(id, Start.AddDays(2), Start.AddDays(9), Start.AddDays(40), CancellationToken.None);

        var window = await MoviesAsync(app, "2026-11-01", "2026-11-20");
        var wide = await MoviesAsync(app, "2026-11-01", "2027-01-31");

        Assert.Equal(["digital", "inCinemas"], window.Select(entry => entry.GetProperty("kind").GetString()!).Order());
        Assert.Equal("2026-11-03", window.Single(entry => entry.GetProperty("kind").GetString() == "inCinemas").GetProperty("date").GetString());
        Assert.Equal("2026-11-10", window.Single(entry => entry.GetProperty("kind").GetString() == "digital").GetProperty("date").GetString());
        Assert.All(window, entry =>
        {
            Assert.Equal(id, entry.GetProperty("movieId").GetString());
            Assert.Equal("Arrival", entry.GetProperty("title").GetString());
            Assert.True(entry.GetProperty("monitored").GetBoolean());
            Assert.False(entry.GetProperty("hasFile").GetBoolean());
        });
        Assert.Equal(["digital", "inCinemas", "physical"], wide.Select(entry => entry.GetProperty("kind").GetString()!).Order());
        Assert.Equal("2026-12-11", wide.Single(entry => entry.GetProperty("kind").GetString() == "physical").GetProperty("date").GetString());
    }

    [Fact]
    public async Task An_unmonitored_film_is_still_on_the_calendar_marked_unmonitored_and_a_bad_window_is_refused_in_words()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Heat", releaseYear = 2026, monitored = false, libraryId, searchForMissingNow = false });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await movies.UpdateReleaseDatesAsync(id, null, Start.AddDays(3), null, CancellationToken.None);

        var entries = await MoviesAsync(app, "2026-11-01", "2026-11-30");
        var backwards = await app.Client.GetAsync("/api/movies/calendar?from=2026-11-30&to=2026-11-01");

        var entry = Assert.Single(entries);
        Assert.False(entry.GetProperty("monitored").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Contains("The end of the window must be after the start.", await backwards.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_series_calendar_lists_the_episodes_airing_in_the_window_monitored_or_not_with_their_wanted_status_and_leaves_out_the_rest()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "tv");
        async Task<string> ShowAsync(string title, bool monitored)
        {
            var response = await app.Client.PostAsJsonAsync("/api/series", new { title, startYear = 2026, monitored, libraryId, searchForMissingNow = false });
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        var watched = await ShowAsync("Severance", true);
        var skipped = await ShowAsync("Andor", false);
        var aired = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        await series.SyncEpisodeCatalogueAsync(watched,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, aired), new CatalogueEpisodeItem(1, 2, "Later", null, aired.AddDays(60))],
            "tmdb", CancellationToken.None);
        await series.SyncEpisodeCatalogueAsync(skipped, [new CatalogueEpisodeItem(1, 1, "Opening", null, aired.AddDays(1))], "tmdb", CancellationToken.None);

        var response = await app.Client.GetAsync("/api/series/calendar?from=2026-11-01T00:00:00Z&to=2026-11-30T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var episodes = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(["Andor", "Severance"], episodes.Select(episode => episode.GetProperty("seriesTitle").GetString()!).Order());
        var pilot = episodes.Single(episode => episode.GetProperty("seriesTitle").GetString() == "Severance");
        Assert.Equal(watched, pilot.GetProperty("seriesId").GetString());
        Assert.Equal(1, pilot.GetProperty("seasonNumber").GetInt32());
        Assert.Equal(1, pilot.GetProperty("episodeNumber").GetInt32());
        Assert.True(pilot.GetProperty("monitored").GetBoolean());
        Assert.False(pilot.GetProperty("hasFile").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(pilot.GetProperty("wantedStatus").GetString()));
        Assert.False(episodes.Single(episode => episode.GetProperty("seriesTitle").GetString() == "Andor").GetProperty("monitored").GetBoolean());
        Assert.DoesNotContain(episodes, episode => episode.GetProperty("title").GetString() == "Later");
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/series/calendar?from=2026-11-30T00:00:00Z&to=2026-11-01T00:00:00Z")).StatusCode);
    }
}
