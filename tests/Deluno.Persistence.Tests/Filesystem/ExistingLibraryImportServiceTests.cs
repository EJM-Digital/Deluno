using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The behaviour that makes a 20,000-item library importable: the work happens
/// in bounded slices, the position survives a restart, and nothing that goes
/// wrong with one title stops the rest.
/// </summary>
public sealed class ExistingLibraryImportServiceTests
{
    [Fact]
    public async Task Start_creates_a_queued_run_rather_than_importing_inline()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 5);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, libraries, movies);

        var progress = await service.StartAsync(libraryId, CancellationToken.None);

        Assert.NotNull(progress);
        Assert.Equal(LibraryImportRunStatuses.Queued, progress.Run.Status);
        Assert.Equal(0, progress.Run.ProcessedCount);

        // Nothing has been written yet — starting an import must return
        // immediately, not do the work.
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Start_twice_returns_the_same_run()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 3);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var service = CreateService(storage, timeProvider, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var first = await service.StartAsync(libraryId, CancellationToken.None);
        var second = await service.StartAsync(libraryId, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Run.Id, second.Run.Id);
    }

    [Fact]
    public async Task Slices_import_every_movie_exactly_once()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 25);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        // Deliberately tiny slices and batches, so the same boundaries a real
        // 20,000-item import crosses are crossed here.
        var service = CreateService(
            storage,
            timeProvider,
            libraries,
            movies,
            new LibraryImportSliceOptions(MaxItemsPerSlice: 4, TimeSpan.FromSeconds(20), MovieBatchSize: 3, SeriesBatchSize: 2));

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);

        var slices = await DrainAsync(service, run.Run.Id);

        Assert.True(slices > 1, $"Expected the import to take more than one slice, took {slices}.");

        var imported = await movies.ListAsync(CancellationToken.None);
        Assert.Equal(25, imported.Count);
        Assert.Equal(25, imported.Select(item => item.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var finished = await service.GetProgressAsync(libraryId, CancellationToken.None);
        Assert.NotNull(finished);
        Assert.Equal(LibraryImportRunStatuses.Completed, finished.Run.Status);
        Assert.Equal(25, finished.Run.ProcessedCount);
        Assert.Equal(25, finished.Run.ImportedCount);
        Assert.Equal(100, finished.PercentComplete);
    }

    [Fact]
    public async Task A_run_resumes_from_its_position_after_a_restart()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 20);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var options = new LibraryImportSliceOptions(MaxItemsPerSlice: 5, TimeSpan.FromSeconds(20), MovieBatchSize: 5, SeriesBatchSize: 2);

        var service = CreateService(storage, timeProvider, libraries, movies, options);
        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);

        var first = await service.RunSliceAsync(run.Run.Id, CancellationToken.None);
        Assert.True(first.MoreWorkRemains);
        Assert.Equal(5, first.ProcessedTotal);

        // Everything after this point goes through a service built from scratch,
        // which is what a restarted process gets. The only thing carried across
        // is the run row.
        var afterRestart = CreateService(storage, timeProvider, libraries, movies, options);

        var resumable = await afterRestart.ListResumableRunsAsync(
            timeProvider.GetUtcNow().AddMinutes(5),
            take: 25,
            CancellationToken.None);
        var candidate = Assert.Single(resumable);
        Assert.Equal(run.Run.Id, candidate.RunId);
        Assert.Equal(5, candidate.ProcessedCount);

        await DrainAsync(afterRestart, run.Run.Id);

        var imported = await movies.ListAsync(CancellationToken.None);
        Assert.Equal(20, imported.Count);
        Assert.Equal(20, imported.Select(item => item.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_keeps_what_was_already_imported()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 20);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage,
            timeProvider,
            libraries,
            movies,
            new LibraryImportSliceOptions(MaxItemsPerSlice: 5, TimeSpan.FromSeconds(20), MovieBatchSize: 5, SeriesBatchSize: 2));

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);
        await service.RunSliceAsync(run.Run.Id, CancellationToken.None);

        var cancelled = await service.SetStateAsync(libraryId, LibraryImportRunStatuses.Cancelled, CancellationToken.None);
        Assert.NotNull(cancelled);
        Assert.Equal(LibraryImportRunStatuses.Cancelled, cancelled.Run.Status);

        var afterCancel = await service.RunSliceAsync(run.Run.Id, CancellationToken.None);
        Assert.False(afterCancel.MoreWorkRemains);
        Assert.Equal(0, afterCancel.ProcessedInSlice);

        // The five already brought in stay in the catalogue. Cancelling an
        // import stops it; it does not undo it.
        Assert.Equal(5, (await movies.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Pausing_holds_the_run_until_it_is_resumed()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 12);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage,
            timeProvider,
            libraries,
            movies,
            new LibraryImportSliceOptions(MaxItemsPerSlice: 4, TimeSpan.FromSeconds(20), MovieBatchSize: 4, SeriesBatchSize: 2));

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);
        await service.RunSliceAsync(run.Run.Id, CancellationToken.None);

        var paused = await service.SetStateAsync(libraryId, LibraryImportRunStatuses.Paused, CancellationToken.None);
        Assert.NotNull(paused);
        Assert.Equal(LibraryImportRunStatuses.Paused, paused.Run.Status);

        var whilePaused = await service.RunSliceAsync(run.Run.Id, CancellationToken.None);
        Assert.False(whilePaused.MoreWorkRemains);
        Assert.Equal(4, (await movies.ListAsync(CancellationToken.None)).Count);

        // A paused run is not a stalled one, so the resume sweep must leave it
        // alone however long it sits there.
        Assert.Empty(await service.ListResumableRunsAsync(
            timeProvider.GetUtcNow().AddDays(1),
            take: 25,
            CancellationToken.None));

        var resumed = await service.SetStateAsync(libraryId, LibraryImportRunStatuses.Running, CancellationToken.None);
        Assert.NotNull(resumed);

        await DrainAsync(service, run.Run.Id);
        Assert.Equal(12, (await movies.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Episodes_are_imported_and_shows_without_episode_numbers_are_set_aside()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = Path.Combine(storage.DataRoot, "tv");
        Directory.CreateDirectory(rootPath);

        var goodShow = Path.Combine(rootPath, "Northern Signal (2019)", "Season 01");
        Directory.CreateDirectory(goodShow);
        await File.WriteAllTextAsync(Path.Combine(goodShow, "Northern.Signal.S01E01.1080p.WEB-DL.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(goodShow, "Northern.Signal.S01E02.1080p.WEB-DL.mkv"), string.Empty);

        var vagueShow = Path.Combine(rootPath, "Quiet Archive (2021)");
        Directory.CreateDirectory(vagueShow);
        await File.WriteAllTextAsync(Path.Combine(vagueShow, "part one.mkv"), string.Empty);

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "tv");
        var series = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider), series: series);

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);
        await DrainAsync(service, run.Run.Id);

        var imported = await series.ListAsync(CancellationToken.None);
        Assert.Equal(2, imported.Count);

        var detail = await series.GetInventoryDetailAsync(
            imported.Single(item => item.Title == "Northern Signal").Id,
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.EpisodeCount);
        Assert.Equal(2, detail.ImportedEpisodeCount);

        // The vague show is still imported — it exists and the user can see it.
        // What is set aside is the claim to know which episodes it has.
        var issue = Assert.Single(await service.ListIssuesAsync(libraryId, 50, CancellationToken.None));
        Assert.Equal("ambiguousEpisode", issue.Kind);
        Assert.Contains("Quiet Archive", issue.SourcePath);

        var finished = await service.GetProgressAsync(libraryId, CancellationToken.None);
        Assert.NotNull(finished);
        Assert.Equal(LibraryImportRunStatuses.Completed, finished.Run.Status);
        Assert.Equal(1, finished.Run.DeferredCount);
    }

    [Fact]
    public async Task Folders_with_no_video_are_walked_past_rather_than_stopping_the_run()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 4);
        Directory.CreateDirectory(Path.Combine(rootPath, "0000 extras"));
        await File.WriteAllTextAsync(Path.Combine(rootPath, "0000 extras", "notes.txt"), "no video here");

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage,
            timeProvider,
            libraries,
            movies,
            new LibraryImportSliceOptions(MaxItemsPerSlice: 2, TimeSpan.FromSeconds(20), MovieBatchSize: 2, SeriesBatchSize: 2));

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);
        await DrainAsync(service, run.Run.Id);

        Assert.Equal(4, (await movies.ListAsync(CancellationToken.None)).Count);

        var finished = await service.GetProgressAsync(libraryId, CancellationToken.None);
        Assert.NotNull(finished);
        Assert.Equal(LibraryImportRunStatuses.Completed, finished.Run.Status);
        Assert.Equal(5, finished.Run.ProcessedCount);
    }

    [Fact]
    public async Task Importing_the_same_library_again_updates_rather_than_duplicates()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = CreateMovieTree(storage, count: 6);
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, libraries, movies);

        var first = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(first);
        await DrainAsync(service, first.Run.Id);

        var second = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first.Run.Id, second.Run.Id);
        await DrainAsync(service, second.Run.Id);

        // Replaying an import is how a resume works, so it has to be safe: the
        // second run finds everything already there and creates nothing.
        Assert.Equal(6, (await movies.ListAsync(CancellationToken.None)).Count);

        var finished = await service.GetProgressAsync(libraryId, CancellationToken.None);
        Assert.NotNull(finished);
        Assert.Equal(0, finished.Run.ImportedCount);
        Assert.Equal(6, finished.Run.SkippedCount);
    }

    [Fact]
    public async Task Real_release_folder_names_import_as_clean_titles()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-20T08:00:00Z"));
        await InitializeAsync(storage, timeProvider);

        var rootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(rootPath);
        foreach (var release in new[]
                 {
                     "Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS",
                     "Conclave.2024.2160p.UHD.BRRip.HEVC.TrueHD.Atmos.7.1-PENGUIN",
                     "Old.Film.1998.DVDRip.XviD.AC3.2.0.PROPER.REPACK.INTERNAL-CLASSIC"
                 })
        {
            var folder = Path.Combine(rootPath, release);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, $"{release}.mkv"), string.Empty);
        }

        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var libraryId = await CreateLibraryAsync(libraries, rootPath, "movies");
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, libraries, movies);

        var run = await service.StartAsync(libraryId, CancellationToken.None);
        Assert.NotNull(run);
        await DrainAsync(service, run.Run.Id);

        var imported = await movies.ListAsync(CancellationToken.None);
        Assert.Equal(["Arrival", "Conclave", "Old Film"], imported.Select(item => item.Title).OrderBy(title => title).ToArray());
    }

    private static async Task<int> DrainAsync(IExistingLibraryImportService service, string runId)
    {
        var slices = 0;
        while (true)
        {
            var outcome = await service.RunSliceAsync(runId, CancellationToken.None);
            slices++;

            if (!outcome.MoreWorkRemains)
            {
                return slices;
            }

            Assert.True(slices < 200, "Import did not converge.");
        }
    }

    private static string CreateMovieTree(TestStorage storage, int count)
    {
        var rootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(rootPath);

        for (var index = 1; index <= count; index++)
        {
            var name = $"Silent Harbour {index} ({1990 + index})";
            var folder = Path.Combine(rootPath, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"{name}.1080p.BluRay.mkv"), string.Empty);
        }

        return rootPath;
    }

    private static ExistingLibraryImportService CreateService(
        TestStorage storage,
        TimeProvider timeProvider,
        ILibrariesRepository libraries,
        IMovieCatalogRepository movies,
        LibraryImportSliceOptions? sliceOptions = null,
        ISeriesCatalogRepository? series = null)
        => new(
            libraries,
            new SqliteLibraryImportRunsRepository(storage.Factory, timeProvider),
            movies,
            series ?? new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            timeProvider,
            sliceOptions);

    private static async Task<string> CreateLibraryAsync(
        ILibrariesRepository libraries,
        string rootPath,
        string mediaType)
    {
        var library = await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: mediaType == "movies" ? "Movies" : "TV",
                MediaType: mediaType,
                Purpose: "Main",
                RootPath: rootPath,
                DownloadsPath: Path.Combine(rootPath, "..", "downloads"),
                QualityProfileId: null,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        return library.Id;
    }

    private static async Task InitializeAsync(TestStorage storage, TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
