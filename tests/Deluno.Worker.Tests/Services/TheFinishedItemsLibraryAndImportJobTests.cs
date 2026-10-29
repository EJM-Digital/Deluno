using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Which library a finished download is imported into (the grab's library, then the library whose route category for
/// that client matches - preferring the one whose downloads folder holds the file - then the library whose downloads
/// folder holds it, then the first of the type), and exactly what the import job it queues says. Each run is the real
/// import planner over a real finished file in a temp folder; only the stores are stand-ins. Refs #1296.
/// </summary>
public sealed class TheFinishedItemsLibraryAndImportJobTests : IDisposable
{
    private const string ReleaseName = "Dune.2021.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-finished-library-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_library_the_grab_was_made_for_wins_over_a_route_category_and_a_downloads_folder_that_hold_the_file()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new() { ["a"] = "movies-a" },
            assignedLibraryId: "b",
            category: "movies-a",
            downloadsFolder: "Downloads-A");

        Assert.Equal("b", run.Request.Preview.LibraryId);
        Assert.Contains("into Library b", run.Message);
    }

    [Fact]
    public async Task With_no_assigned_library_the_library_whose_route_category_for_that_client_matches_gets_the_file()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new() { ["b"] = "Movies-B" },
            category: "movies-b",
            downloadsFolder: "Downloads-A");

        Assert.Equal("b", run.Request.Preview.LibraryId);
    }

    [Fact]
    public async Task Of_two_libraries_with_the_same_route_category_the_one_whose_downloads_folder_holds_the_file_gets_it()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new() { ["a"] = "movies", ["b"] = "movies" },
            category: "movies",
            downloadsFolder: "Downloads-B");

        Assert.Equal("b", run.Request.Preview.LibraryId);
    }

    [Fact]
    public async Task Of_two_libraries_with_the_same_route_category_and_no_downloads_folder_match_the_first_gets_it()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new() { ["a"] = "movies", ["b"] = "movies" },
            category: "movies",
            downloadsFolder: "Elsewhere");

        Assert.Equal("a", run.Request.Preview.LibraryId);
    }

    [Fact]
    public async Task With_no_route_match_the_first_library_of_the_type_whose_downloads_folder_holds_the_file_gets_it()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new() { ["a"] = "something-else" },
            category: "movies",
            downloadsFolder: "Downloads-B");

        Assert.Equal("b", run.Request.Preview.LibraryId);
    }

    [Fact]
    public async Task With_nothing_else_to_go_on_the_first_library_of_the_media_type_gets_it_and_a_show_never_lands_in_a_film_library()
    {
        var film = await RunAsync(
            [Lib("tv-1", "tv", "Downloads-T"), Lib("a", "movies", "Downloads-A"), Lib("b", "movies", "Downloads-B")],
            routes: new(),
            category: null,
            downloadsFolder: "Elsewhere");
        Assert.Equal("a", film.Request.Preview.LibraryId);

        var show = await RunAsync(
            [Lib("a", "movies", "Downloads-A"), Lib("tv-1", "tv", "Downloads-T"), Lib("tv-2", "tv", "Downloads-U")],
            routes: new(),
            category: null,
            downloadsFolder: "Elsewhere",
            mediaType: "tv");
        Assert.Equal("tv-1", show.Request.Preview.LibraryId);
        Assert.Equal("series", show.Job.RelatedEntityType);
    }

    [Fact]
    public async Task A_film_import_is_queued_with_the_catalogues_details_auto_transfer_copy_fallback_and_no_overwrite_without_authorisation()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A")],
            routes: new(),
            category: "movies",
            downloadsFolder: "Downloads-A",
            dispatch: new DispatchCatalogueLink("dispatch-1", "movie", "movie-1", LibraryId: "a"),
            movie: Movie());

        var preview = run.Request.Preview;
        Assert.Equal("Dune", preview.Title);
        Assert.Equal(2021, preview.Year);
        Assert.Equal("tt1160419", preview.ImdbId);
        Assert.Equal("Dune", preview.OriginalTitle);
        Assert.Equal("Legendary", preview.Studio);
        Assert.Equal(["Science Fiction", "Adventure"], preview.Genres);
        Assert.Equal("a", preview.LibraryId);
        Assert.Equal(["movies"], preview.Tags);
        Assert.Equal("auto", run.Request.TransferMode);
        Assert.True(run.Request.AllowCopyFallback);
        Assert.False(run.Request.Overwrite);
        Assert.False(run.Request.ForceReplacement);
        Assert.Equal("dispatch-1", run.Request.DispatchId);
        Assert.Null(run.Request.ExpectedExistingPath);
        Assert.Null(run.Request.ReplacementTargets);
        Assert.Equal("filesystem.import.execute", run.Job.JobType);
        Assert.Equal("download-client", run.Job.Source);
        Assert.Equal("movie", run.Job.RelatedEntityType);
        Assert.Equal("movie-1", run.Job.RelatedEntityId);
        Assert.Equal("Dune 2021 1080p WEB DL DDP5 1 H 264 DELUNO finished in qBittorrent; Deluno queued it for import into Library a.", run.Message);
        Assert.Equal(("library", "a", "import-job"), run.ActivityScope);
    }

    [Fact]
    public async Task A_replacement_the_grab_authorised_sets_overwrite_and_carries_the_file_it_replaces()
    {
        var targets = new[] { new DispatchReplacementTarget("movie-1", "/library/Dune (2021)/Dune.mkv") };
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A")],
            routes: new(),
            category: "movies",
            downloadsFolder: "Downloads-A",
            dispatch: new DispatchCatalogueLink(
                "dispatch-1", "movie", "movie-1", LibraryId: "a",
                ReplacementAuthorized: true, ReplacementExpectedPath: "/library/Dune (2021)/Dune.mkv", ReplacementTargets: targets),
            movie: Movie());

        Assert.True(run.Request.Overwrite);
        Assert.False(run.Request.ForceReplacement);
        Assert.Equal("/library/Dune (2021)/Dune.mkv", run.Request.ExpectedExistingPath);
        Assert.Equal(targets, run.Request.ReplacementTargets);
    }

    [Fact]
    public async Task A_forced_replacement_authorised_by_the_grab_is_carried_to_the_import()
    {
        var run = await RunAsync(
            [Lib("a", "movies", "Downloads-A")],
            routes: new(),
            category: "movies",
            downloadsFolder: "Downloads-A",
            dispatch: new DispatchCatalogueLink(
                "dispatch-1", "movie", "movie-1", LibraryId: "a",
                ReplacementAuthorized: true, ForceReplacementAuthorized: true),
            movie: Movie());

        Assert.True(run.Request.Overwrite);
        Assert.True(run.Request.ForceReplacement);
    }

    [Fact]
    public async Task A_show_import_carries_the_shows_numbering_and_network_and_is_related_to_the_show()
    {
        var series = (SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem)) with
        {
            Id = "series-1",
            Title = "The Wire",
            StartYear = 2002,
            ImdbId = "tt0306414",
            Genres = "Crime, Drama",
            SeriesType = "standard",
            NumberingScheme = "absolute",
            MetadataJson = """{"network":"HBO","tvdbId":"79126"}""",
            OriginalTitle = "The Wire"
        };
        var run = await RunAsync(
            [Lib("tv-1", "tv", "Downloads-T")],
            routes: new(),
            category: "tv",
            downloadsFolder: "Downloads-T",
            mediaType: "tv",
            dispatch: new DispatchCatalogueLink("dispatch-9", "series", "series-1", LibraryId: "tv-1"),
            series: series);

        var preview = run.Request.Preview;
        Assert.Equal("The Wire", preview.Title);
        Assert.Equal(2002, preview.Year);
        Assert.Equal("series-1", preview.SeriesId);
        Assert.Equal("standard", preview.SeriesType);
        Assert.Equal("absolute", preview.NumberingScheme);
        Assert.Equal("HBO", preview.Network);
        Assert.Equal("79126", preview.TvDbId);
        Assert.Equal(["Crime", "Drama"], preview.Genres);
        Assert.Equal("series", run.Job.RelatedEntityType);
        Assert.Equal("series-1", run.Job.RelatedEntityId);
    }

    [Theory]
    [InlineData(DownloadQueueStatuses.Downloading)]
    [InlineData(DownloadQueueStatuses.Queued)]
    [InlineData(DownloadQueueStatuses.Stalled)]
    [InlineData("paused")]
    public async Task A_download_that_is_still_downloading_paused_queued_or_stalled_is_never_queued_for_import_or_handed_to_a_processor(string status)
    {
        await RunAsync(
            [Lib("a", "movies", "Downloads-A")],
            routes: new(),
            category: "movies",
            downloadsFolder: "Downloads-A",
            status: status,
            expectJob: false);

        Processors.Verify(r => r.EnsureProcessorHandoffAsync(It.IsAny<CreateProcessorHandoffRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MovieListItem Movie()
        => (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem)) with
        {
            Id = "movie-1",
            Title = "Dune",
            ReleaseYear = 2021,
            ImdbId = "tt1160419",
            OriginalTitle = "Dune",
            Genres = "Science Fiction, Adventure",
            MetadataJson = """{"studio":"Legendary"}"""
        };

    private LibraryItem Lib(string id, string mediaType, string downloadsFolder)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "Library-" + id)).FullName;
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, $"Library {id}", mediaType, "main", root, Path.Combine(_root, downloadsFolder), null, null, null,
            true, true, "standard", null, null, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private Mock<IProcessorRepository> Processors { get; } = NewProcessors();

    private static Mock<IProcessorRepository> NewProcessors()
    {
        var mock = new Mock<IProcessorRepository>();
        mock.Setup(r => r.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());
        return mock;
    }

    private sealed record Outcome(ImportExecuteRequest Request, EnqueueJobRequest Job, string Message, (string? Scope, string? Id, string? JobId) ActivityScope);

    private async Task<Outcome> RunAsync(
        IReadOnlyList<LibraryItem> libraries,
        Dictionary<string, string> routes,
        string? category,
        string downloadsFolder,
        string? assignedLibraryId = null,
        string mediaType = "movies",
        DispatchCatalogueLink? dispatch = null,
        MovieListItem? movie = null,
        SeriesListItem? series = null,
        string status = DownloadQueueStatuses.Completed,
        bool expectJob = true)
    {
        var file = Path.Combine(_root, downloadsFolder, ReleaseName, ReleaseName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "finished download");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));

        EnqueueJobRequest? queued = null;
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued = request)
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });

        string? message = null;
        (string? Scope, string? Id, string? JobId) scope = default;
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(instance => instance.ListActivityAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActivityEventItem>());
        activity.Setup(instance => instance.RecordActivityAsync(
                "filesystem.import.auto-queued", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, string?, string?, CancellationToken>((_, text, _, relatedJob, relatedType, relatedId, _) =>
            {
                message = text;
                scope = (relatedType, relatedId, relatedJob);
            })
            .ReturnsAsync((ActivityEventItem)RuntimeHelpers.GetUninitializedObject(typeof(ActivityEventItem)));

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        if (dispatch is not null)
        {
            jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", ReleaseName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(dispatch);
        }

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        var seriesRepository = new Mock<ISeriesCatalogRepository>();
        seriesRepository.Setup(repository => repository.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(series);

        var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Overview(new DownloadQueueItem(
            "queue-1", "client-1", "qBittorrent", "qbittorrent", mediaType, "Dune 2021 1080p WEB DL DDP5 1 H 264 DELUNO", ReleaseName,
            category ?? "", status, 1, 0, 0, 100, 100, 0, "Indexer", null,
            DateTimeOffset.UnixEpoch, file, assignedLibraryId)));

        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        repository.Setup(instance => instance.GetLibraryRoutingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string libraryId, CancellationToken _) => new LibraryRoutingSnapshot(
                libraryId, libraryId, [],
                routes.TryGetValue(libraryId, out var routed)
                    ? [new LibraryDownloadClientLinkItem("link-" + libraryId, libraryId, "client-1", "qBittorrent", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, routed)]
                    : []));

        await new WorkPlanner(new FailingLogger(), jobs.Object, new ConfigurationBuilder().Build(), TimeProvider.System)
            .PlanImportAutomationAsync(
                scheduler.Object,
                Processors.Object,
                repository.Object,
                new LibraryAvailabilityService(Mock.Of<IActivityFeedRepository>(), TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance),
                telemetry.Object,
                Mock.Of<IProcessorConnectionService>(),
                activity.Object,
                movies.Object,
                seriesRepository.Object,
                TimeProvider.System,
                CancellationToken.None);

        if (!expectJob)
        {
            Assert.Null(queued);
            return null!;
        }

        Assert.NotNull(queued);
        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(queued!.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.NotNull(message);
        return new Outcome(request, queued, message!, (scope.Scope, scope.Id, scope.JobId));
    }

    private sealed class FailingLogger : Microsoft.Extensions.Logging.ILogger<WorkPlanner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                throw new InvalidOperationException(formatter(state, exception), exception);
            }
        }
    }

    private static DownloadTelemetryOverview Overview(DownloadQueueItem item)
    {
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        return new(
            summary,
            [new DownloadClientTelemetrySnapshot(
                "client-1", "qBittorrent", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [item], [], DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch);
    }
}
