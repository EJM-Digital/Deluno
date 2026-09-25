using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Adding a title searches for it right away.
///
/// <para><b>Why this file exists.</b> #645. The owner added a new monitored TV show
/// live and expected Deluno to start searching for it. Nothing happened until he
/// clicked "Search now" himself. <c>POST /api/series</c> and <c>POST
/// /api/movies</c> only ever enqueued <c>series.catalog.refresh</c> /
/// <c>movies.catalog.refresh</c> — a metadata pull, not a search — so a
/// newly-added, monitored, missing title sat there until the library's own next
/// scheduled cycle happened to reach it, with nothing on screen explaining the
/// wait. The add dialog's "Monitor and search automatically" checkbox read as a
/// promise to search immediately, and did not keep it.</para>
///
/// <para>The fix enqueues a real, targeted <c>library.search.movies</c> /
/// <c>library.search.tv</c> job — the same shape <c>AnotherCopySearch</c>,
/// <c>RecoveryCaseRetry</c> and <c>ExternalRedownload</c> already use for "search
/// this one title now" — alongside the existing refresh, gated on the title
/// actually being missing (or upgradable) and monitored. An already-fully-owned
/// title, or one added unmonitored, must not trigger a pointless search.</para>
///
/// <para>Both media kinds are held to the same contract here, deliberately, the
/// same reason <see cref="ReAddingKeepsTheFileTests"/> is.</para>
/// </summary>
public sealed class NewTitlesSearchRightAwayTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Adding_a_monitored_missing_title_enqueues_a_targeted_search_alongside_the_refresh(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);

        var id = await AddAsync(app, Route(kind), CreateBody(kind, "Severance", 2022, monitored: true, libraryId));

        var searchJob = await FindJobAsync(app, SearchJobType(kind), id);
        Assert.True(searchJob is not null, "a newly-added, monitored, missing title should be searched for right away");
        Assert.Equal(RelatedEntityType(kind) == "movie" ? "movies" : "series", searchJob!.Source);
        Assert.Equal(RelatedEntityType(kind), searchJob.RelatedEntityType);
        Assert.Equal(id, searchJob.RelatedEntityId);

        using var payload = JsonDocument.Parse(searchJob.PayloadJson!);
        Assert.Equal(id, payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal(libraryId, payload.RootElement.GetProperty("libraryId").GetString());
        Assert.True(payload.RootElement.GetProperty("checkMissing").GetBoolean());

        // On top of the refresh, not instead of it.
        Assert.NotNull(await FindJobAsync(app, RefreshJobType(kind), id));
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Adding_an_unmonitored_title_does_not_search(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);

        var id = await AddAsync(app, Route(kind), CreateBody(kind, "Severance", 2022, monitored: false, libraryId));

        Assert.Null(await FindJobAsync(app, SearchJobType(kind), id));
        // The refresh is unconditional; only the search is gated on monitoring.
        Assert.NotNull(await FindJobAsync(app, RefreshJobType(kind), id));
    }

    /// <summary>
    /// <c>AddAsync</c> dedupes (#530): re-adding a title the catalogue already
    /// holds a file for returns that same row, with its real <c>HasFile</c> and
    /// <c>Monitored</c> — not whatever this call's own request said. Searching
    /// for something already fully owned is exactly the pointless search this
    /// feature must not cause.
    ///
    /// <para>Seeded monitored and covered directly through the repository,
    /// before the create endpoint ever sees the title — the way an import or a
    /// migration leaves one. Re-adding it unmonitored first and flipping
    /// <c>monitored</c> on the second call would not isolate this: <c>AddAsync</c>
    /// keeps the row's own stored <c>Monitored</c> on a dedupe and ignores
    /// whatever the new request asked for, the same as it does for
    /// <c>HasFile</c>, so that second call could never actually be monitored.
    /// Seeding it monitored up front is what makes this test able to fail if the
    /// <c>HasFile</c> gate stops working while the <c>Monitored</c> gate still
    /// does.</para>
    /// </summary>
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Re_adding_a_title_that_already_has_a_file_does_not_search(MediaKind kind)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await CreateLibraryAsync(app, kind);
        var id = await SeedMonitoredTitleAsync(app, kind, "Severance", 2022);

        var state = app.Services.GetRequiredService<IMediaStateRepository>();
        await state.EnsureWantedStateAsync(
            kind,
            id,
            libraryId,
            WantedStatuses.Covered,
            "Imported from disk.",
            hasFile: true,
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            CancellationToken.None);

        // Added through the API - the dialog a person actually uses - and it
        // dedupes onto the row just seeded rather than creating a second one.
        var second = await AddAsync(app, Route(kind), CreateBody(kind, "Severance", 2022, monitored: true, libraryId));

        Assert.Equal(id, second);
        Assert.Null(await FindJobAsync(app, SearchJobType(kind), id));
        Assert.NotNull(await FindJobAsync(app, RefreshJobType(kind), id));
    }

    // ------------------------------------------------------------------ helpers

    private static string Route(MediaKind kind) => kind == MediaKind.Movie ? "/api/movies/" : "/api/series/";

    private static string SearchJobType(MediaKind kind) => LibrarySearchJobTypes.For(kind == MediaKind.Movie ? "movies" : "tv");

    private static string RefreshJobType(MediaKind kind) => kind == MediaKind.Movie ? "movies.catalog.refresh" : "series.catalog.refresh";

    private static string RelatedEntityType(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "series";

    private static object CreateBody(MediaKind kind, string title, int year, bool monitored, string libraryId)
        => kind == MediaKind.Movie
            ? new { title, releaseYear = year, monitored, libraryId }
            : new { title, startYear = year, monitored, libraryId };

    /// <summary>
    /// A title in the catalogue already, monitored, the create endpoint never
    /// having run for it — through the repository rather than <c>POST</c>, so
    /// this cannot itself enqueue the search this file is testing for.
    /// </summary>
    private static async Task<string> SeedMonitoredTitleAsync(ApplicationTestHost app, MediaKind kind, string title, int year)
    {
        if (kind == MediaKind.Movie)
        {
            var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
            var created = await movies.AddAsync(
                new CreateMovieRequest(Title: title, ReleaseYear: year, ImdbId: null, Monitored: true),
                CancellationToken.None);
            return created.Id;
        }

        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var createdSeries = await series.AddAsync(
            new CreateSeriesRequest(Title: title, StartYear: year, ImdbId: null, Monitored: true),
            CancellationToken.None);
        return createdSeries.Id;
    }

    private static async Task<JobQueueItem?> FindJobAsync(ApplicationTestHost app, string jobType, string relatedEntityId)
    {
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var all = await jobs.ListAsync(500, CancellationToken.None);
        return all.SingleOrDefault(job =>
            string.Equals(job.JobType, jobType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.RelatedEntityId, relatedEntityId, StringComparison.Ordinal));
    }

    /// <summary>
    /// A real library of the right kind, created through the API.
    ///
    /// <para>Not optional: the endpoint writes wanted state, and enqueues the
    /// search this file is about, inside <c>foreach (var library in
    /// libraries)</c> — a fresh install has none, so with no library the loop
    /// never runs and neither defect this file guards against can occur.</para>
    /// </summary>
    private static async Task<string> CreateLibraryAsync(ApplicationTestHost app, MediaKind kind)
    {
        var mediaType = kind == MediaKind.Movie ? "movies" : "tv";
        var rootPath = Path.Combine(app.DataRoot, mediaType);
        Directory.CreateDirectory(rootPath);

        var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = mediaType,
            mediaType,
            purpose = "collection",
            rootPath,
            downloadsPath = (string?)null,
            qualityProfileId = (string?)null,
            importWorkflow = "copy",
            processorName = (string?)null,
            processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null,
            processorFailureMode = (string?)null,
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false,
            searchIntervalHours = (int?)null,
            retryDelayHours = (int?)null,
            maxItemsPerRun = (int?)null
        });

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /api/libraries/ returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, object body)
    {
        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
