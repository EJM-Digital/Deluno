using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The per-title switches on films and shows over a real host and the real catalogue files: stop or resume monitoring (one and
/// many, a film or an episode), put off the next search, skip the next automatic search once, protect a film's file from a worse
/// copy, set the minimum availability of several films, and acknowledge a metadata notice. Each route says what it did, changes
/// only what it was asked to and leaves every file on disk as it was.
/// </summary>
public sealed class TitleAutomationAndMonitoringRoutesTests
{
    private static async Task<bool> MovieMonitoredAsync(ApplicationTestHost app, string id)
        => (await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None))!.Monitored;

    private static async Task<bool> ShowMonitoredAsync(ApplicationTestHost app, string id)
        => (await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetByIdAsync(id, CancellationToken.None))!.Monitored;

    [Fact]
    public async Task Monitoring_can_be_switched_off_and_on_for_several_films_at_once_and_only_for_the_films_chosen()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var one = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var two = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, one.LibraryId);
        var three = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010, one.LibraryId);

        var off = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { one.MovieId, two.MovieId, one.MovieId }, monitored = false });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(2, (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updated").GetInt32());
        Assert.False(await MovieMonitoredAsync(app, one.MovieId));
        Assert.False(await MovieMonitoredAsync(app, two.MovieId));
        Assert.True(await MovieMonitoredAsync(app, three.MovieId));
        Assert.True(File.Exists(one.FilePath) && File.Exists(two.FilePath), "switching monitoring off never touches a file");

        var on = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { two.MovieId }, monitored = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(await MovieMonitoredAsync(app, two.MovieId));
        Assert.False(await MovieMonitoredAsync(app, one.MovieId));

        var none = await app.Client.PutAsJsonAsync("/api/movies/monitoring", new { movieIds = Array.Empty<string>(), monitored = false });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PutAsJsonAsync("/api/movies/monitoring", new { movieIds = new[] { one.MovieId }, monitored = true })).StatusCode);
        Assert.False(await MovieMonitoredAsync(app, one.MovieId));
    }

    [Fact]
    public async Task Monitoring_can_be_switched_off_and_on_for_several_shows_and_for_single_episodes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var wire = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var other = await ApiScenes.ShowOnDiskAsync(app, "Severance", 2022, wire.LibraryId);

        var off = await app.Client.PutAsJsonAsync("/api/series/monitoring", new { seriesIds = new[] { wire.SeriesId }, monitored = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(await ShowMonitoredAsync(app, wire.SeriesId));
        Assert.True(await ShowMonitoredAsync(app, other.SeriesId));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PutAsJsonAsync("/api/series/monitoring", new { seriesIds = Array.Empty<string>(), monitored = false })).StatusCode);

        var episodes = await app.Client.PutAsJsonAsync("/api/series/episodes/monitoring", new { episodeIds = new[] { other.EpisodeId }, monitored = false });
        Assert.Equal(HttpStatusCode.OK, episodes.StatusCode);
        var detail = await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetInventoryDetailAsync(other.SeriesId, CancellationToken.None);
        Assert.False(detail!.Episodes.Single().Monitored);
        Assert.True(File.Exists(other.FilePath), "an unmonitored episode keeps its file");
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PutAsJsonAsync("/api/series/episodes/monitoring", new { episodeIds = Array.Empty<string>(), monitored = true })).StatusCode);

        await app.Client.PutAsJsonAsync("/api/series/episodes/monitoring", new { episodeIds = new[] { other.EpisodeId }, monitored = true });
        Assert.True((await app.Services.GetRequiredService<ISeriesCatalogRepository>().GetInventoryDetailAsync(other.SeriesId, CancellationToken.None))!.Episodes.Single().Monitored);
    }

    [Fact]
    public async Task A_films_next_search_can_be_put_off_or_skipped_once_and_a_title_that_is_not_wanted_has_nothing_to_put_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var covered = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Tenet", releaseYear = 2020, monitored = true, libraryId = covered.LibraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var film = new { MovieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!, LibraryId = covered.LibraryId };
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/movies/{covered.MovieId}/automation/defer", new { libraryId = covered.LibraryId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/movies/{covered.MovieId}/automation/skip-once", new { libraryId = covered.LibraryId })).StatusCode);

        var defer = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = film.LibraryId, hours = 48 });
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        var until = DateTimeOffset.Parse((await defer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetString()!);
        Assert.InRange((until - DateTimeOffset.UtcNow).TotalHours, 47.5, 48.5);
        var stored = await ApiScenes.TextAsync(app, "movies", $"SELECT next_eligible_search_utc FROM movie_wanted_state WHERE movie_id = '{film.MovieId}';");
        Assert.InRange((DateTimeOffset.Parse(stored!) - until).Duration().TotalSeconds, 0, 2);

        var tooLong = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = film.LibraryId, hours = 100000 });
        Assert.InRange((DateTimeOffset.Parse((await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetString()!) - DateTimeOffset.UtcNow).TotalHours, 719, 721);

        var skip = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/skip-once", new { libraryId = film.LibraryId });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        Assert.Contains("skipped", (await skip.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1L, await ApiScenes.ScalarAsync(app, "movies", $"SELECT skip_next_automation_search FROM movie_wanted_state WHERE movie_id = '{film.MovieId}';"));

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/defer", new { libraryId = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/automation/skip-once", new { libraryId = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/movies/no-such-film/automation/defer", new { libraryId = film.LibraryId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/movies/no-such-film/automation/skip-once", new { libraryId = film.LibraryId })).StatusCode);
    }

    [Fact]
    public async Task A_shows_next_search_can_be_put_off_or_skipped_once()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var defer = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/defer", new { libraryId = show.LibraryId });
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        var until = DateTimeOffset.Parse((await defer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deferredUntilUtc").GetString()!);
        Assert.InRange((until - DateTimeOffset.UtcNow).TotalHours, 23.5, 24.5);
        var stored = await ApiScenes.TextAsync(app, "series", $"SELECT next_eligible_search_utc FROM series_wanted_state WHERE series_id = '{show.SeriesId}';");
        Assert.InRange((DateTimeOffset.Parse(stored!) - until).Duration().TotalSeconds, 0, 2);

        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/skip-once", new { libraryId = show.LibraryId })).StatusCode);
        Assert.Equal(1L, await ApiScenes.ScalarAsync(app, "series", $"SELECT skip_next_automation_search FROM series_wanted_state WHERE series_id = '{show.SeriesId}';"));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/automation/defer", new { libraryId = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/series/no-such-show/automation/defer", new { libraryId = show.LibraryId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/series/no-such-show/automation/skip-once", new { libraryId = show.LibraryId })).StatusCode);
    }

    [Fact]
    public async Task A_films_file_can_be_protected_from_a_worse_copy_and_the_protection_can_be_taken_off()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);

        var on = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/replacement-protection", new { preventLowerQualityReplacements = true });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.Equal(1L, await ApiScenes.ScalarAsync(app, "movies", $"SELECT prevent_lower_quality_replacements FROM movie_wanted_state WHERE movie_id = '{film.MovieId}';"));

        var off = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/replacement-protection", new { preventLowerQualityReplacements = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.Equal(0L, await ApiScenes.ScalarAsync(app, "movies", $"SELECT prevent_lower_quality_replacements FROM movie_wanted_state WHERE movie_id = '{film.MovieId}';"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/movies/no-such-film/replacement-protection", new { preventLowerQualityReplacements = true })).StatusCode);
        Assert.True(File.Exists(film.FilePath));
    }

    [Fact]
    public async Task The_minimum_availability_of_several_films_is_set_together_and_a_word_nobody_knows_is_refused_without_changing_any()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var one = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var two = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, one.LibraryId);

        var set = await app.Client.PostAsJsonAsync("/api/movies/bulk/minimum-availability", new { movieIds = new[] { one.MovieId, two.MovieId, "no-such-film" }, minimumAvailability = "inCinemas" });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var body = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("updated").GetInt32());
        Assert.Equal("inCinemas", body.GetProperty("minimumAvailability").GetString());
        Assert.Equal("inCinemas", await ApiScenes.TextAsync(app, "movies", $"SELECT minimum_availability FROM movie_entries WHERE id = '{one.MovieId}';"));

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/movies/bulk/minimum-availability", new { movieIds = new[] { one.MovieId }, minimumAvailability = "someday" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync("/api/movies/bulk/minimum-availability", new { movieIds = Array.Empty<string>(), minimumAvailability = "released" })).StatusCode);
        Assert.Equal("inCinemas", await ApiScenes.TextAsync(app, "movies", $"SELECT minimum_availability FROM movie_entries WHERE id = '{two.MovieId}';"));
    }

    [Fact]
    public async Task Acknowledging_a_metadata_notice_for_a_title_with_none_answers_204_and_for_a_title_that_is_not_there_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync($"/api/movies/{film.MovieId}/metadata/issue/acknowledge", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/movies/no-such-film/metadata/issue/acknowledge", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync($"/api/series/{show.SeriesId}/metadata/issue/acknowledge", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/series/no-such-show/metadata/issue/acknowledge", content: null)).StatusCode);
    }
}
