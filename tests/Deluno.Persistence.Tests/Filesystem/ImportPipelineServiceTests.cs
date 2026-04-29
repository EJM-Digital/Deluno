using Deluno.Filesystem;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
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

        var platform = CreatePlatformRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await platform.CreateLibraryAsync(
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
        var service = CreateService(storage, timeProvider, platform, movies);

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

        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival.2016.WEB.1080p.mkv");
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(new FileInfo(sourcePath).Length, new FileInfo(destinationPath).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.deluno-*"));

        var movie = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Equal("Arrival", movie.Title);
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

        var destinationFolder = Path.Combine(movieRootPath, "Blade Runner 2017 (2017)");
        var blockedDestinationPath = Path.Combine(destinationFolder, "Blade.Runner.2017.WEB.720p.mkv");
        Directory.CreateDirectory(blockedDestinationPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await platform.CreateLibraryAsync(
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
        var service = CreateService(storage, timeProvider, platform, movies);

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

        var platform = CreatePlatformRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(platform, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, movies);
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

        var reconciliation = CreateReconciliationService(storage, timeProvider, platform, movies);
        var report = await reconciliation.ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, item => item.Kind == "missingTrackedFile");
        Assert.Equal("critical", issue.Severity);
        Assert.Contains("Conclave", issue.Title, StringComparison.OrdinalIgnoreCase);

        var repair = await reconciliation.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"),
            CancellationToken.None);

        Assert.True(repair.Repaired);
        Assert.Empty(await movies.ListTrackedFilesAsync(issue.LibraryId, CancellationToken.None));
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
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(platform, movieRootPath, downloadsPath);

        var orphanPath = Path.Combine(movieRootPath, "Loose.Movie.2024.mkv");
        var artifactPath = Path.Combine(movieRootPath, "Loose.Movie.2024.mkv.deluno-stage-test.tmp");
        await File.WriteAllTextAsync(orphanPath, "orphan media");
        await File.WriteAllTextAsync(artifactPath, "partial import");

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var reconciliation = CreateReconciliationService(storage, timeProvider, platform, movies);
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

    private static ImportPipelineService CreateService(
        TestStorage storage,
        TimeProvider timeProvider,
        SqlitePlatformSettingsRepository platform,
        SqliteMovieCatalogRepository movies)
        => new(
            platform,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher()),
            new SuccessfulProbeService(),
            new MediaDecisionService(),
            NullLogger<ImportPipelineService>.Instance);

    private static FilesystemReconciliationService CreateReconciliationService(
        TestStorage storage,
        TimeProvider timeProvider,
        SqlitePlatformSettingsRepository platform,
        SqliteMovieCatalogRepository movies)
        => new(
            platform,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher()),
            timeProvider);

    private static async Task CreateMovieLibraryAsync(
        SqlitePlatformSettingsRepository platform,
        string movieRootPath,
        string downloadsPath)
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
            MaxItemsPerRun: 25);
        await platform.CreateLibraryAsync(request, CancellationToken.None);
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
}
