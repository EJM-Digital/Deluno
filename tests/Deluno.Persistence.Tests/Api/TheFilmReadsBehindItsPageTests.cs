using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The reads a film's page makes: its held subtitles, and each read answering with that film's rows only.</summary>
public sealed class TheFilmReadsBehindItsPageTests
{
    [Fact]
    public async Task A_films_held_subtitles_name_each_track_by_language_source_provider_source_key_and_timing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        var film = await ApiScenes.FilmOnDiskAsync(app);
        var filmId = film.MovieId;
        var other = (await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, film.LibraryId)).MovieId;
        await subtitles.RecordFetchedAsync(MediaKind.Movie, filmId, new MediaSubtitleRow("en", "sidecar", false, true, "/media/Dune/Dune.en.srt", null, "srt", "gestdown", 2, "dune.1080p.web"), CancellationToken.None);
        await subtitles.RecordTimingStatusAsync(MediaKind.Movie, filmId, "en", false, true, "moved", "It was 2 seconds late, so it was moved.", CancellationToken.None);
        await subtitles.RecordFetchedAsync(MediaKind.Movie, other, new MediaSubtitleRow("de", "sidecar", false, false, "/media/Other/Other.de.srt", null, "srt", "gestdown", 1), CancellationToken.None);

        var held = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{filmId}/subtitles");

        var track = Assert.Single(held.EnumerateArray());
        Assert.Equal("en", track.GetProperty("language").GetString());
        Assert.Equal("sidecar", track.GetProperty("source").GetString());
        Assert.Equal("gestdown", track.GetProperty("provider").GetString());
        Assert.True(track.GetProperty("hearingImpaired").GetBoolean());
        Assert.False(track.GetProperty("forced").GetBoolean());
        Assert.Equal("srt", track.GetProperty("codec").GetString());
        Assert.Equal("dune.1080p.web", track.GetProperty("sourceKey").GetString());
        Assert.Equal("moved", track.GetProperty("timingStatus").GetString());
        Assert.Equal("It was 2 seconds late, so it was moved.", track.GetProperty("timingReason").GetString());
    }
}
