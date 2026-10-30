using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #840's cross-library Wanted screen: titles missing a wanted subtitle
/// language, and the bulk "search all" action that queues one
/// subtitle.title.search job per listed item.
/// </summary>
public sealed class SubtitleWantedApiTests
{
    [Fact]
    public async Task Wanted_lists_a_title_missing_its_librarys_language()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, movieId) = await CreateLibraryAndMovieAsync(app);

        var response = await app.Client.GetAsync("/api/subtitles/wanted");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = document.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("mediaId").GetString() == movieId);
        Assert.Equal("Movie", row.GetProperty("kind").GetString());
        Assert.Equal(libraryId, row.GetProperty("libraryId").GetString());
        Assert.Contains("en", row.GetProperty("languagesMissing").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Every_title_missing_subtitles_is_reachable_page_by_page_with_the_total()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, _) = await CreateLibraryAndMovieAsync(app);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingBatchAsync(
            libraryId,
            Enumerable.Range(1, 129).Select(index => new ExistingMovieImportRequest(
                Title: $"Film {index:000}", ReleaseYear: 2000, WantedStatus: WantedStatuses.Covered,
                WantedReason: "Imported from your existing library.", CurrentQuality: "WEB 1080p",
                TargetQuality: "WEB 1080p", QualityCutoffMet: true,
                FilePath: $"/media/Film{index:000}/Film{index:000}.mkv", FileSizeBytes: 1_000_000_000)).ToList(),
            CancellationToken.None);

        var seen = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var url = "/api/subtitles/wanted?pageSize=50" + (token is null ? "" : $"&pageToken={Uri.EscapeDataString(token)}");
            var response = await app.Client.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(130, root.GetProperty("totalCount").GetInt32());
            seen.AddRange(root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("mediaId").GetString()!));
            token = root.GetProperty("nextPageToken").GetString();
            Assert.Equal(token is not null, root.GetProperty("hasMore").GetBoolean());
            pages++;
        }
        while (token is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(130, seen.Count);
        Assert.Equal(130, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_library_asking_for_no_languages_lists_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival", ReleaseYear: 2016, WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.", CurrentQuality: "WEB 1080p",
                    TargetQuality: "WEB 1080p", QualityCutoffMet: true,
                    FilePath: @"D:\Media\Arrival\Arrival.mkv", FileSizeBytes: 4_000_000_000)
            ],
            CancellationToken.None);

        var response = await app.Client.GetAsync("/api/subtitles/wanted");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Search_all_queues_one_job_per_listed_item()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, movieId) = await CreateLibraryAndMovieAsync(app);

        var response = await app.Client.PostAsJsonAsync(
            "/api/subtitles/wanted/search-all",
            new { items = new[] { new { kind = "Movie", mediaId = movieId, libraryId } } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("queued").GetInt32());

        var jobQueue = app.Services.GetRequiredService<IJobQueueRepository>();
        var jobs = await jobQueue.ListAsync(50, CancellationToken.None);
        Assert.Contains(jobs, job => job.JobType == "subtitle.title.search" && job.RelatedEntityId == movieId);
    }

    [Fact]
    public async Task Search_all_with_no_items_is_a_validation_problem()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/subtitles/wanted/search-all", new { items = Array.Empty<object>() });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string LibraryId, string MovieId)> CreateLibraryAndMovieAsync(ApplicationTestHost app)
    {
        var libraries = app.Services.GetRequiredService<ILibrariesRepository>();
        var created = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                Purpose: "Main",
                RootPath: @"D:\Media",
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);
        await libraries.UpdateLibrarySubtitlesAsync(
            created.Id, new UpdateLibrarySubtitlesRequest(Languages: ["en"], Mode: "all"), CancellationToken.None);

        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingBatchAsync(
            created.Id,
            [
                new ExistingMovieImportRequest(
                    Title: "Dune", ReleaseYear: 2021, WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.", CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p", QualityCutoffMet: true,
                    FilePath: @"D:\Media\Dune\Dune.mkv", FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(Search: "Dune"), CancellationToken.None);
        var movieId = page.Items.Single(item => item.Title == "Dune").Id;

        return (created.Id, movieId);
    }
}
