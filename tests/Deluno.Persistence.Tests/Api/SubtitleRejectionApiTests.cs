using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #922's one-click "This is out of sync", through the real route table.
///
/// <para>Reuses #897's own mechanism: blocklist, drop the held row, and say
/// so in Activity — the same three things an automatic mismatch does, just
/// triggered by a person's own judgment instead of the correlation.</para>
/// </summary>
public sealed class SubtitleRejectionApiTests
{
    [Fact]
    public async Task Rejecting_a_held_subtitle_blocklists_it_and_drops_the_row()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddMovieAsync(app);

        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id,
            new MediaSubtitleRow(
                "en", "fetched", Forced: false, HearingImpaired: false, FilePath: "x.en.srt",
                StreamIndex: null, Codec: "srt", Provider: "gestdown", SourceKey: "gestdown|abc123"),
            CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/movies/{id}/subtitles/en/reject", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var held = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.Empty(held);

        var blocked = await subtitles.ListBlockedAsync(MediaKind.Movie, id, CancellationToken.None);
        var entry = Assert.Single(blocked);
        Assert.Equal("gestdown|abc123", entry.BlockKey);

        var activityFeed = app.Services.GetRequiredService<IActivityFeedRepository>();
        var activity = await activityFeed.ListActivityAsync(50, "movie", id, CancellationToken.None);
        Assert.Contains(activity, item => item.Category == "subtitle.rejected");
    }

    [Fact]
    public async Task Rejecting_a_language_nothing_is_held_for_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddMovieAsync(app);

        var response = await app.Client.PostAsync($"/api/movies/{id}/subtitles/en/reject", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rejecting_a_subtitle_for_a_title_that_does_not_exist_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/movies/does-not-exist/subtitles/en/reject", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync(
            "/api/movies/", new { title = "Arrival", releaseYear = 2016, monitored = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
