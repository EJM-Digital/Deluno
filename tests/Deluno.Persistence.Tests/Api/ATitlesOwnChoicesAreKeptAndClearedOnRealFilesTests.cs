using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A film's or a show's own choices (coverage register, area api), on a real film and show on disk: the subtitle languages it wants
/// instead of its library's, and how a show's episodes are numbered. Each is read back from the database file, a refusal stores nothing,
/// and clearing or changing a choice never touches a file.
/// </summary>
public sealed class ATitlesOwnChoicesAreKeptAndClearedOnRealFilesTests
{
    [Fact]
    public async Task A_films_own_subtitle_languages_are_stored_cleared_and_never_touch_its_file()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);

        var set = await app.Client.PutAsJsonAsync($"/api/movies/{film.MovieId}/subtitles/override", new { languages = new[] { "ja", "en" }, mode = "first" });

        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.Equal("ja,en", await ApiScenes.TextAsync(app, "movies", $"SELECT languages FROM media_subtitle_override WHERE media_id = '{film.MovieId}'"));
        Assert.Equal("first", await ApiScenes.TextAsync(app, "movies", $"SELECT mode FROM media_subtitle_override WHERE media_id = '{film.MovieId}'"));

        var cleared = await app.Client.DeleteAsync($"/api/movies/{film.MovieId}/subtitles/override");

        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "movies", $"SELECT COUNT(*) FROM media_subtitle_override WHERE media_id = '{film.MovieId}'"));
        Assert.True(File.Exists(film.FilePath), "clearing a choice never touches the film's file");
        var title = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}");
        Assert.Equal(JsonValueKind.Null, title.GetProperty("subtitleLanguagesOverride").ValueKind);
    }

    [Fact]
    public async Task A_shows_own_subtitle_languages_are_stored_cleared_and_never_touch_its_files()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var set = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/subtitles/override", new { languages = new[] { "es" }, mode = "all" });

        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.Equal("es", await ApiScenes.TextAsync(app, "series", $"SELECT languages FROM media_subtitle_override WHERE media_id = '{show.SeriesId}'"));

        var cleared = await app.Client.DeleteAsync($"/api/series/{show.SeriesId}/subtitles/override");

        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Equal(0, await ApiScenes.ScalarAsync(app, "series", $"SELECT COUNT(*) FROM media_subtitle_override WHERE media_id = '{show.SeriesId}'"));
        Assert.True(File.Exists(show.FilePath), "clearing a choice never touches an episode file");
    }

    [Fact]
    public async Task A_shows_numbering_is_chosen_stored_and_read_back_and_a_choice_that_makes_no_sense_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "One Piece", 1999);

        var saved = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/numbering", new
        {
            seriesType = "anime",
            numberingScheme = "absolute",
            numberingSource = "owner",
            mappings = new[] { new { episodeId = show.EpisodeId, absoluteNumber = 1 } }
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var numbering = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/numbering");
        Assert.Equal("anime", numbering.GetProperty("seriesType").GetString());
        Assert.Equal("absolute", numbering.GetProperty("numberingScheme").GetString());
        Assert.Equal("owner", numbering.GetProperty("numberingSource").GetString());
        var episode = Assert.Single(numbering.GetProperty("episodes").EnumerateArray());
        Assert.Equal(1, episode.GetProperty("absoluteNumber").GetInt32());
        Assert.Equal("anime", await ApiScenes.TextAsync(app, "series", $"SELECT series_type FROM series_entries WHERE id = '{show.SeriesId}'"));
        Assert.True(File.Exists(show.FilePath), "choosing a numbering never renames a file");

        var nonsense = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/numbering", new { seriesType = "documentary" });
        var duplicate = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/numbering", new
        {
            mappings = new[] { new { episodeId = show.EpisodeId, absoluteNumber = 1 }, new { episodeId = show.EpisodeId, absoluteNumber = 2 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Contains("Series type must be standard, daily, or anime.", await nonsense.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("Each episode may appear only once", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("anime", await ApiScenes.TextAsync(app, "series", $"SELECT series_type FROM series_entries WHERE id = '{show.SeriesId}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/series/no-such-show/numbering", new { seriesType = "anime" })).StatusCode);
    }
}
