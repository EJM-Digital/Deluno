using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Re-deciding a library's titles (what a changed plan runs) reads whether each title is out yet: a film from its release dates,
/// a show from its earliest air date. Driven on the real databases through the real routes.
/// </summary>
public sealed class TheRedecisionKnowsWhetherATitleIsOutTests
{
    private static Task<WantedReevaluationResult> RedecideAsync(ApplicationTestHost app, MediaKind kind, string libraryId)
        => app.Services.GetRequiredService<IMediaStateRepository>().ReevaluateWantedStateAsync(kind, new WantedReevaluationScope(LibraryId: libraryId), "WEB 1080p", true, false, CancellationToken.None);

    [Fact]
    public async Task A_film_not_released_yet_is_upcoming_and_becomes_missing_when_its_date_has_passed_and_a_show_is_judged_on_its_earliest_air_date()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var filmLibrary = await ApiScenes.CreateLibraryAsync(app, "movies");
        var film = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId = filmLibrary });
        Assert.True(film.IsSuccessStatusCode, await film.Content.ReadAsStringAsync());
        var filmId = (await film.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        string FilmStatus() => ApiScenes.TextAsync(app, "movies", $"SELECT wanted_status FROM movie_wanted_state WHERE movie_id = '{filmId}';").GetAwaiter().GetResult()!;

        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET minimum_availability = 'released', in_cinemas_date = NULL, physical_release_date = NULL, digital_release_date = '{DateTime.UtcNow.AddYears(1):yyyy-MM-dd}' WHERE id = '{filmId}';");
        await RedecideAsync(app, MediaKind.Movie, filmLibrary);
        Assert.Equal("upcoming", FilmStatus());

        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_entries SET digital_release_date = '{DateTime.UtcNow.AddYears(-1):yyyy-MM-dd}' WHERE id = '{filmId}';");
        await RedecideAsync(app, MediaKind.Movie, filmLibrary);
        Assert.Equal("missing", FilmStatus());

        var showLibrary = await ApiScenes.CreateLibraryAsync(app, "tv");
        var catalogue = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        await catalogue.ImportExistingAsync(
            showLibrary, "Breaking Bad", 2008, "missing", "Wanted.", null, "WEB 1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: false)], CancellationToken.None);
        var seriesId = (await catalogue.ListAsync(CancellationToken.None)).Single(item => item.Title == "Breaking Bad").Id;
        string ShowStatus() => ApiScenes.TextAsync(app, "series", $"SELECT wanted_status FROM series_wanted_state WHERE series_id = '{seriesId}';").GetAwaiter().GetResult()!;

        // The row ImportExistingAsync writes for a show with no file on disk reads as having one; a show nothing has downloaded does not.
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE series_wanted_state SET has_file = 0 WHERE series_id = '{seriesId}';");
        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE episode_entries SET air_date_utc = '{DateTime.UtcNow.AddYears(2):O}' WHERE series_id = '{seriesId}';");
        await RedecideAsync(app, MediaKind.Series, showLibrary);
        Assert.Equal("upcoming", ShowStatus());

        await ApiScenes.ExecuteAsync(app, "series", $"UPDATE episode_entries SET air_date_utc = '{DateTime.UtcNow.AddYears(-2):O}' WHERE series_id = '{seriesId}';");
        await RedecideAsync(app, MediaKind.Series, showLibrary);
        Assert.Equal("missing", ShowStatus());
    }
}
