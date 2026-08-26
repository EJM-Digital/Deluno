using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

public sealed class ImportPipelineServiceTests
{
    [Fact]
    public async Task ExecuteAsync_stages_verifies_places_file_then_updates_catalog()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                Purpose: "Main",
                RootPath: movieRootPath,
                DownloadsPath: downloadsPath,
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Arrival",
                    Year: 2016,
                    Genres: ["Drama", "Science Fiction"],
                    Tags: [],
                    Studio: "Paramount",
                    OriginalLanguage: "en"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.CatalogUpdated);
        Assert.Equal("copy", result.Response.TransferModeUsed);

        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016).mkv");
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(new FileInfo(sourcePath).Length, new FileInfo(destinationPath).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.deluno-*"));
        Assert.False((await platform.GetAsync(CancellationToken.None)).WorkflowVerified);

        var movie = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Equal("Arrival", movie.Title);
    }

    [Fact]
    public async Task ExecuteAsync_resolves_the_video_inside_a_folder_shaped_source()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var releaseFolder = Path.Combine(downloadsPath, "Arrival.2016.1080p.WEB-VERIFY");
        Directory.CreateDirectory(Path.Combine(releaseFolder, "Subs"));
        Directory.CreateDirectory(movieRootPath);

        // The real feature file, a smaller sample, and non-video clutter - the
        // shape a download client reports for a multi-file torrent.
        var featurePath = Path.Combine(releaseFolder, "Arrival.2016.1080p.WEB-VERIFY.mkv");
        await File.WriteAllBytesAsync(featurePath, Enumerable.Range(0, 8192).Select(value => (byte)(value % 251)).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(releaseFolder, "arrival-sample.mkv"), new byte[512]);
        await File.WriteAllTextAsync(Path.Combine(releaseFolder, "Arrival.2016.nfo"), "clutter");
        foreach (var path in Directory.EnumerateFiles(releaseFolder, "*.*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: releaseFolder,
                    FileName: "Arrival.2016.1080p.WEB-VERIFY.mkv",
                    MediaType: "movies",
                    Title: "Arrival",
                    Year: 2016,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016).mkv");
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(new FileInfo(featurePath).Length, new FileInfo(destinationPath).Length);
    }

    [Fact]
    public async Task ExecuteAsync_names_the_folder_when_it_holds_no_importable_video()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var releaseFolder = Path.Combine(downloadsPath, "Arrival.2016.1080p.WEB-VERIFY");
        Directory.CreateDirectory(releaseFolder);
        Directory.CreateDirectory(movieRootPath);
        await File.WriteAllTextAsync(Path.Combine(releaseFolder, "Arrival.2016.nfo"), "clutter");

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: releaseFolder,
                    FileName: "Arrival.2016.1080p.WEB-VERIFY.mkv",
                    MediaType: "movies",
                    Title: "Arrival",
                    Year: 2016,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("No importable video file was found inside", result.Message);
        Assert.Contains(releaseFolder, result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_rolls_back_staged_move_when_final_placement_fails()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Blade.Runner.2017.WEB.720p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 2048).Select(value => (byte)(value % 193)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var destinationFolder = Path.Combine(movieRootPath, "Blade Runner 2017 (2017)");
        var blockedDestinationPath = Path.Combine(destinationFolder, "Blade Runner 2017 (2017).mkv");
        Directory.CreateDirectory(blockedDestinationPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies",
                MediaType: "movies",
                Purpose: "Main",
                RootPath: movieRootPath,
                DownloadsPath: downloadsPath,
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: "Blade.Runner.2017.WEB.720p.mkv",
                    MediaType: "movies",
                    Title: "Blade Runner 2017",
                    Year: 2017,
                    Genres: ["Science Fiction"],
                    Tags: [],
                    Studio: "Warner",
                    OriginalLanguage: "en"),
                TransferMode: "move",
                Overwrite: false,
                AllowCopyFallback: false),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.StatusCode);
        Assert.True(File.Exists(sourcePath));
        Assert.True(Directory.Exists(blockedDestinationPath));
        Assert.Empty(Directory.GetFiles(destinationFolder, "*.deluno-*"));
        Assert.Empty(await movies.ListAsync(CancellationToken.None));

        var recovery = await movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        var recoveryCase = Assert.Single(recovery.RecentCases);
        Assert.Equal("importFailed", recoveryCase.FailureKind);
        Assert.Contains("Blade Runner 2017", recoveryCase.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_applies_library_cleanup_only_after_a_successful_import()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var sourceFolder = Path.Combine(downloadsPath, "anime");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(sourceFolder, "Paprika.2006.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 2048).Select(value => (byte)(value % 193)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(
            libraries,
            movieRootPath,
            downloadsPath,
            cleanupMode: "remove-source-after-import",
            removeEmptySourceFolders: true);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Paprika",
                    Year: 2006,
                    Genres: ["Animation"],
                    Tags: [],
                    Studio: "Madhouse",
                    OriginalLanguage: "ja"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(sourcePath));
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Contains("source file was removed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_waits_for_a_source_file_to_be_stable_before_probing_or_importing()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "The.Matrix.1999.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 2048).Select(value => (byte)(value % 193)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "The Matrix",
                    Year: 1999,
                    Genres: ["Science Fiction"],
                    Tags: [],
                    Studio: "Warner",
                    OriginalLanguage: "en"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(425, result.StatusCode);
        Assert.Contains("still being written", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAsync_explains_that_an_unstable_source_will_wait_without_running_ffprobe()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Dune.2021.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, new ProbeMustNotRunService());

        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: null,
                MediaType: "movies",
                Title: "Dune",
                Year: 2021,
                Genres: ["Science Fiction"],
                Tags: [],
                Studio: "Warner",
                OriginalLanguage: "en"),
            CancellationToken.None);

        Assert.True(preview.SourceExists);
        Assert.Null(preview.MediaProbe);
        Assert.Contains(preview.Warnings, warning => warning.Contains("still being written", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.DecisionSteps, step => step.Contains("not stable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reconciliation_detects_missing_tracked_file_and_marks_it_missing_only_when_requested()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Conclave.2024.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 3072).Select(value => (byte)(value % 211)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var import = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Conclave",
                    Year: 2024,
                    Genres: ["Drama"],
                    Tags: [],
                    Studio: "Focus",
                    OriginalLanguage: "en"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(import.Succeeded);

        File.Delete(import.Response!.Preview.DestinationPath);

        var reconciliation = CreateReconciliationService(storage, timeProvider, libraries, movies);
        var report = await reconciliation.ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "missingTrackedFile");
        Assert.Equal("critical", issue.Severity);
        Assert.Contains("Conclave", issue.Title, StringComparison.OrdinalIgnoreCase);

        var repair = await reconciliation.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"),
            CancellationToken.None);

        Assert.True(repair.Repaired);
        var remainingTrackedFiles = new List<MovieTrackedFileItem>();
        await foreach (var file in movies.StreamTrackedFilesAsync(issue.LibraryId, CancellationToken.None))
        {
            remainingTrackedFiles.Add(file);
        }

        Assert.Empty(remainingTrackedFiles);
    }

    [Fact]
    public async Task Reconciliation_reports_orphans_and_cleans_only_deluno_artifacts_on_explicit_repair()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var orphanPath = Path.Combine(movieRootPath, "Loose.Movie.2024.mkv");
        var artifactPath = Path.Combine(movieRootPath, "Loose.Movie.2024.mkv.deluno-stage-test.tmp");
        await File.WriteAllTextAsync(orphanPath, "orphan media");
        await File.WriteAllTextAsync(artifactPath, "partial import");

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var reconciliation = CreateReconciliationService(storage, timeProvider, libraries, movies);
        var report = await reconciliation.ScanAsync(CancellationToken.None);

        var orphan = Assert.Single(report.Issues, item => item.Kind == "orphanFile");
        var artifact = Assert.Single(report.Issues, item => item.Kind == "partialImportArtifact");

        var orphanRepair = await reconciliation.RepairAsync(
            new FilesystemReconciliationRepairRequest(orphan.Id, "queue-import-review"),
            CancellationToken.None);
        Assert.True(orphanRepair.Repaired);
        Assert.True(File.Exists(orphanPath));
        Assert.Single((await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);

        var artifactRepair = await reconciliation.RepairAsync(
            new FilesystemReconciliationRepairRequest(artifact.Id, "cleanup-artifact"),
            CancellationToken.None);
        Assert.True(artifactRepair.Repaired);
        Assert.False(File.Exists(artifactPath));
    }

    /// <summary>
    /// Cleanup must never delete a file a torrent client is still sharing (#287).
    ///
    /// This is the bug in its original shape: turn cleanup on, import a torrent,
    /// and Deluno deleted the completed file directly. The torrent stayed
    /// registered against data that had vanished, so the client errored it,
    /// seeding stopped, and on a private tracker that is the user's ratio or
    /// their account. Deluno caused it and said nothing.
    ///
    /// The file now stays, and the sharing rule removes it through the client
    /// once the obligation to the site is discharged (#288).
    /// </summary>
    [Theory]
    [InlineData("qbittorrent", false)]
    [InlineData("transmission", false)]
    [InlineData("deluge", false)]
    // Usenet has no sharing phase, so nobody is left holding a broken torrent
    // and the ordinary delete is the correct thing to do.
    [InlineData("sabnzbd", true)]
    [InlineData("nzbget", true)]
    public async Task Cleanup_only_deletes_a_completed_download_no_client_is_still_sharing(
        string protocol,
        bool expectRemoved)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Interstellar.2014.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath, cleanupMode: "remove-source-after-import");

        var connections = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                Name: "Test client", Protocol: protocol, Host: "localhost", Port: 8080,
                Username: null, Password: null, EndpointUrl: "http://localhost:8080",
                MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null,
                Priority: 1, IsEnabled: true),
            CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: "movies-main", mediaType: "movies", entityType: "movie", entityId: "movie-123",
            releaseName: "Interstellar.2014.WEB.1080p", indexerName: "TestIndexer",
            downloadClientId: client.Id, downloadClientName: client.Name,
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = new ImportPipelineService(
            platform, libraries, movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            jobStore, new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null,
            new NullImportResolutionsRepository(),
            new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider),
            connections,
            NullLogger<ImportPipelineService>.Instance,
            null);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: true, AllowCopyFallback: true)
            {
                DispatchId = dispatchId
            },
            CancellationToken.None);

        Assert.True(result.Response?.Executed);
        Assert.Equal(expectRemoved, !File.Exists(sourcePath));

        if (!expectRemoved)
        {
            // And it says so, rather than leaving the user to wonder why the
            // download drive did not shrink.
            Assert.Contains("still sharing", result.Response!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A file no download client ever owned — a manual import, a watched folder,
    /// an existing-library scan — has nobody else to tidy it, so cleanup still
    /// removes it directly.
    /// </summary>
    [Fact]
    public async Task Cleanup_still_removes_a_source_that_never_came_from_a_client()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Interstellar.2014.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath, cleanupMode: "remove-source-after-import");

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: true, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Response?.Executed);
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task Import_with_dispatchId_records_resolution()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Interstellar.2014.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var realtime = new RecordingRealtimeEventPublisher();
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, realtime, new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: "movies-main",
            mediaType: "movies",
            entityType: "movie",
            entityId: "movie-123",
            releaseName: "Interstellar.2014.WEB.1080p",
            indexerName: "TestIndexer",
            downloadClientId: "client-1",
            downloadClientName: "TestClient",
            status: "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(dispatchId);
        Assert.NotEmpty(dispatchId);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var importResolutions = new SqliteImportResolutionsRepository(storage.Factory, timeProvider);
        var service = new ImportPipelineService(
            platform,
            libraries,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            jobStore,
            new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null, // IOutboundNotificationService — not needed in tests
            importResolutions,
            dispatches,
            null,
            NullLogger<ImportPipelineService>.Instance,
            realtime);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Interstellar",
                    Year: 2014,
                    Genres: ["Science Fiction"],
                    Tags: [],
                    Studio: "Paramount",
                    OriginalLanguage: "en"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.CatalogUpdated);
        Assert.Equal(
            [(dispatchId, "Interstellar.2014.WEB.1080p", "movies")],
            realtime.DispatchImportsStarted);
        Assert.True((await platform.GetAsync(CancellationToken.None)).WorkflowVerified);

        var resolutions = await importResolutions.GetDispatchResolutionsAsync(dispatchId, CancellationToken.None);
        Assert.Single(resolutions);
        var resolution = resolutions[0];
        Assert.Equal(dispatchId, resolution.DispatchId);
        Assert.Equal("movies", resolution.MediaType);
        Assert.NotNull(resolution.CatalogId);
        Assert.Equal("movie", resolution.CatalogItemType);
        Assert.True(resolution.IsSuccessful);
    }

    private static async Task InitializeAllAsync(TestStorage storage, TimeProvider timeProvider)
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
        await new JobsSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static SqlitePlatformSettingsRepository CreatePlatformRepository(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

    private static SqliteLibrariesRepository CreateLibrariesRepository(
        TestStorage storage,
        TimeProvider timeProvider)
        => new(storage.Factory, timeProvider);

    private static ImportPipelineService CreateService(
        TestStorage storage,
        TimeProvider timeProvider,
        SqlitePlatformSettingsRepository platform,
        ILibrariesRepository librariesRepository,
        SqliteMovieCatalogRepository movies,
        IMediaProbeService? mediaProbeService = null)
        => new(
            platform,
            librariesRepository,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            mediaProbeService ?? new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null, // IOutboundNotificationService — not needed in tests
            new NullImportResolutionsRepository(),
            null,
            null,
            NullLogger<ImportPipelineService>.Instance,
            null);

    private static FilesystemReconciliationService CreateReconciliationService(
        TestStorage storage,
        TimeProvider timeProvider,
        ILibrariesRepository librariesRepository,
        SqliteMovieCatalogRepository movies)
        => new(
            librariesRepository,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            timeProvider);

    private static async Task CreateMovieLibraryAsync(
        ILibrariesRepository librariesRepository,
        string movieRootPath,
        string downloadsPath,
        string cleanupMode = "keep-source",
        bool removeEmptySourceFolders = false)
    {
        var request = new CreateLibraryRequest(
            Name: "Movies",
            MediaType: "movies",
            Purpose: "Main",
            RootPath: movieRootPath,
            DownloadsPath: downloadsPath,
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
            MaxItemsPerRun: 25,
            CleanupMode: cleanupMode,
            RemoveEmptySourceFolders: removeEmptySourceFolders);
        await librariesRepository.CreateLibraryAsync(request, CancellationToken.None);
    }

    private static async Task SaveSettingsAsync(
        SqlitePlatformSettingsRepository platform,
        string movieRootPath,
        string downloadsPath)
    {
        await platform.SaveAsync(
            new UpdatePlatformSettingsRequest(
                AppInstanceName: "Deluno",
                MovieRootPath: movieRootPath,
                SeriesRootPath: null,
                DownloadsPath: downloadsPath,
                IncompleteDownloadsPath: null,
                AutoStartJobs: false,
                EnableNotifications: false,
                RenameOnImport: true,
                UseHardlinks: false,
                CleanupEmptyFolders: false,
                RemoveCompletedDownloads: false,
                UnmonitorWhenCutoffMet: false,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: null,
                HostPort: 5099,
                UrlBase: null,
                RequireAuthentication: true,
                UiTheme: "system",
                UiDensity: "comfortable",
                DefaultMovieView: "grid",
                DefaultShowView: "grid",
                MetadataNfoEnabled: false,
                MetadataArtworkEnabled: false,
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "broker",
                MetadataBrokerUrl: null,
                MetadataTmdbApiKey: null,
                MetadataOmdbApiKey: null,
                ReleaseNeverGrabPatterns: null),
            CancellationToken.None);
    }

    private sealed class SuccessfulProbeService : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo(
                Status: "succeeded",
                Tool: "test",
                Message: null,
                DurationSeconds: 7200,
                Container: "matroska",
                Bitrate: 12_000_000,
                VideoStreams:
                [
                    new MediaVideoStreamInfo(
                        Index: 0,
                        Codec: "h264",
                        Profile: "High",
                        Width: 1920,
                        Height: 1080,
                        PixelFormat: "yuv420p",
                        FrameRate: 23.976,
                        Bitrate: 10_000_000,
                        Language: "eng")
                ],
                AudioStreams: [],
                SubtitleStreams: []));
    }

    private sealed class ProbeMustNotRunService : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("ffprobe should not run for an unstable source file.");
    }
}
