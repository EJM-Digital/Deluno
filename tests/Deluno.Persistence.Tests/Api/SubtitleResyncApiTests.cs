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

    /// <summary>
    /// #842's other missing half: a real, successful synchronous resync -
    /// the file was out of time and this call moved it, answered "done" with
    /// what happened, and recorded the new timing status against the row.
    /// </summary>
    [Fact]
    public async Task Resyncing_a_subtitle_that_needed_moving_reports_done_and_moved_and_records_it()
    {
        var timingSync = new StubSubtitleTimingSync(new SubtitleTimingResult(
            Adjusted: true,
            Offset: TimeSpan.FromSeconds(2.4),
            Reason: "Moved 2.4 s later to match the audio.",
            Outcome: SubtitleTimingOutcome.Moved));

        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<ISubtitleTimingSync>(timingSync));
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

        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.True(body.GetProperty("moved").GetBoolean());
        Assert.Equal("Moved", body.GetProperty("outcome").GetString());
        Assert.Contains("2.4", body.GetProperty("message").GetString());

        var row = Assert.Single(await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None));
        Assert.Equal("moved", row.TimingStatus);

        // The one override #842 spells out: run-now forces the policy on for
        // this single call, whatever the library otherwise defaults to.
        Assert.True(timingSync.LastPolicy!.Enabled);
    }

    /// <summary>
    /// #842's real-failure case: a confident mismatch rejects and blocklists
    /// the subtitle instead of leaving a wrong-timed file held, the same
    /// path the automatic reject-and-refetch (#897) already uses.
    /// </summary>
    [Fact]
    public async Task Resyncing_a_subtitle_that_does_not_fit_this_file_rejects_it()
    {
        var timingSync = new StubSubtitleTimingSync(new SubtitleTimingResult(
            Adjusted: false,
            Offset: TimeSpan.Zero,
            Reason: "The best alignment sits at the very edge of the searchable window.",
            Outcome: SubtitleTimingOutcome.Mismatched));

        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<ISubtitleTimingSync>(timingSync));
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

        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.False(body.GetProperty("moved").GetBoolean());
        Assert.Equal("Mismatched", body.GetProperty("outcome").GetString());

        // Rejected: nothing held for the language any more, ready for the
        // next candidate to be fetched in its place.
        var remaining = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.DoesNotContain(remaining, row => row.Language == "en");
    }

    /// <summary>Records what it was called with and returns a fixed outcome, so a test can drive either result without a real video or subtitle file.</summary>
    private sealed class StubSubtitleTimingSync(SubtitleTimingResult result) : ISubtitleTimingSync
    {
        public SubtitleTimingPolicy? LastPolicy { get; private set; }

        public Task<SubtitleTimingResult> SyncAsync(
            string videoPath,
            string subtitlePath,
            string? originalLanguage,
            CancellationToken cancellationToken,
            SubtitleTimingPolicy? policy = null)
        {
            LastPolicy = policy;
            return Task.FromResult(result);
        }
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
