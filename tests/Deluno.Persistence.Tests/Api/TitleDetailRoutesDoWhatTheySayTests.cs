using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The per-title routes of both engines when the request is right (coverage register, area api): a metadata
/// override is saved and can be cleared, and an episode's subtitle is rejected or resynced with its real file.
/// </summary>
public sealed class TitleDetailRoutesDoWhatTheySayTests
{
    public static TheoryData<string> Kinds => new() { "movies", "series" };

    private static async Task<string> TitleIdAsync(ApplicationTestHost app, string kind)
        => kind == "movies" ? (await ApiScenes.FilmOnDiskAsync(app)).MovieId : (await ApiScenes.ShowOnDiskAsync(app)).SeriesId;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_metadata_override_replaces_the_overridden_fields_and_a_blank_one_clears_it_again(string kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await TitleIdAsync(app, kind);

        var put = await app.Client.PutAsJsonAsync($"/api/{kind}/{id}/metadata/override", new
        {
            originalTitle = "Duna",
            overview = "A mind-expanding story of sand.",
            posterUrl = "https://images.example.com/poster.jpg",
            backdropUrl = "https://images.example.com/backdrop.jpg",
            rating = 8.4,
            genres = "Science Fiction, Adventure",
            externalUrl = "https://example.com/duna",
            imdbId = "tt1160419"
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var detail = await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{id}");
        Assert.Equal("A mind-expanding story of sand.", detail.GetProperty("overview").GetString());
        Assert.Equal("Duna", detail.GetProperty("originalTitle").GetString());
        Assert.Equal(8.4, detail.GetProperty("rating").GetDouble(), 1);
        Assert.Equal("Science Fiction, Adventure", detail.GetProperty("genres").GetString());
        Assert.Equal("tt1160419", detail.GetProperty("imdbId").GetString());
        Assert.Contains("metadata values were manually overridden", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

        var cleared = await app.Client.PutAsJsonAsync($"/api/{kind}/{id}/metadata/override", new { overview = "", rating = (double?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{id}");
        Assert.True(after.GetProperty("overview").ValueKind == JsonValueKind.Null || after.GetProperty("overview").GetString() == string.Empty, "a blank override clears the stored text");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("rating").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync($"/api/{kind}/no-such-title/metadata/override", new { overview = "x" })).StatusCode);
    }

    private static async Task<(string EpisodeId, string SubtitlePath)> HeldEpisodeSubtitleAsync(ApplicationTestHost app)
    {
        var show = await ApiScenes.ShowOnDiskAsync(app);
        var subtitlePath = Path.ChangeExtension(show.FilePath, ".en.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:01,000 --> 00:00:03,000\nHello.\n");
        await app.Services.GetRequiredService<IMediaSubtitleRepository>().RecordFetchedAsync(
            MediaKind.Series, show.EpisodeId,
            new MediaSubtitleRow("en", "fetched", Forced: false, HearingImpaired: false, FilePath: subtitlePath, StreamIndex: null, Codec: "srt", Provider: "gestdown", SourceKey: "gestdown|abc123"),
            CancellationToken.None);
        return (show.EpisodeId, subtitlePath);
    }

    [Fact]
    public async Task Rejecting_an_episodes_subtitle_deletes_the_file_blocklists_that_one_and_says_so_in_Activity()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (episodeId, subtitlePath) = await HeldEpisodeSubtitleAsync(app);
        Assert.True(File.Exists(subtitlePath));

        var response = await app.Client.PostAsync($"/api/series/episodes/{episodeId}/subtitles/en/reject", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rejected").GetBoolean());
        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        Assert.Empty(await subtitles.ListSubtitlesAsync(MediaKind.Series, episodeId, CancellationToken.None));
        Assert.Equal("gestdown|abc123", Assert.Single(await subtitles.ListBlockedAsync(MediaKind.Series, episodeId, CancellationToken.None)).BlockKey);
        Assert.False(File.Exists(subtitlePath), "the rejected subtitle file is removed from beside the episode");
        Assert.Contains("subtitle.rejected", await app.Client.GetStringAsync("/api/activity"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/series/episodes/{episodeId}/subtitles/en/reject", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/series/episodes/no-such-episode/subtitles/en/reject", content: null)).StatusCode);
    }

    [Fact]
    public async Task Resyncing_an_episodes_subtitle_that_needed_moving_reports_it_done_and_moved_and_records_the_timing()
    {
        var timing = new StubTiming(new SubtitleTimingResult(true, TimeSpan.FromSeconds(2.4), "Moved 2.4 s later to match the audio.", SubtitleTimingOutcome.Moved));
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<ISubtitleTimingSync>(timing));
        var (episodeId, subtitlePath) = await HeldEpisodeSubtitleAsync(app);

        var response = await app.Client.PostAsync($"/api/series/episodes/{episodeId}/subtitles/en/resync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.True(body.GetProperty("moved").GetBoolean());
        Assert.Equal("Moved", body.GetProperty("outcome").GetString());
        Assert.Contains("2.4", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        var row = Assert.Single(await app.Services.GetRequiredService<IMediaSubtitleRepository>().ListSubtitlesAsync(MediaKind.Series, episodeId, CancellationToken.None));
        Assert.Equal("moved", row.TimingStatus);
        Assert.True(timing.LastPolicy!.Enabled, "run-now switches the policy on for this one call");
        Assert.Equal(subtitlePath, timing.LastSubtitlePath);
        Assert.True(File.Exists(subtitlePath));
    }

    [Fact]
    public async Task Resyncing_an_episodes_subtitle_that_does_not_fit_rejects_it_and_leaves_no_subtitle_held()
    {
        var timing = new StubTiming(new SubtitleTimingResult(false, TimeSpan.Zero, "The best alignment sits at the very edge of the searchable window.", SubtitleTimingOutcome.Mismatched));
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<ISubtitleTimingSync>(timing));
        var (episodeId, subtitlePath) = await HeldEpisodeSubtitleAsync(app);

        var response = await app.Client.PostAsync($"/api/series/episodes/{episodeId}/subtitles/en/resync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.Equal("Mismatched", body.GetProperty("outcome").GetString());
        Assert.Empty(await app.Services.GetRequiredService<IMediaSubtitleRepository>().ListSubtitlesAsync(MediaKind.Series, episodeId, CancellationToken.None));
        Assert.False(File.Exists(subtitlePath));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync($"/api/series/episodes/{episodeId}/subtitles/en/resync", content: null)).StatusCode);
    }

    private sealed class StubTiming(SubtitleTimingResult result) : ISubtitleTimingSync
    {
        public SubtitleTimingPolicy? LastPolicy { get; private set; }

        public string? LastSubtitlePath { get; private set; }

        public Task<SubtitleTimingResult> SyncAsync(string videoPath, string subtitlePath, string? originalLanguage, CancellationToken cancellationToken, SubtitleTimingPolicy? policy = null)
        {
            LastPolicy = policy;
            LastSubtitlePath = subtitlePath;
            return Task.FromResult(result);
        }
    }
}
