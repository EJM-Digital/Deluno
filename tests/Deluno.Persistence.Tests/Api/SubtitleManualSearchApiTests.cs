using Deluno.Contracts;
using Deluno.Integrations.Subtitles;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #841: manual search, pick, blacklist and upload, through the real route
/// table.
/// </summary>
public sealed class SubtitleManualSearchApiTests
{
    [Fact]
    public async Task Candidates_lists_what_the_fetch_service_ranked()
    {
        var fetchService = new FakeFetchService(
        [
            new SubtitleSearchCandidate(
                "gestdown", "Gestdown", "gestdown|abc", "abc", "en", false, "Dune.2021.WEB", "someone", 12,
                SubtitleMatch.AnyRelease, "Any release with English.")
        ]);
        await using var app = await StartAsync(fetchService);
        var id = await AddMovieAsync(app);

        var response = await app.Client.GetAsync($"/api/movies/{id}/subtitles/en/candidates");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidate = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("gestdown", candidate.GetProperty("providerKey").GetString());
        Assert.Equal("abc", candidate.GetProperty("downloadToken").GetString());
    }

    [Fact]
    public async Task Picking_a_candidate_writes_it_and_records_it_as_manual()
    {
        var fetchService = new FakeFetchService(
        [],
        pickOutcome: new SubtitleFetchOutcome(
            "en", true, "gestdown", @"D:\Media\Arrival\Arrival.en.srt", false, "Written.",
            SourceKey: "gestdown|abc"));
        await using var app = await StartAsync(fetchService);
        var id = await AddMovieAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            $"/api/movies/{id}/subtitles/en/candidates/pick", new { providerKey = "gestdown", downloadToken = "abc" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        var row = Assert.Single(await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None));
        Assert.Equal("manual", row.Source);
        Assert.Equal("gestdown|abc", row.SourceKey);
    }

    [Fact]
    public async Task Blacklisting_a_candidate_never_downloaded_still_stores_the_block()
    {
        await using var app = await StartAsync(new FakeFetchService([]));
        var id = await AddMovieAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            $"/api/movies/{id}/subtitles/en/blacklist",
            new { providerKey = "gestdown", downloadToken = "bad-1", reason = "Wrong episode." });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        var blocked = await subtitles.ListBlockedAsync(MediaKind.Movie, id, CancellationToken.None);
        var entry = Assert.Single(blocked);
        Assert.Equal("gestdown|bad-1", entry.BlockKey);
        Assert.Equal("Wrong episode.", entry.Reason);
    }

    [Fact]
    public async Task Uploading_a_real_subtitle_writes_and_holds_it()
    {
        await using var app = await StartAsync(new FakeFetchService([]));
        var id = await AddMovieAsync(app);

        var srt = Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:03,000\r\nHello.\r\n");
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(srt);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "Arrival.en.srt");

        var response = await app.Client.PostAsync($"/api/movies/{id}/subtitles/en/upload", content);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var subtitles = app.Services.GetRequiredService<IMediaSubtitleRepository>();
        var row = Assert.Single(await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None));
        Assert.Equal("uploaded", row.Source);
        Assert.NotNull(row.FilePath);
        Assert.True(File.Exists(row.FilePath));
        var written = await File.ReadAllTextAsync(row.FilePath!);
        Assert.Contains("Hello.", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploading_something_that_is_not_a_subtitle_is_refused()
    {
        await using var app = await StartAsync(new FakeFetchService([]));
        var id = await AddMovieAsync(app);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>not a subtitle</html>"));
        content.Add(fileContent, "file", "not-a-subtitle.srt");

        var response = await app.Client.PostAsync($"/api/movies/{id}/subtitles/en/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<ApplicationTestHost> StartAsync(FakeFetchService fetchService)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<ISubtitleFetchService>();
            services.AddSingleton<ISubtitleFetchService>(fetchService);
        });

    private static async Task<string> AddMovieAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync(
            "/api/movies/", new { title = "Arrival", releaseYear = 2016, monitored = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        // The upload/pick endpoints need a file path on the movie to know
        // where to write the subtitle beside.
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival",
                    ReleaseYear: 2016,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 1080p",
                    TargetQuality: "WEB 1080p",
                    QualityCutoffMet: true,
                    FilePath: Path.Combine(app.DataRoot, "Arrival", "Arrival.mkv"),
                    FileSizeBytes: 4_000_000_000)
            ],
            CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(Search: "Arrival"), CancellationToken.None);
        var withFile = page.Items.First(item => item.FilePath is not null);
        Directory.CreateDirectory(Path.GetDirectoryName(withFile.FilePath)!);

        return withFile.Id;
    }

    private sealed class FakeFetchService(
        IReadOnlyList<SubtitleSearchCandidate> candidates,
        SubtitleFetchOutcome? pickOutcome = null) : ISubtitleFetchService
    {
        public Task<SubtitleFetchOutcome> FetchAsync(
            SubtitleSearchRequest request, string language, bool isEpisodeMedia, string videoPath,
            bool excludeHearingImpaired, CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null, SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false, IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult(new SubtitleFetchOutcome(language, false, null, null, false, "Not used by this test."));

        public Task<IReadOnlyList<SubtitleSearchCandidate>> SearchCandidatesAsync(
            SubtitleSearchRequest request, string language, bool isEpisodeMedia, string? videoPath,
            bool excludeHearingImpaired, CancellationToken cancellationToken,
            SubtitleNamePolicy? namePolicy = null, IReadOnlyCollection<string>? blockedKeys = null)
            => Task.FromResult(candidates);

        public Task<SubtitleFetchOutcome> DownloadCandidateAsync(
            SubtitleSearchRequest request, string providerKey, string downloadToken, string language,
            bool isEpisodeMedia, string videoPath, CancellationToken cancellationToken,
            SubtitleContentModificationPolicy? contentPolicy = null, SubtitleNamePolicy? namePolicy = null,
            bool omitLanguageCode = false)
            => Task.FromResult(pickOutcome ?? new SubtitleFetchOutcome(language, false, null, null, false, "No pick configured."));
    }
}
