using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
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

    [Fact]
    public async Task Tv_import_uses_persisted_airdate_numbering_for_rename_and_catalogue_identity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Daily.Show.2026-04-29.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}]");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest(
                "Daily Show",
                2026,
                "tt1234567",
                SeriesType: SeriesTypes.Daily,
                NumberingScheme: SeriesNumberingSchemes.AirDate),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "The Episode", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var request = new ImportExecuteRequest(
            Preview: new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Daily Show",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id,
                SeriesType: SeriesTypes.Daily,
                NumberingScheme: SeriesNumberingSchemes.AirDate),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.EndsWith(
            Path.Combine("Daily Show (2026)", "Daily Show - S01E03 - The Episode [WEB 1080p].mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(preview.Warnings, warning => warning.Contains("could not safely determine", StringComparison.OrdinalIgnoreCase));

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Response?.CatalogUpdated);
        Assert.True(File.Exists(preview.DestinationPath));

        var detail = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episode = Assert.Single(detail!.Episodes);
        Assert.True(episode.HasFile);
        Assert.Equal(3, episode.EpisodeNumber);

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT file_path FROM episode_entries WHERE id = @episodeId;";
        var episodeId = command.CreateParameter();
        episodeId.ParameterName = "@episodeId";
        episodeId.Value = episode.EpisodeId;
        command.Parameters.Add(episodeId);
        Assert.Equal(preview.DestinationPath, await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tv_import_uses_persisted_absolute_numbering_for_rename_and_catalogue_identity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Anime.Show.101.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}]");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest(
                "Anime Show",
                2026,
                "tt1234567",
                SeriesType: SeriesTypes.Anime,
                NumberingScheme: SeriesNumberingSchemes.Absolute),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "The Beginning", null, timeProvider.GetUtcNow(), AbsoluteNumber: 101)],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var request = new ImportExecuteRequest(
            Preview: new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Anime Show",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id,
                SeriesType: SeriesTypes.Anime,
                NumberingScheme: SeriesNumberingSchemes.Absolute),
            TransferMode: "copy",
            Overwrite: false,
            AllowCopyFallback: true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.EndsWith(
            Path.Combine("Anime Show (2026)", "Anime Show - S01E03 - The Beginning [WEB 1080p].mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(preview.Warnings, warning => warning.Contains("could not safely determine", StringComparison.OrdinalIgnoreCase));

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Response?.CatalogUpdated);
        Assert.True(File.Exists(preview.DestinationPath));

        var detail = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episode = Assert.Single(detail!.Episodes);
        Assert.True(episode.HasFile);
        Assert.Equal(3, episode.EpisodeNumber);
        Assert.Equal(101, episode.AbsoluteNumber);

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM episode_entries WHERE id = @episodeId;";
        var episodeId = command.CreateParameter();
        episodeId.ParameterName = "@episodeId";
        episodeId.Value = episode.EpisodeId;
        command.Parameters.Add(episodeId);
        Assert.Equal(preview.DestinationPath, await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tv_multi_episode_import_persists_one_library_destination_for_every_episode()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Example.Show.S01E02E03.2160p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Example Show", 2026, "tt7654321"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 3, "Three", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);

        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                sourcePath,
                Path.GetFileName(sourcePath),
                "tv",
                "Example Show",
                2026,
                [],
                [],
                null,
                null,
                SeriesId: series.Id,
                SeriesType: SeriesTypes.Standard,
                NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy",
            Overwrite: false,
            AllowCopyFallback: true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.Equal(Path.GetFileName(sourcePath), Path.GetFileName(preview.DestinationPath));

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(preview.DestinationPath));

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT episode_number, file_path, imported_utc
            FROM episode_entries
            WHERE series_id = @seriesId AND season_number = 1 AND episode_number IN (2, 3)
            ORDER BY episode_number;
            """;
        var seriesId = command.CreateParameter();
        seriesId.ParameterName = "@seriesId";
        seriesId.Value = series.Id;
        command.Parameters.Add(seriesId);

        var imported = new List<(int EpisodeNumber, string FilePath, string ImportedUtc)>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            imported.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Equal([2, 3], imported.Select(item => item.EpisodeNumber).ToArray());
        Assert.All(imported, item => Assert.Equal(preview.DestinationPath, item.FilePath));
        Assert.Single(imported.Select(item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Single(imported.Select(item => item.ImportedUtc).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Tv_season_label_is_review_only_but_an_explicit_multi_file_pack_commits_every_episode_and_retries_idempotently()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        var qualityRepository = new SqliteQualityRepository(storage.Factory, timeProvider);
        var profile = await qualityRepository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Pack evidence",
                "tv",
                "WEB 2160p",
                "WEB 1080p,WEB 2160p",
                null,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: true),
            CancellationToken.None);
        var library = await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath, profile.Id);

        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, mediaState);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Pack Review", 2026, "tt7654321"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);

        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: seriesRepository,
            qualityRepository: qualityRepository);
        var seasonLabelPath = Path.Combine(downloadsPath, "Pack.Review.S01.2160p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(seasonLabelPath, Enumerable.Repeat((byte)0x31, 4096).ToArray());
        File.SetLastWriteTimeUtc(seasonLabelPath, DateTime.UtcNow.AddMinutes(-1));
        var seasonLabelRequest = new ImportExecuteRequest(
            new ImportPreviewRequest(
                seasonLabelPath,
                Path.GetFileName(seasonLabelPath),
                "tv",
                "Pack Review",
                2026,
                [],
                [],
                null,
                null,
                SeriesId: series.Id,
                SeriesType: SeriesTypes.Standard,
                NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy",
            Overwrite: false,
            AllowCopyFallback: true);

        var seasonLabelResult = await service.ExecuteAsync(seasonLabelRequest, CancellationToken.None);
        Assert.False(seasonLabelResult.Succeeded);
        Assert.Equal(409, seasonLabelResult.StatusCode);
        Assert.Contains("does not prove", seasonLabelResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(seasonLabelPath));

        var packDirectory = Path.Combine(downloadsPath, "Pack.Review.S01.2160p.WEB-DL");
        Directory.CreateDirectory(packDirectory);
        foreach (var episodeNumber in new[] { 1, 2 })
        {
            var path = Path.Combine(packDirectory, $"Pack.Review.S01E{episodeNumber:D2}.2160p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)(0x40 + episodeNumber), 4096).ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }

        var directoryRequest = seasonLabelRequest with
        {
            Preview = seasonLabelRequest.Preview with
            {
                SourcePath = packDirectory,
                FileName = "Pack.Review.S01.2160p.WEB-DL.mkv"
            }
        };
        var directoryPreview = await service.PreviewAsync(directoryRequest.Preview, CancellationToken.None);
        Assert.NotNull(directoryPreview.Pack);
        Assert.True(directoryPreview.Pack.CanExecute);
        Assert.False(directoryPreview.Pack.AlreadyCommitted);
        Assert.Equal(2, directoryPreview.Pack.SourceFileCount);
        Assert.Equal(2, directoryPreview.Pack.EpisodeCount);
        Assert.Equal(
            ["S01E01", "S01E02"],
            directoryPreview.Pack.Files.SelectMany(file => file.EpisodeKeys).OrderBy(key => key).ToArray());
        Assert.True(ImportFileReadiness.IsPreviewReady(directoryPreview));

        var directoryResult = await service.ExecuteAsync(directoryRequest, CancellationToken.None);
        Assert.True(directoryResult.Succeeded);
        Assert.Equal(200, directoryResult.StatusCode);
        Assert.Equal(2, directoryResult.Response!.PackFiles!.Count);
        Assert.Equal(2, Directory.GetFiles(packDirectory, "*.mkv").Length);

        var detail = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(2, detail!.Episodes.Count(episode => episode.HasFile));
        var placedFolder = Path.Combine(seriesRootPath, "Pack Review (2026)");
        Assert.Equal(2, Directory.GetFiles(placedFolder, "*.mkv").Length);

        var preferenceSnapshots = new List<PreferenceEvaluationSnapshot>();
        foreach (var file in directoryPreview.Pack.Files)
        {
            var snapshot = await mediaState.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                series.Id,
                library.Id,
                fileIdentity: null,
                CancellationToken.None,
                file.DestinationPath,
                new FileInfo(file.DestinationPath).Length);
            preferenceSnapshots.Add(Assert.IsType<PreferenceEvaluationSnapshot>(snapshot));
        }
        Assert.Equal(2, preferenceSnapshots.Select(item => item.FileIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.All(preferenceSnapshots, snapshot =>
        {
            Assert.Equal($"quality-profile/{profile.Id}", snapshot.PlanId);
            Assert.Equal("filesystem.import", snapshot.Source);
        });

        await using (var connection = await storage.Factory.OpenConnectionAsync(
                         DelunoDatabaseNames.Series,
                         CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT episode_number, file_path, imported_utc FROM episode_entries WHERE series_id = @seriesId AND season_number = 1 ORDER BY episode_number;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@seriesId";
            parameter.Value = series.Id;
            command.Parameters.Add(parameter);
            var rows = new List<(int Episode, string Path, string ImportedUtc)>();
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            Assert.Equal([1, 2], rows.Select(row => row.Episode).ToArray());
            Assert.Equal(2, rows.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Single(rows.Select(row => row.ImportedUtc).Distinct(StringComparer.Ordinal));
            Assert.All(rows, row => Assert.StartsWith(placedFolder, row.Path, StringComparison.OrdinalIgnoreCase));
        }

        var retryPreview = await service.PreviewAsync(directoryRequest.Preview, CancellationToken.None);
        Assert.True(retryPreview.Pack!.CanExecute);
        Assert.True(retryPreview.Pack.AlreadyCommitted);
        var retry = await service.ExecuteAsync(directoryRequest, CancellationToken.None);
        Assert.True(retry.Succeeded);
        Assert.Contains("already placed and catalogued", retry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("already-committed", retry.Response!.TransferModeUsed);
        Assert.All(retry.Response.PackFiles!, file => Assert.Equal("already-committed", file.TransferModeUsed));
        Assert.Equal(2, Directory.GetFiles(placedFolder, "*.mkv").Length);

        var firstPlan = directoryPreview.Pack.Files.Single(file => file.EpisodeKeys.Contains("S01E01"));
        var expectedBytes = await File.ReadAllBytesAsync(firstPlan.SourcePath);
        await File.WriteAllBytesAsync(firstPlan.DestinationPath, Enumerable.Repeat((byte)0x7F, expectedBytes.Length).ToArray());
        var tamperedRetry = await service.PreviewAsync(directoryRequest.Preview, CancellationToken.None);
        Assert.False(tamperedRetry.Pack!.CanExecute);
        Assert.False(tamperedRetry.Pack.AlreadyCommitted);
        Assert.Contains(tamperedRetry.Pack.BlockReasons, reason => reason.Contains("not the fully committed pack", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllBytesAsync(firstPlan.DestinationPath, expectedBytes);

        var afterRestart = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var restartedDetail = await afterRestart.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(2, restartedDetail!.Episodes.Count(episode => episode.HasFile));

        var recovery = await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Single(recovery.RecentCases, item => item.FailureKind == "unmatched");
        Assert.Contains(recovery.RecentCases, item => item.Summary.Contains("does not prove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Tv_pack_with_duplicate_episode_claims_is_blocked_before_any_file_is_placed()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Duplicate Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-1))],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Duplicate.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var suffix in new[] { "A", "B" })
        {
            var source = Path.Combine(packDirectory, $"Duplicate.Pack.S01E01.{suffix}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)0x55, 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.False(preview.Pack!.CanExecute);
        Assert.Contains(preview.Pack.BlockReasons, reason => reason.Contains("more than one file", StringComparison.OrdinalIgnoreCase));
        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(2, Directory.GetFiles(packDirectory, "*.mkv").Length);
        Assert.False(Directory.Exists(Path.Combine(seriesRootPath, "Duplicate Pack (2026)")));
        Assert.DoesNotContain((await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes, episode => episode.HasFile);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task Tv_pack_catalogue_failure_rolls_back_every_destination_and_keeps_every_source(string transferMode)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Rollback Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Rollback.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"Rollback.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x60 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            transferMode, false, true);
        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.True(preview.Pack!.CanExecute, string.Join(" | ", preview.Pack.BlockReasons));

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None))
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText =
                "CREATE TRIGGER fail_pack_catalogue BEFORE UPDATE OF has_file ON episode_entries BEGIN SELECT RAISE(ABORT, 'injected pack catalogue failure'); END;";
            await trigger.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Directory.GetFiles(packDirectory, "*.mkv").Length);
        var placedFolder = Path.Combine(seriesRootPath, "Rollback Pack (2026)");
        Assert.Empty(Directory.Exists(placedFolder) ? Directory.GetFiles(placedFolder, "*.mkv") : []);
        Assert.DoesNotContain((await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes, episode => episode.HasFile);
        var recovery = await repository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Contains(recovery.RecentCases, item => item.Summary.Contains("rolled back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Tv_pack_replacement_requires_exact_episode_ownership_and_converges_idempotently()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Replace Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var oldDirectory = Path.Combine(downloadsPath, "old", "Replace.Pack.S01");
        Directory.CreateDirectory(oldDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(oldDirectory, $"Replace.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x20 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var oldRequest = new ImportExecuteRequest(
            new ImportPreviewRequest(
                oldDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);
        Assert.True((await service.ExecuteAsync(oldRequest, CancellationToken.None)).Succeeded);

        var installed = (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes
            .OrderBy(episode => episode.EpisodeNumber)
            .ToArray();
        var targets = new List<DispatchReplacementTarget>();
        foreach (var episode in installed)
        {
            targets.Add(new DispatchReplacementTarget(
                episode.EpisodeId,
                (await repository.GetEpisodeFilePathAsync(episode.EpisodeId, CancellationToken.None))!));
        }
        var oldBytesByPath = targets.Select(target => target.ExpectedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);

        var newDirectory = Path.Combine(downloadsPath, "new", "Replace.Pack.S01");
        Directory.CreateDirectory(newDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(newDirectory, $"Replace.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x70 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var replacementRequest = oldRequest with
        {
            Preview = oldRequest.Preview with { SourcePath = newDirectory },
            Overwrite = true,
            ForceReplacement = true,
            DispatchId = "season-dispatch-1",
            ReplacementTargets = targets
        };

        var wrongTargets = targets
            .Select((target, index) => index == 0
                ? target with { ExpectedPath = target.ExpectedPath + ".wrong" }
                : target)
            .ToArray();
        var rejected = await service.ExecuteAsync(
            replacementRequest with { ReplacementTargets = wrongTargets },
            CancellationToken.None);
        Assert.False(rejected.Succeeded);
        Assert.Equal(409, rejected.StatusCode);
        Assert.All(oldBytesByPath, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));

        var replaced = await service.ExecuteAsync(replacementRequest, CancellationToken.None);
        Assert.True(replaced.Succeeded, replaced.Message);
        Assert.Equal(2, replaced.Response!.PackFiles!.Count);
        foreach (var file in replaced.Response.PackFiles)
        {
            Assert.Equal(await File.ReadAllBytesAsync(file.SourcePath), await File.ReadAllBytesAsync(file.DestinationPath));
        }
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.deluno-pack-backup*", SearchOption.AllDirectories));

        var retry = await service.ExecuteAsync(replacementRequest, CancellationToken.None);
        Assert.True(retry.Succeeded, retry.Message);
        Assert.Equal("already-committed", retry.Response!.TransferModeUsed);
        Assert.Equal(2, (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes.Count(episode => episode.HasFile));
    }

    [Fact]
    public async Task Tv_pack_replacement_catalogue_failure_restores_every_owned_file_and_source()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Restore Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var oldDirectory = Path.Combine(downloadsPath, "old", "Restore.Pack.S01");
        Directory.CreateDirectory(oldDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(oldDirectory, $"Restore.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x30 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var baseRequest = new ImportExecuteRequest(
            new ImportPreviewRequest(
                oldDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);
        Assert.True((await service.ExecuteAsync(baseRequest, CancellationToken.None)).Succeeded);

        var targets = new List<DispatchReplacementTarget>();
        foreach (var episode in (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes)
        {
            targets.Add(new DispatchReplacementTarget(
                episode.EpisodeId,
                (await repository.GetEpisodeFilePathAsync(episode.EpisodeId, CancellationToken.None))!));
        }
        var originalBytes = targets.Select(target => target.ExpectedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);
        var newDirectory = Path.Combine(downloadsPath, "new", "Restore.Pack.S01");
        Directory.CreateDirectory(newDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(newDirectory, $"Restore.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x60 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None))
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText =
                "CREATE TRIGGER fail_pack_replacement BEFORE UPDATE OF has_file ON episode_entries BEGIN SELECT RAISE(ABORT, 'injected replacement catalogue failure'); END;";
            await trigger.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var result = await service.ExecuteAsync(
            baseRequest with
            {
                Preview = baseRequest.Preview with { SourcePath = newDirectory },
                Overwrite = true,
                ForceReplacement = true,
                DispatchId = "season-dispatch-rollback",
                ReplacementTargets = targets
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(originalBytes, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));
        Assert.Equal(2, Directory.GetFiles(newDirectory, "*.mkv").Length);
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.deluno-pack-backup*", SearchOption.AllDirectories));
        Assert.All(targets, target => Assert.Equal(target.ExpectedPath, repository.GetEpisodeFilePathAsync(target.EntityId, CancellationToken.None).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Automated_replacement_cannot_overwrite_a_destination_the_title_did_not_own()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Sintel.2010.WEB.1080p.mkv");
        var incoming = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, incoming);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var previewRequest = new ImportPreviewRequest(
            SourcePath: sourcePath,
            FileName: null,
            MediaType: "movies",
            Title: "Sintel",
            Year: 2010,
            Genres: [],
            Tags: [],
            Studio: null,
            OriginalLanguage: null);
        var preview = await service.PreviewAsync(previewRequest, CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(preview.DestinationPath)!);
        var existing = Enumerable.Repeat((byte)0xA5, 4096).ToArray();
        await File.WriteAllBytesAsync(preview.DestinationPath, existing);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: previewRequest,
                TransferMode: "copy",
                Overwrite: true,
                AllowCopyFallback: true,
                ForceReplacement: true,
                DispatchId: "dispatch-1",
                ExpectedExistingPath: Path.Combine(movieRootPath, "Some Other Title", "Some Other Title.mkv")),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("not the file this title owned", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, await File.ReadAllBytesAsync(preview.DestinationPath));
        Assert.True(File.Exists(sourcePath));

        var ownedReplacement = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: previewRequest,
                TransferMode: "copy",
                Overwrite: true,
                AllowCopyFallback: true,
                ForceReplacement: true,
                DispatchId: "dispatch-2",
                ExpectedExistingPath: preview.DestinationPath),
            CancellationToken.None);

        Assert.True(ownedReplacement.Succeeded, ownedReplacement.Message);
        Assert.Equal(incoming, await File.ReadAllBytesAsync(preview.DestinationPath));
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
        IMediaProbeService? mediaProbeService = null,
        ISeriesCatalogRepository? seriesCatalogRepository = null,
        IQualityRepository? qualityRepository = null)
        => new(
            platform,
            librariesRepository,
            movies,
            seriesCatalogRepository ?? new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            mediaProbeService ?? new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null, // IOutboundNotificationService — not needed in tests
            new NullImportResolutionsRepository(),
            null,
            null,
            NullLogger<ImportPipelineService>.Instance,
            null,
            null,
            qualityRepository);

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

    private static Task<LibraryItem> CreateTvLibraryAsync(
        ILibrariesRepository librariesRepository,
        string seriesRootPath,
        string downloadsPath,
        string? qualityProfileId = null)
        => librariesRepository.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "TV",
                MediaType: "tv",
                Purpose: "Main",
                RootPath: seriesRootPath,
                DownloadsPath: downloadsPath,
                QualityProfileId: qualityProfileId,
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

    private static async Task SaveSettingsAsync(
        SqlitePlatformSettingsRepository platform,
        string movieRootPath,
        string downloadsPath,
        string? seriesRootPath = null,
        string episodeFileFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}")
    {
        await platform.SaveAsync(
            new UpdatePlatformSettingsRequest(
                AppInstanceName: "Deluno",
                MovieRootPath: movieRootPath,
                SeriesRootPath: seriesRootPath,
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
                EpisodeFileFormat: episodeFileFormat,
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
