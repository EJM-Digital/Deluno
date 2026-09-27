using Deluno.Contracts;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #842: a per-file, run-now resync through the real route table. It runs
/// the timing logic synchronously and answers "done" with the result -
/// never "queued", which is what the bulk button already says.
/// </summary>
public sealed class SubtitleResyncApiTests
{
    [Fact]
    public async Task Resyncing_a_file_that_is_no_longer_on_disk_says_so_and_is_done_not_queued()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();

        var id = await ImportAsync(movies, "Dune");
        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id,
            new MediaSubtitleRow(
                "en", "fetched", Forced: false, HearingImpaired: false, FilePath: @"D:\Media\Dune\Dune.en.srt",
                StreamIndex: null, Codec: "srt", Provider: "gestdown"),
            CancellationToken.None);

        var response = await app.Client.PostAsync("/api/movies/" + id + "/subtitles/en/resync", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        // "done", not "queued" - this ran now, synchronously, and reports
        // what it actually found rather than promising a later pass will.
        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.False(body.GetProperty("moved").GetBoolean());
        Assert.Equal("Inconclusive", body.GetProperty("outcome").GetString());
        Assert.Contains("no longer on disk", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Recorded against the row, so the title's own page can show it.
        var row = Assert.Single(await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None));
        Assert.Equal("not_checked", row.TimingStatus);
    }

    [Fact]
    public async Task Resyncing_a_language_nothing_is_held_for_is_a_404()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var id = await ImportAsync(movies, "Dune");

        var response = await app.Client.PostAsync("/api/movies/" + id + "/subtitles/en/resync", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> ImportAsync(IMovieCatalogRepository movies, string title)
    {
        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: 2021,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p",
                    QualityCutoffMet: true,
                    FilePath: $@"D:\Media\{title}\{title}.mkv",
                    FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
        return page.Items.Single(item => item.Title == title).Id;
    }
}
