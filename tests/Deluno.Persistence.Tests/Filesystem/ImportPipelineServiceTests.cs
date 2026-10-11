using System.Text.Json;
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
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Http;
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
                SearchIntervalMinutes: 6,
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
                    OriginalLanguage: "en",
                    // This is the baseline happy-path test, not a test of
                    // identity resolution: a confirmed id clears the "no
                    // confirmed metadata match" guard (#643) so the import
                    // reaches the catalogue-update logic under test.
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.CatalogUpdated);
        Assert.Equal("copy", result.Response.TransferModeUsed);

        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016) [WEB 1080p].mkv");
        // #892: a copy import's original is checked and cleared by default,
        // once nothing (no client, no sharing rule) still needs it.
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(4096, new FileInfo(destinationPath).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.deluno-*"));
        Assert.False((await platform.GetAsync(CancellationToken.None)).WorkflowVerified);

        var movie = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Equal("Arrival", movie.Title);
    }

    /// <summary>
    /// #852: once a title Deluno acquired through a Seerr approval (#851)
    /// finishes importing, Deluno reports it available back to that Seerr
    /// server, the same call Overseerr's own scanners make.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_reports_the_import_available_back_to_seerr_for_a_seerr_linked_movie()
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
                Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: movieRootPath, DownloadsPath: downloadsPath,
                QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: true, MissingSearchEnabled: true,
                UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25),
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        // Pre-existing, the way a title Seerr approved already is by the time
        // #851 adds it — the import that follows must find this same row.
        var existing = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, "tt2543164"), CancellationToken.None);

        var seerrRepository = new SqliteSeerrRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var seerrConnection = await seerrRepository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        var seerrAcquisitions = new SqliteSeerrAcquisitionsRepository(storage.Factory, timeProvider);
        await seerrAcquisitions.RecordAsync(seerrConnection!.Id, seerrConnection.Name, "movies", existing.Id, "Arrival", seerrMediaId: 99, "added", null, null, CancellationToken.None);

        var seerrClient = new RecordingSeerrClient();
        var service = CreateService(storage, timeProvider, platform, libraries, movies, seerrAcquisitions: seerrAcquisitions, seerrRepository: seerrRepository, seerrClient: seerrClient);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies", Title: "Arrival", Year: 2016,
                    Genres: [], Tags: [], Studio: null, OriginalLanguage: null, ImdbId: "tt2543164",
                    // The catalogue write this import lands on: without a
                    // grab's own dispatch, this is the only way the import
                    // pipeline's CatalogImportResult carries back which movie
                    // row the file belongs to (see MarkCatalogImportedAsync's
                    // "EntityId = dispatch?.EntityId ?? request.SeriesId").
                    SeriesId: existing.Id),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(seerrConnection.Id, Assert.Single(seerrClient.MarkedAvailable).ConnectionId);
        Assert.Equal(99, Assert.Single(seerrClient.MarkedAvailable).SeerrMediaId);

        var updated = Assert.Single(await seerrAcquisitions.ListRecentAsync(seerrConnection.Id, 10, CancellationToken.None));
        Assert.Equal("available", updated.Status);
    }

    /// <summary>
    /// #852: a Seerr outage while reporting availability must never fail an
    /// import that already landed — the failure is recorded on the
    /// acquisition (so the Seerr settings card shows it) instead.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_records_an_available_failed_status_when_seerr_cannot_be_reached()
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
                Name: "Movies", MediaType: "movies", Purpose: "Main", RootPath: movieRootPath, DownloadsPath: downloadsPath,
                QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: true, MissingSearchEnabled: true,
                UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25),
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var existing = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, "tt2543164"), CancellationToken.None);

        var seerrRepository = new SqliteSeerrRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var seerrConnection = await seerrRepository.SaveAsync(null, new SaveSeerrRequest("Lab Seerr", "http://127.0.0.1:5055", "key-1", true), CancellationToken.None);
        var seerrAcquisitions = new SqliteSeerrAcquisitionsRepository(storage.Factory, timeProvider);
        await seerrAcquisitions.RecordAsync(seerrConnection!.Id, seerrConnection.Name, "movies", existing.Id, "Arrival", seerrMediaId: 99, "added", null, null, CancellationToken.None);

        var seerrClient = new UnreachableSeerrClient();
        var service = CreateService(storage, timeProvider, platform, libraries, movies, seerrAcquisitions: seerrAcquisitions, seerrRepository: seerrRepository, seerrClient: seerrClient);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies", Title: "Arrival", Year: 2016,
                    Genres: [], Tags: [], Studio: null, OriginalLanguage: null, ImdbId: "tt2543164",
                    SeriesId: existing.Id),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);

        var updated = Assert.Single(await seerrAcquisitions.ListRecentAsync(seerrConnection.Id, 10, CancellationToken.None));
        Assert.Equal("available-failed", updated.Status);
        Assert.Equal($"Seerr on {Deluno.Contracts.DelunoHostName.Current} is unreachable", updated.Message);
    }

    private sealed class UnreachableSeerrClient : Deluno.Integrations.Seerr.ISeerrClient
    {
        public Task<Deluno.Connections.Contracts.SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<Deluno.Integrations.Seerr.SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<Deluno.Integrations.Seerr.SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken)
            => Task.FromResult<Deluno.Integrations.MediaServers.MediaServerAnswer<bool>>(new Deluno.Integrations.MediaServers.MediaServerAnswer<bool>.Unreachable($"{connection.Name} is unreachable"));
    }

    private sealed class RecordingSeerrClient : Deluno.Integrations.Seerr.ISeerrClient
    {
        public List<(string ConnectionId, int SeerrMediaId)> MarkedAvailable { get; } = [];

        public Task<Deluno.Connections.Contracts.SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<Deluno.Integrations.Seerr.SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<Deluno.Integrations.Seerr.SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Deluno.Integrations.MediaServers.MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken)
        {
            MarkedAvailable.Add((connection.Id, seerrMediaId));
            return Task.FromResult<Deluno.Integrations.MediaServers.MediaServerAnswer<bool>>(new Deluno.Integrations.MediaServers.MediaServerAnswer<bool>.Reported(true));
        }
    }

    /// <summary>
    /// #764: RenameFoldersOnImport and RenameOnImport are independent -- all
    /// four combinations, against a source that is itself a release folder
    /// (the only case Deluno has a name to keep for).
    /// </summary>
    [Theory]
    [InlineData(false, false, "Arrival.2016.WEB.1080p-FLUX", "Arrival.2016.WEB.1080p-FLUX.mkv")]
    [InlineData(false, true, "Arrival.2016.WEB.1080p-FLUX", "Arrival (2016).mkv")]
    [InlineData(true, false, "Arrival (2016)", "Arrival.2016.WEB.1080p-FLUX.mkv")]
    [InlineData(true, true, "Arrival (2016)", "Arrival (2016).mkv")]
    public async Task Preview_renames_the_movie_folder_and_file_independently(
        bool renameFoldersOnImport,
        bool renameOnImport,
        string expectedFolderName,
        string expectedFileName)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        // The release folder a download client reports for a multi-file
        // torrent -- the only "existing folder name" Deluno ever has to
        // decide whether to keep.
        var sourceFolder = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p-FLUX");
        Directory.CreateDirectory(sourceFolder);
        var sourcePath = Path.Combine(sourceFolder, "Arrival.2016.WEB.1080p-FLUX.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year})",
            renameOnImport: renameOnImport,
            renameFoldersOnImport: renameFoldersOnImport);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourceFolder,
                FileName: null,
                MediaType: "movies",
                Title: "Arrival",
                Year: 2016,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null),
            CancellationToken.None);

        Assert.Equal(
            Path.Combine(movieRootPath, expectedFolderName, expectedFileName),
            preview.DestinationPath);
    }

    /// <summary>
    /// #806: Remove completed is a per-client choice. Turned on for the
    /// client a dispatch came from, a successful import also asks that
    /// client to let go of the finished download and its data — the same
    /// gateway the sharing rule already uses (#288), not a duplicate path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_removes_the_completed_download_when_its_client_has_Remove_completed_on()
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
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var connections = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true,
                RemoveCompleted: true),
            CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: "movies-main",
            mediaType: "movies",
            entityType: "movie",
            entityId: "movie-arrival",
            releaseName: "Arrival.2016.WEB.1080p",
            indexerName: "TestIndexer",
            downloadClientId: client.Id,
            downloadClientName: client.Name,
            status: "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "arrival-hash", 4096, completed: true, CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var gateway = new FakeDownloadClientActionGateway();
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: dispatches,
            connectionsRepository: connections,
            downloadClientActions: gateway);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: ["Drama"], Tags: [],
                    Studio: null, OriginalLanguage: "en", ImdbId: "tt2543164"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(client.Id, gateway.LastClientId);
        Assert.Equal("arrival-hash", gateway.LastQueueItemId);
    }

    /// <summary>Left off (the default, #936), nothing here changes: the client keeps its own entry exactly as it does today.</summary>
    [Fact]
    public async Task ExecuteAsync_leaves_the_download_alone_when_Remove_completed_is_off()
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
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var connections = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true),
            CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: "movies-main",
            mediaType: "movies",
            entityType: "movie",
            entityId: "movie-arrival",
            releaseName: "Arrival.2016.WEB.1080p",
            indexerName: "TestIndexer",
            downloadClientId: client.Id,
            downloadClientName: client.Name,
            status: "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "arrival-hash", 4096, completed: true, CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var gateway = new FakeDownloadClientActionGateway();
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: dispatches,
            connectionsRepository: connections,
            downloadClientActions: gateway);

        await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: ["Drama"], Tags: [],
                    Studio: null, OriginalLanguage: "en", ImdbId: "tt2543164"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.Null(gateway.LastClientId);
    }

    private sealed class FakeDownloadClientActionGateway : Deluno.Recovery.Services.IDownloadClientActionGateway
    {
        public string? LastClientId { get; private set; }

        public string? LastQueueItemId { get; private set; }

        public Task<Deluno.Recovery.Services.DownloadClientRemovalResult> RemoveWithDataAsync(
            string clientId, string queueItemId, CancellationToken cancellationToken)
        {
            LastClientId = clientId;
            LastQueueItemId = queueItemId;
            return Task.FromResult(new Deluno.Recovery.Services.DownloadClientRemovalResult(true, "removed"));
        }

        public Task<Deluno.Recovery.Services.DownloadClientRemovalResult> StopSeedingAsync(
            string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// #765: a destination volume below the configured minimum free space
    /// blocks the import with a clear, dismissable reason instead of failing
    /// mid-copy.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_blocks_import_when_destination_free_space_is_below_the_configured_minimum()
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
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, minimumFreeSpaceMb: 500);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        // 100MB free, below the 500MB floor configured above.
        var service = CreateService(storage, timeProvider, platform, libraries, movies, freeSpaceBytesProbe: _ => 100L * 1024 * 1024);

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
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, result.StatusCode);
        Assert.Contains("free", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
    }

    /// <summary>#765: a destination volume above the configured minimum imports normally.</summary>
    [Fact]
    public async Task ExecuteAsync_imports_when_destination_free_space_is_above_the_configured_minimum()
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
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, minimumFreeSpaceMb: 100);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        // 500MB free, above the 100MB floor configured above.
        var service = CreateService(storage, timeProvider, platform, libraries, movies, freeSpaceBytesProbe: _ => 500L * 1024 * 1024);

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
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
    }

    /// <summary>#765: SkipFreeSpaceCheck bypasses the guard even when the probe reports almost nothing free.</summary>
    [Fact]
    public async Task ExecuteAsync_skips_the_free_space_check_when_the_switch_is_on()
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
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, minimumFreeSpaceMb: 500, skipFreeSpaceCheck: true);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, freeSpaceBytesProbe: _ => 0L);

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
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
    }

    /// <summary>#766: the configured chmod is applied to an imported file when enabled, on the non-Windows hosts CI runs on.</summary>
    [Fact]
    public async Task ExecuteAsync_applies_the_configured_chmod_when_set_permissions_on_import_is_enabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, setPermissionsOnImport: true, folderChmod: "700");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

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
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016) [WEB 1080p].mkv");
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(destinationPath));
    }

    /// <summary>#766: with the setting off, Deluno never touches the imported file's mode bits.</summary>
    [Fact]
    public async Task ExecuteAsync_leaves_permissions_untouched_when_set_permissions_on_import_is_disabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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
        File.SetUnixFileMode(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        // setPermissionsOnImport left at its off-by-default (#936) even though a chmod is configured.
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, folderChmod: "700");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

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
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016) [WEB 1080p].mkv");
        Assert.True(File.Exists(destinationPath));
        Assert.NotEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(destinationPath));
    }

    /// <summary>
    /// #857: a successful import dispatches "import.completed" outbound —
    /// not only "import.failed" — gated by the matching NotificationPreferences
    /// toggle the same way the failure path is gated.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_dispatches_import_completed_outbound_when_enabled()
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
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var outbound = new RecordingOutboundNotificationService();
        var enabledPrefs = new FakeNotificationService(new NotificationPreferences { ImportCompletedEnabled = true });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            outboundNotificationService: outbound,
            notificationService: enabledPrefs);

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
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["import.completed"], outbound.DispatchedCategories);
    }

    /// <summary>
    /// #990: import.completed carries the imported title's own tags, read
    /// while the catalogue entry the import matched already exists — so a
    /// notification connection scoped to a tag actually gets to see it. An
    /// untagged title dispatches with no tags at all, the same as before
    /// tags reached this path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_carries_the_titles_own_tags_on_import_completed()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var tagStore = new SqliteMediaTagStore(storage.Factory, timeProvider);
        var outbound = new RecordingOutboundNotificationService();
        var enabledPrefs = new FakeNotificationService(new NotificationPreferences { ImportCompletedEnabled = true });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider),
            outboundNotificationService: outbound,
            notificationService: enabledPrefs,
            mediaTagStore: tagStore);

        // The catalogue entry exists — and is tagged — before the import
        // that fills in its file, exactly like a title added ahead of time
        // (from Seerr, or by hand) and then downloaded.
        var catalogued = await movies.AddAsync(new CreateMovieRequest("Spirited Away", 2001, null), CancellationToken.None);
        await tagStore.ReplaceAsync(MediaKind.Movie, catalogued.Id, [new MediaTagAssignment("anime-tag", "anime")], CancellationToken.None);

        var sourcePath = Path.Combine(downloadsPath, "Spirited.Away.2001.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "Spirited Away 2001 WEB 1080p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Spirited Away", 2001, [], [], null, null),
                "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("import.completed", outbound.DispatchedCategories);
        var titleTags = outbound.DispatchedTitleTags["import.completed"];
        Assert.NotNull(titleTags);
        Assert.Contains("anime", titleTags);
    }

    /// <summary>
    /// #857: disabling ImportCompletedEnabled suppresses the outbound
    /// dispatch, matching how the other lifecycle toggles gate their events.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_suppresses_import_completed_outbound_when_disabled()
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
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var outbound = new RecordingOutboundNotificationService();
        var disabledPrefs = new FakeNotificationService(new NotificationPreferences { ImportCompletedEnabled = false });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            outboundNotificationService: outbound,
            notificationService: disabledPrefs);

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
                    OriginalLanguage: "en",
                    ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(outbound.DispatchedCategories);
    }

    /// <summary>#856: renaming a file on import is its own outbound trigger, off by default.</summary>
    [Fact]
    public async Task ExecuteAsync_dispatches_rename_outbound_when_enabled()
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
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var outbound = new RecordingOutboundNotificationService();
        var enabledPrefs = new FakeNotificationService(new NotificationPreferences { RenameEnabled = true });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            outboundNotificationService: outbound,
            notificationService: enabledPrefs);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(sourcePath, null, "movies", "Arrival", 2016, [], [], null, "en", ImdbId: "tt2543164"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        // The staged name (Arrival.2016.WEB.1080p.mkv) differs from the
        // destination Deluno names it (Arrival (2016) [WEB 1080p].mkv).
        Assert.Contains("rename", outbound.DispatchedCategories);
    }

    /// <summary>
    /// #856: an upgrade that replaces an existing file dispatches both
    /// "upgrade" and "file.deleted" for the file it replaced — the two
    /// triggers only fire together because an upgrade is what removed it.
    /// </summary>
    [Fact]
    public async Task An_upgrade_dispatches_upgrade_and_file_deleted_outbound_when_enabled()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var outbound = new RecordingOutboundNotificationService();
        var enabledPrefs = new FakeNotificationService(new NotificationPreferences { UpgradeEnabled = true, FileDeletedEnabled = true });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider),
            outboundNotificationService: outbound,
            notificationService: enabledPrefs);
        var catalogued = await movies.AddAsync(new CreateMovieRequest("The Wild Robot", 2024, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));
        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 WEB 720p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(firstSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);
        outbound.DispatchedCategories.Clear();

        var secondDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 REPACK 1080p BluRay", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var secondSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.REPACK.1080p.BluRay.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x22, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(secondSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: true, AllowCopyFallback: true, ForceReplacement: true, DispatchId: secondDispatchId),
            CancellationToken.None);
        Assert.True(secondResult.Succeeded, secondResult.Message);

        Assert.Contains("upgrade", outbound.DispatchedCategories);
        Assert.Contains("file.deleted", outbound.DispatchedCategories);

        // The Live screen (#1096) shows upgrades won: the import event names the quality it landed at, the quality it replaced and its size.
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT details_json FROM activity_events WHERE category = 'filesystem.import.completed' ORDER BY created_utc DESC, rowid DESC LIMIT 1;";
        var details = Assert.IsType<string>(await command.ExecuteScalarAsync());
        using var document = JsonDocument.Parse(details);
        Assert.True(document.RootElement.GetProperty("Upgraded").GetBoolean(), details);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("PreviousQuality").GetString()), details);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("NewQuality").GetString()), details);
        Assert.Equal(8192, document.RootElement.GetProperty("FileSizeBytes").GetInt64());
        // And the library it landed in (#1103), so the shelf can tag it.
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("LibraryId").GetString()), details);
    }

    /// <summary>
    /// #856: a failed import that opens a recovery case is exactly Radarr's
    /// "needs manual action" moment.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_dispatches_manual_interaction_required_outbound_when_enabled()
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
        var outbound = new RecordingOutboundNotificationService();
        var enabledPrefs = new FakeNotificationService(new NotificationPreferences { ManualInteractionRequiredEnabled = true });
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            outboundNotificationService: outbound,
            notificationService: enabledPrefs);

        // No importable video inside the release folder: a real import
        // failure, which is exactly what opens the recovery case.
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
        Assert.Contains("manual_interaction_required", outbound.DispatchedCategories);
    }

    /// <summary>
    /// #417. An import Deluno cannot name must not invent a film from the
    /// filename.
    ///
    /// <para>The lab ended up with three catalogue entries called things like
    /// <c>Sintel.2010.2160p.WEB-DL.x265-DELUNO</c> — no metadata provider, no
    /// ids, and marked as already meeting their target quality, so nothing would
    /// ever search for them or correct them. They sat in the library looking
    /// like films.</para>
    ///
    /// <para>Import recovery is where an unidentified file belongs: it exists,
    /// it explains itself, and a person can resolve it.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_sends_an_unidentified_film_to_recovery_rather_than_inventing_one()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var sourcePath = Path.Combine(downloadsPath, "Sintel.2010.2160p.WEB-DL.x265-DELUNO.mkv");
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
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        // No Title and no ImdbId: nothing here says which film this is.
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: null,
                    Year: null,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);

        // No phantom film, under any name.
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
        Assert.False(result.Response.CatalogUpdated);

        // And it is not silently dropped either — it is waiting to be resolved.
        var summary = await movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary.OpenCount);
        var recovery = Assert.Single(summary.RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
        Assert.Equal(1, summary.UnmatchedCount);
        Assert.Contains("could not tell which film it is", recovery.Summary);
    }

    /// <summary>
    /// #643, live on the rig: two separate "Say Yes to the Dress" shows
    /// existed in one library. Text-matching (release-filename inference)
    /// found the TEXT "Say Yes to the Dress" and passed it through as
    /// <c>Title</c> — non-empty, so the #417 guard above did not fire — but
    /// nothing had confirmed that text against TMDB: no SeriesId, no TvDbId,
    /// no ImdbId, and no show by that name was catalogued yet either. The
    /// old code let that sail into <c>ImportExistingAsync</c>, which did its
    /// own title+year lookup, missed, and invented a new show from bare
    /// text. It then imported 97 episodes onto that invented show, every
    /// episode title null, because there was no real metadata behind any of
    /// it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_sends_an_unconfirmed_text_matched_film_to_recovery_rather_than_inventing_one()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var sourcePath = Path.Combine(downloadsPath, "Silver.Static.2019.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        // Text-matching found a plausible title, but nothing confirmed it: no
        // ImdbId, and no film called "Silver Static" is catalogued yet.
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Silver Static",
                    Year: 2019,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);

        // No phantom film, under any name.
        Assert.Empty(await movies.ListAsync(CancellationToken.None));
        Assert.False(result.Response.CatalogUpdated);

        var summary = await movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary.OpenCount);
        var recovery = Assert.Single(summary.RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
        Assert.Equal(1, summary.UnmatchedCount);
        Assert.Contains("Silver Static", recovery.Summary);
        Assert.Contains("could not confirm", recovery.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The TV mirror of #643. Same shape as the film case above: a Title
    /// text-matched from a release filename, never confirmed against TMDB,
    /// and not the name of anything already catalogued either.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_sends_an_unconfirmed_text_matched_show_to_recovery_rather_than_inventing_one()
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

        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.S08E12.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        // Text-matching found a plausible show title, but nothing confirmed
        // it: no SeriesId, no TvDbId, no ImdbId, and no show called "Say Yes
        // to the Dress" is catalogued yet.
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "tv",
                    Title: "Say Yes to the Dress",
                    Year: 2007,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Response);

        // No phantom show, under any name — and no episodes filed against one.
        Assert.Empty(await seriesRepository.ListAsync(CancellationToken.None));
        Assert.False(result.Response.CatalogUpdated);

        var summary = await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary.OpenCount);
        var recovery = Assert.Single(summary.RecentCases);
        Assert.Equal("unmatched", recovery.FailureKind);
        Assert.Equal(1, summary.UnmatchedCount);
        Assert.Contains("Say Yes to the Dress", recovery.Summary);
        Assert.Contains("could not confirm", recovery.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The #643 guard is only about inventing a NEW catalogue row. A Title
    /// that lands on a film Deluno already has — by name and year, exactly
    /// what <c>ImportExistingAsync</c> already matches on — must keep
    /// importing onto it, with no confirmed id needed, same as before this
    /// guard existed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_matches_an_unconfirmed_title_onto_an_existing_film_by_name_rather_than_recovery()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var sourcePath = Path.Combine(downloadsPath, "Silver.Static.2019.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        // Already catalogued, by a prior confirmed import, under the very
        // name text-matching will guess this time.
        var existing = await movies.AddAsync(new CreateMovieRequest("Silver Static", 2019, null), CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Silver Static",
                    Year: 2019,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);

        // One film — the existing one — not a second, unconfirmed entry.
        var only = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Equal(existing.Id, only.Id);
        Assert.True(only.HasFile);

        var summary = await movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal(0, summary.OpenCount);
    }

    /// <summary>
    /// #643 as it actually happened on the rig: every grab was for the real
    /// show and carried its id, but the real show had been catalogued without
    /// a start year and the file name said 2007. The write matched on title
    /// and year, missed, and made a phantom from the file name. A known id is
    /// where the file goes, whatever the name and year say. Run against both
    /// the shared media store (production) and the catalogue's own path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_files_an_episode_onto_the_show_its_request_names_even_when_the_year_differs(bool sharedMediaState)
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

        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.2007.S11E15.Always.the.Pageant.Girl.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var mediaState = sharedMediaState ? new SqliteMediaStateRepository(storage.Factory, timeProvider) : null;
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, mediaState);
        // The real show, confirmed against TMDB but with no start year stored.
        var real = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Say Yes to the Dress", null, null, MetadataProvider: "tmdb", MetadataProviderId: "31306"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            real.Id,
            [new CatalogueEpisodeItem(11, 15, "Always the Pageant Girl", null, timeProvider.GetUtcNow().AddYears(-10))],
            "provider",
            CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies, seriesCatalogRepository: seriesRepository);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "tv",
                    Title: "Say Yes to the Dress",
                    Year: 2007,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null,
                    SeriesId: real.Id),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Response!.CatalogUpdated);

        var only = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None));
        Assert.Equal(real.Id, only.Id);
        var detail = await seriesRepository.GetInventoryDetailAsync(real.Id, CancellationToken.None);
        var episode = Assert.Single(detail!.Episodes, item => item.HasFile);
        Assert.Equal(11, episode.SeasonNumber);
        Assert.Equal(15, episode.EpisodeNumber);
    }

    /// <summary>
    /// The other half: an id that names nothing — the show it named has
    /// since been removed — is not a confirmation, so it cannot carry an
    /// unconfirmed title past the #643 guard into a brand-new show.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_does_not_treat_a_series_id_that_no_longer_exists_as_confirmation()
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

        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.2007.S11E13.For.His.Consideration.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, mediaState);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, seriesCatalogRepository: seriesRepository);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "tv",
                    Title: "Say Yes to the Dress",
                    Year: 2007,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null,
                    SeriesId: "01a0d310c61474feb09f69b94c56071d"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Response!.CatalogUpdated);
        Assert.Empty(await seriesRepository.ListAsync(CancellationToken.None));
        var summary = await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal("unmatched", Assert.Single(summary.RecentCases).FailureKind);
    }

    /// <summary>
    /// The TV mirror of the regression check above: a Title that lands on a
    /// show Deluno already has, by name and year, must keep importing onto
    /// it — this is the "real" Say Yes to the Dress side of #643, which was
    /// already working and must not break.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_matches_an_unconfirmed_title_onto_an_existing_show_by_name_rather_than_recovery()
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

        var sourcePath = Path.Combine(downloadsPath, "Say.Yes.to.the.Dress.S08E12.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        // Already catalogued and confirmed against TMDB, e.g. tmdbId "31306"
        // on the rig — the real show.
        var existing = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Say Yes to the Dress", 2007, null),
            CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "tv",
                    Title: "Say Yes to the Dress",
                    Year: 2007,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);

        // One show — the existing one — not a second, phantom entry.
        var only = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None));
        Assert.Equal(existing.Id, only.Id);

        var detail = await seriesRepository.GetInventoryDetailAsync(existing.Id, CancellationToken.None);
        var episode = Assert.Single(detail!.Episodes);
        Assert.True(episode.HasFile);

        var summary = await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Equal(0, summary.OpenCount);
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
        // #764: this test is about resolving the video inside a folder-shaped
        // source, not about the folder-rename toggle -- give it the
        // existing-install setting (V0083 freezes existing installs to this)
        // so its destination path keeps asserting the templated folder name
        // it always has.
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, renameFoldersOnImport: true);

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
        var destinationPath = Path.Combine(movieRootPath, "Arrival (2016)", "Arrival (2016) [WEB 1080p].mkv");
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
        var blockedDestinationPath = Path.Combine(destinationFolder, "Blade Runner 2017 (2017) [WEB 720p].mkv");
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
                SearchIntervalMinutes: 6,
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
        // The move threw; that can clear by itself, so it is retried rather than
        // treated as a refusal that tells a processor to remove its copy (#603).
        Assert.Equal(503, result.StatusCode);
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
    public async Task ExecuteAsync_refuses_a_source_file_that_is_not_stable_yet()
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

    /// <summary>
    /// #562: a processor hands its output over and then finishes with it — Weir
    /// moves its file into place and puts the original timestamps back after. The
    /// import began in that moment and failed a second later, so every processed
    /// import failed its first attempt. Seconds of waiting cost less than an attempt.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_waits_the_few_seconds_a_source_file_needs_to_settle()
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
        var service = CreateService(storage, timeProvider, platform, libraries, movies, settleWindow: TimeSpan.FromSeconds(10));

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
                    OriginalLanguage: "en",
                    // This test is about the settle-window wait, not identity
                    // resolution (#643): a confirmed id clears the "no
                    // confirmed metadata match" guard so the import reaches
                    // the settle-wait logic under test.
                    ImdbId: "tt0133093"),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
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
                    OriginalLanguage: "en",
                    // This test is about reconciliation finding a missing
                    // tracked file, which needs a real catalogue row to track
                    // in the first place; a confirmed id clears the "no
                    // confirmed metadata match" guard (#643) so the import
                    // reaches the catalogue instead of import recovery.
                    ImdbId: "tt20215234"),
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

    /// <summary>
    /// A library that is not there is one fact, not ten thousand.
    ///
    /// <para>The scan asks <c>File.Exists</c> of every tracked path in turn, so
    /// a drive that has not mounted answers false to all of them and every
    /// title in the library came back as a critical missing-file issue. Its two
    /// sibling scans already stopped at an unreachable root; this one did not,
    /// and it is the one whose repair marks titles missing.</para>
    ///
    /// <para>That mattered the moment DESIGN-007 decision 11 had those
    /// corrections applied on a schedule rather than by hand: an unmounted NAS
    /// would mark a whole library missing and start downloading it again.</para>
    /// </summary>
    [Fact]
    public async Task Reconciliation_reports_an_unreachable_library_rather_than_every_file_in_it()
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

        // The drive goes away, taking its tracked file with it. This is a
        // mount failure, not a deletion.
        Directory.Delete(movieRootPath, recursive: true);

        var reconciliation = CreateReconciliationService(storage, timeProvider, libraries, movies);
        var report = await reconciliation.ScanAsync(CancellationToken.None);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("libraryRootUnreachable", issue.Kind);
        Assert.Contains("not reachable", issue.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(issue.RepairActions);

        // The point of the whole thing: nothing is offered as missing, so
        // nothing can be marked missing and re-downloaded.
        Assert.DoesNotContain(report.Issues, item => item.Kind == "missingTrackedFile");
    }

    /// <summary>
    /// #913 / cleanup sheet K4, restart mid-copy: a "move" transfer had
    /// already consumed the original download into the staging file when the
    /// app stopped, so only the staged copy - not the original - survives.
    /// The source is "still there" in the sense the acceptance means it: a
    /// complete file exists to finish importing from.
    /// </summary>
    [Fact]
    public async Task RestartRecovery_finishes_an_import_whose_staged_copy_survived_the_restart()
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        // A "move" transfer already consumed this path into the staging file
        // below, so the original download no longer exists - only the marker
        // and the fully-copied staging file remain, exactly as a crash right
        // after the move-into-staging step would leave them.
        var sourcePath = Path.Combine(downloadsPath, "Sanctuary.2024.WEB.1080p.mkv");
        var destinationPath = Path.Combine(movieRootPath, "staged-destination.mkv");
        var stagingPath = destinationPath + ".deluno-stage-restarttest.tmp";
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(stagingPath, bytes);
        File.SetLastWriteTimeUtc(stagingPath, DateTime.UtcNow.AddMinutes(-1));
        await File.WriteAllTextAsync(
            stagingPath + ".deluno-marker.json",
            JsonSerializer.Serialize(new ImportRestartMarker(sourcePath, destinationPath, stagingPath, "movies", "Sanctuary (2024)")));

        var recovery = CreateRestartRecoveryService(libraries, service, jobStore);
        var report = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.Equal(0, report.Abandoned);
        Assert.False(File.Exists(stagingPath));
        Assert.False(File.Exists(stagingPath + ".deluno-marker.json"));
        Assert.False(File.Exists(sourcePath), "the recovered content was consumed by the fresh import, not left behind twice");

        // The reissued import carries no confirmed metadata (the marker only
        // knows a display title), so - correctly, per #643/#888 - it lands as
        // an import recovery case rather than guessing a catalogue entry. The
        // point under test is that the file was placed and the pipeline ran
        // again at all, which the recovery case proves.
        Assert.Single((await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);

        var activity = await jobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-recovered" &&
            entry.Message.Contains("Finished an import interrupted by a restart", StringComparison.Ordinal));
    }

    /// <summary>
    /// #913: restart mid-copy where a "copy" transfer left the original
    /// download untouched. Recovery should re-import from that original
    /// rather than requiring the (possibly incomplete) partial copy.
    /// </summary>
    [Fact]
    public async Task RestartRecovery_finishes_an_import_whose_original_source_survived_the_restart()
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var sourcePath = Path.Combine(downloadsPath, "Sanctuary.2024.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
        var destinationPath = Path.Combine(movieRootPath, "staged-destination.mkv");
        var stagingPath = destinationPath + ".deluno-copy-restarttest.tmp";
        await File.WriteAllTextAsync(stagingPath, "half-written");
        await File.WriteAllTextAsync(
            stagingPath + ".deluno-marker.json",
            JsonSerializer.Serialize(new ImportRestartMarker(sourcePath, destinationPath, stagingPath, "movies", "Sanctuary (2024)")));

        var recovery = CreateRestartRecoveryService(libraries, service, jobStore);
        var report = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(stagingPath), "the incomplete partial copy is never left behind");
        Assert.False(File.Exists(stagingPath + ".deluno-marker.json"));

        // As above: no confirmed metadata means this correctly becomes an
        // import recovery case rather than a guessed catalogue entry.
        Assert.Single((await movies.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
    }

    /// <summary>
    /// #913: when neither the original download nor a fully-copied staging
    /// file survived the restart, Deluno can only remove the leftover and
    /// say so - never blocklist on nothing but a restart.
    /// </summary>
    [Fact]
    public async Task RestartRecovery_tells_you_when_nothing_survived_the_restart_and_never_blocklists()
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var sourcePath = Path.Combine(downloadsPath, "Gone.2024.WEB.1080p.mkv");
        var destinationPath = Path.Combine(movieRootPath, "gone-destination.mkv");
        var stagingPath = destinationPath + ".deluno-copy-restarttest.tmp";
        await File.WriteAllTextAsync(
            stagingPath + ".deluno-marker.json",
            JsonSerializer.Serialize(new ImportRestartMarker(sourcePath, destinationPath, stagingPath, "movies", "Gone (2024)")));
        // Neither the incomplete copy nor the original download exists - the
        // marker is the only trace of the interrupted attempt.

        var recovery = CreateRestartRecoveryService(libraries, service, jobStore);
        var report = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, report.Recovered);
        Assert.Equal(1, report.Abandoned);
        Assert.False(File.Exists(stagingPath + ".deluno-marker.json"));

        var activity = await jobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-abandoned" &&
            entry.Message.Contains("Could not finish an import interrupted by a restart", StringComparison.Ordinal) &&
            entry.Message.Contains("Nothing was blocklisted", StringComparison.Ordinal));

        var summary = await movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Empty(summary.RecentCases);
    }

    /// <summary>
    /// #913: a crash between the final move and deleting the marker leaves a
    /// fully-imported file with a stray marker/staging leftover next to it.
    /// Recovery must recognise the import already finished rather than
    /// re-importing on top of it.
    /// </summary>
    [Fact]
    public async Task RestartRecovery_cleans_up_a_marker_left_behind_after_the_file_was_already_placed()
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

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var sourcePath = Path.Combine(downloadsPath, "Placed.2024.WEB.1080p.mkv");
        var destinationPath = Path.Combine(movieRootPath, "placed-destination.mkv");
        await File.WriteAllTextAsync(destinationPath, "already imported");
        var stagingPath = destinationPath + ".deluno-stage-restarttest.tmp";
        await File.WriteAllTextAsync(stagingPath, "stray leftover");
        await File.WriteAllTextAsync(
            stagingPath + ".deluno-marker.json",
            JsonSerializer.Serialize(new ImportRestartMarker(sourcePath, destinationPath, stagingPath, "movies", "Placed (2024)")));

        var recovery = CreateRestartRecoveryService(libraries, service, jobStore);
        var report = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(stagingPath));
        Assert.False(File.Exists(stagingPath + ".deluno-marker.json"));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal("already imported", await File.ReadAllTextAsync(destinationPath));
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

    /// <summary>
    /// #892: a copy import leaves a full second copy of the download's bytes
    /// on disk. Once the copy is verified (the import already succeeded),
    /// the original is checked and cleared - this must happen regardless of
    /// the opt-in "remove source after import" setting, which stays off by
    /// default (#936).
    /// </summary>
    [Fact]
    public async Task A_copy_import_deletes_its_checked_original_even_with_cleanup_off_by_default()
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
        // Deliberately no cleanupMode override: the library keeps the
        // default, off setting.
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

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
        Assert.False(File.Exists(sourcePath), "a copy import's checked original must be cleared even though the opt-in cleanup setting is off");
    }

    /// <summary>
    /// A title removed without its files and added again finds its own identical file still at the
    /// destination. The import takes that file as the title's, deletes the second copy and never
    /// overwrites; a different file at the destination is still refused.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_title_that_came_back_while_its_old_file_sits_at_the_destination_adopts_it_only_when_identical(bool identical)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Nosferatu.1922.WEB.1080p.mkv");
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var destinationPath = Path.Combine(movieRootPath, "Nosferatu (1922)", "Nosferatu (1922) [WEB 1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var existing = (byte[])bytes.Clone();
        if (!identical)
        {
            existing[100] ^= 0xFF;
            existing[existing.Length - 5] ^= 0xFF;
        }
        await File.WriteAllBytesAsync(destinationPath, existing);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Nosferatu", Year: 1922, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null, ImdbId: "tt0013442"),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.Equal(existing, await File.ReadAllBytesAsync(destinationPath));
        if (identical)
        {
            Assert.True(result.Succeeded);
            Assert.True(result.Response?.CatalogUpdated);
            Assert.False(File.Exists(sourcePath), "the second copy is deleted");
            var movie = Assert.Single(await movies.ListAsync(CancellationToken.None));
            Assert.Equal("Nosferatu", movie.Title);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal(409, result.StatusCode);
            Assert.True(File.Exists(sourcePath));
        }
    }

    /// <summary>
    /// #1254: a Usenet release sent again has a new download id, so Deluno hands it to the processor again and
    /// imports the cleaned file again. The film is already in the library at that very path. Same content settles
    /// as already in the library; different content replaces the title's own file through the recycle bin. Either
    /// way the import succeeds the first time, leaves one copy in the library and nothing in the processor's output
    /// (the scenario suite saw it retry with "The destination file already exists" until it gave up).
    /// </summary>
    [Theory]
    [InlineData("hardlink", true)]
    [InlineData("hardlink", false)]
    [InlineData("copy", true)]
    [InlineData("copy", false)]
    public async Task A_release_sent_again_with_a_new_download_id_settles_on_the_file_the_title_already_has(string transferMode, bool identical)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-12T18:50:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var readyPath = Path.Combine(storage.DataRoot, "Weir", "Ready", "Movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main",
                RootPath: movieRootPath, DownloadsPath: downloadsPath, QualityProfileId: null,
                ImportWorkflow: ImportWorkflows.RefineBeforeImport,
                ProcessorName: "External Refiner", ProcessorOutputPath: readyPath,
                ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold",
                AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25,
                CleanupMode: "keep-source"),
            CancellationToken.None);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var catalogued = await movies.AddAsync(new CreateMovieRequest("Gpreafterermj27", 2020, null), CancellationToken.None);

        const string release = "Gpreafterermj27.2020.1080p.WEB-DL.x264-GOLDEN";
        string CleanedFile(byte[] bytes)
        {
            var folder = Directory.CreateDirectory(Path.Combine(readyPath, release)).FullName;
            var file = Path.Combine(folder, release + ".mkv");
            File.WriteAllBytes(file, bytes);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
            return file;
        }

        async Task<ImportPipelineResult> ImportAsync(string source, string downloadId)
        {
            var dispatchId = await jobStore.RecordDownloadDispatchAsync(
                libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
                releaseName: release, indexerName: "TestIndexer",
                downloadClientId: "client-1", downloadClientName: "TestClient",
                status: "sent", notesJson: null, clientExternalId: downloadId, cancellationToken: CancellationToken.None);
            return await service.ExecuteAsync(
                new ImportExecuteRequest(
                    new ImportPreviewRequest(source, null, "movies", "Gpreafterermj27", 2020, [], [], null, null),
                    transferMode, Overwrite: false, AllowCopyFallback: true, DispatchId: dispatchId),
                CancellationToken.None);
        }

        var firstBytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var first = await ImportAsync(CleanedFile(firstBytes), "SABnzbd_nzo_first");
        Assert.True(first.Succeeded, first.Message);
        var libraryFile = (await movies.GetByIdAsync(catalogued.Id, CancellationToken.None))!.FilePath!;
        Assert.True(File.Exists(libraryFile), libraryFile);

        // The same release sent again: the processor cleans it again into the same path.
        var secondBytes = (byte[])firstBytes.Clone();
        if (!identical)
        {
            secondBytes[100] ^= 0xFF;
            secondBytes[secondBytes.Length - 5] ^= 0xFF;
        }
        var secondSource = CleanedFile(secondBytes);
        var second = await ImportAsync(secondSource, "SABnzbd_nzo_second");

        Assert.True(second.Succeeded, second.Message);
        Assert.NotEqual(409, second.StatusCode);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
        var film = (await movies.GetByIdAsync(catalogued.Id, CancellationToken.None))!;
        Assert.Equal(libraryFile, film.FilePath);
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(libraryFile));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(libraryFile)!, "*.mkv"));
        Assert.False(File.Exists(secondSource), "nothing is left in the processor's output");
        Assert.False(Directory.Exists(Path.GetDirectoryName(secondSource)!), "the release's folder in the processor's output is cleared too");
    }

    /// <summary>
    /// The opt-in "remove source after import" setting still governs a
    /// hardlink import unchanged (#936: never flip an existing install's
    /// automatic behaviour on) - a hardlinked source is the same bytes as the
    /// library file under a second name, not a second copy, so there is no
    /// disk space to reclaim by clearing it.
    /// </summary>
    [Fact]
    public async Task A_hardlink_import_still_keeps_its_source_when_cleanup_is_off_by_default()
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
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "hardlink", Overwrite: true, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Response?.Executed);
        Assert.True(File.Exists(sourcePath), "a hardlink import's source must be left alone while cleanup is off by default");
    }

    /// <summary>
    /// #915: a processor's copy is always removed after a successful import,
    /// regardless of the library's Cleanup mode (kept off here on purpose) --
    /// and so is everything else it left in this hand-off's own subfolder:
    /// scraps, a leftover temporary-named file, and the now-empty folder
    /// itself. A sibling hand-off's subfolder is never touched.
    /// </summary>
    [Fact]
    public async Task A_folder_hand_off_processor_output_is_fully_cleaned_up_after_import()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var processorOutputPath = Path.Combine(storage.DataRoot, "refined");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main",
                RootPath: movieRootPath, DownloadsPath: downloadsPath, QualityProfileId: null,
                ImportWorkflow: ImportWorkflows.RefineBeforeImport,
                ProcessorName: "External Refiner", ProcessorOutputPath: processorOutputPath,
                ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold",
                AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25,
                // Left off on purpose: the processor's own copy is cleaned up
                // regardless of this setting (#915), which only ever governed
                // the original download.
                CleanupMode: "keep-source"),
            CancellationToken.None);

        var handoffFolder = Directory.CreateDirectory(Path.Combine(processorOutputPath, "Interstellar.2014.WEB.1080p-DELUNO")).FullName;
        var matchedFile = Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(matchedFile, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(matchedFile, DateTime.UtcNow.AddMinutes(-1));
        // A scrap the processor also dropped, and a leftover it never
        // finished renaming away.
        await File.WriteAllTextAsync(Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.nfo"), "scrap");
        await File.WriteAllTextAsync(Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.mkv.partial"), "leftover");

        // A sibling hand-off's own subfolder, which must survive untouched.
        var siblingFolder = Directory.CreateDirectory(Path.Combine(processorOutputPath, "Arrival.2016.WEB.1080p-DELUNO")).FullName;
        var siblingFile = Path.Combine(siblingFolder, "Arrival.2016.WEB.1080p-DELUNO.mkv");
        await File.WriteAllTextAsync(siblingFile, "unrelated hand-off");

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: matchedFile, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(handoffFolder), "the whole hand-off subfolder, including its scraps and leftover temp file, should be gone");
        Assert.True(Directory.Exists(siblingFolder), "a sibling hand-off's own subfolder must never be touched");
        Assert.True(File.Exists(siblingFile));
    }

    /// <summary>
    /// #915: a loose-file hand-off shares the processor output root with
    /// other hand-offs. Only its own leftover (same name stem, a temporary
    /// suffix the processor never finished renaming away) is removed --
    /// an unrelated file sitting in the same shared folder is left alone.
    /// </summary>
    [Fact]
    public async Task A_loose_file_processor_output_only_removes_its_own_leftovers()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var processorOutputPath = Path.Combine(storage.DataRoot, "refined");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(processorOutputPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Movies", MediaType: "movies", Purpose: "Main",
                RootPath: movieRootPath, DownloadsPath: downloadsPath, QualityProfileId: null,
                ImportWorkflow: ImportWorkflows.RefineBeforeImport,
                ProcessorName: "External Refiner", ProcessorOutputPath: processorOutputPath,
                ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold",
                AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25,
                CleanupMode: "keep-source"),
            CancellationToken.None);

        var matchedFile = Path.Combine(processorOutputPath, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(matchedFile, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(matchedFile, DateTime.UtcNow.AddMinutes(-1));
        var leftover = Path.Combine(processorOutputPath, "Interstellar.2014.WEB.1080p-DELUNO.mkv.tmp");
        await File.WriteAllTextAsync(leftover, "leftover");
        var unrelatedFile = Path.Combine(processorOutputPath, "Arrival.2016.WEB.1080p-DELUNO.mkv");
        await File.WriteAllTextAsync(unrelatedFile, "unrelated hand-off");

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: matchedFile, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(matchedFile), "the matched file itself is removed once imported");
        Assert.False(File.Exists(leftover), "the leftover temp file sharing this hand-off's name stem should be removed");
        Assert.True(File.Exists(unrelatedFile), "an unrelated file in the shared output root must never be touched");
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
            realtime,
            freeSpaceBytesProbe: _ => 7_000_000_000L);

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
                    OriginalLanguage: "en",
                    // The dispatch's EntityId above ("movie-123") is not a
                    // real catalogued movie, so nothing anchors this import
                    // except the Title text. This test is about dispatch
                    // resolution recording, not identity resolution, so it
                    // needs a confirmed id to clear the "no confirmed
                    // metadata match" guard (#643).
                    ImdbId: "tt0816692"),
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

        // #1097: the drive the file landed on says how much room is left, as soon as the import is done.
        var drive = Assert.Single(realtime.LiveEventsOf<DriveSpaceChangedEvent>(LiveEventNames.DriveSpaceChanged));
        Assert.Equal(7_000_000_000L, drive.FreeBytes);
        Assert.StartsWith(movieRootPath, drive.Path, StringComparison.OrdinalIgnoreCase);

        var resolutions = await importResolutions.GetDispatchResolutionsAsync(dispatchId, CancellationToken.None);
        Assert.Single(resolutions);
        var resolution = resolutions[0];
        Assert.Equal(dispatchId, resolution.DispatchId);
        Assert.Equal("movies", resolution.MediaType);
        Assert.NotNull(resolution.CatalogId);
        Assert.Equal("movie", resolution.CatalogItemType);
        Assert.True(resolution.IsSuccessful);
    }

    /// <summary>
    /// #526: with two movie libraries, a film grabbed for the second went into
    /// the first, because the destination was "the first library of its type".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_import_goes_to_the_library_that_grabbed_it(bool named)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-17T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var firstRoot = Path.Combine(storage.DataRoot, "movies");
        var secondRoot = Path.Combine(storage.DataRoot, "refined-movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var sourcePath = Path.Combine(downloadsPath, "Dementia.13.1963.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, firstRoot, downloadsPath);
        await CreateMovieLibraryAsync(libraries, firstRoot, downloadsPath);
        await CreateMovieLibraryAsync(libraries, secondRoot, downloadsPath);
        var second = (await libraries.ListLibrariesAsync(CancellationToken.None)).Single(library => library.RootPath == secondRoot);

        var realtime = new RecordingRealtimeEventPublisher();
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, realtime, new NullDownloadDispatchesRepository());
        var service = new ImportPipelineService(
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            jobStore,
            new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null,
            null,
            null,
            null,
            NullLogger<ImportPipelineService>.Instance,
            realtime);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath,
                    FileName: null,
                    MediaType: "movies",
                    Title: "Dementia 13",
                    Year: 1963,
                    Genres: [],
                    Tags: [],
                    Studio: null,
                    OriginalLanguage: "en",
                    LibraryId: named ? second.Id : null),
                TransferMode: "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith(named ? secondRoot : firstRoot, result.Response!.Preview.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_preserves_dispatch_release_evidence_after_clean_movie_rename()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-15T04:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Big.Buck.Bunny.2008.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x42, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);

        var quality = new SqliteQualityRepository(storage.Factory, timeProvider);
        var netflixGuide = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.Name.Equals("NF", StringComparison.Ordinal)
            && GuideFormatConditions.AppliesTo(format, "movies"));
        var netflix = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                netflixGuide.Name,
                "movies",
                netflixGuide.OriginalScore,
                netflixGuide.TrashId,
                GuideFormatConditions.For(netflixGuide),
                true),
            CancellationToken.None);
        var preferredGroup = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(
                "Preferred DELUNO group",
                "movies",
                100,
                null,
                "[{\"type\":\"releaseGroup\",\"value\":\"DELUNO\",\"negate\":false,\"required\":true}]",
                true),
            CancellationToken.None);
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "E2E Movies 1080p",
                "movies",
                "WEB 1080p",
                "WEB 1080p",
                $"{netflix.Id},{preferredGroup.Id}",
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                FormatIntents: new Dictionary<string, string>
                {
                    [netflix.Id] = "prefer",
                    [preferredGroup.Id] = "prefer"
                }),
            CancellationToken.None);
        await CreateMovieLibraryAsync(
            libraries,
            movieRootPath,
            downloadsPath,
            qualityProfileId: profile.Id);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var realtime = new RecordingRealtimeEventPublisher();
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, realtime, new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        const string releaseName = "Big.Buck.Bunny.2008.1080p.NF.WEB-DL.x264-DELUNO";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id,
            "movies",
            "movie",
            "movie-big-buck-bunny",
            releaseName,
            "TestIndexer",
            "client-1",
            "TestClient",
            "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);

        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            movies,
            qualityRepository: quality,
            downloadDispatchesRepository: dispatches);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(
                    sourcePath,
                    null,
                    "movies",
                    "Big Buck Bunny",
                    2008,
                    [],
                    [],
                    null,
                    "en",
                    // The dispatch's EntityId above ("movie-big-buck-bunny")
                    // is not a real catalogued movie, so nothing anchors this
                    // import except the Title text. This test is about
                    // preference-evaluation and provenance recording, not
                    // identity resolution, so it needs a confirmed id to
                    // clear the "no confirmed metadata match" guard (#643).
                    ImdbId: "tt1254207"),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var tracked = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            tracked.Add(item);
        }
        var imported = Assert.Single(tracked);
        // The name keeps what Deluno decided - the quality, and the formats
        // TRaSH marks as belonging in a file name - without the raw release
        // name or its group. See also the test below, where the incoming file
        // name says none of it.
        Assert.Equal("Big Buck Bunny (2008) [WEB 1080p] [NF].mkv", Path.GetFileName(imported.FilePath));
        Assert.DoesNotContain("DELUNO", Path.GetFileName(imported.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x264", Path.GetFileName(imported.FilePath), StringComparison.OrdinalIgnoreCase);

        var snapshot = await mediaState.GetLatestPreferenceEvaluationSnapshotAsync(
            MediaKind.Movie,
            imported.MovieId,
            library.Id,
            fileIdentity: null,
            CancellationToken.None,
            imported.FilePath,
            imported.FileSizeBytes);
        Assert.NotNull(snapshot);
        Assert.Equal(releaseName, snapshot.ReleaseNameEvidence);
        Assert.Equal(
            new[] { netflix.Id, preferredGroup.Id }.Order(StringComparer.Ordinal),
            snapshot.MatchedRuleIds.Order(StringComparer.Ordinal));
        Assert.Contains(snapshot.Facts, fact =>
            fact.TraitId.Equals("service.netflix", StringComparison.OrdinalIgnoreCase)
            && fact.Evidence?.Source == "guide-custom-format");

        var timeline = await dispatches.GetDispatchTimelineAsync(dispatchId, CancellationToken.None);
        var provenanceEvent = Assert.Single(timeline, item => item.EventType == "file_provenance_recorded");
        using var provenance = JsonDocument.Parse(Assert.IsType<string>(provenanceEvent.DetailsJson));
        Assert.Equal(releaseName, provenance.RootElement.GetProperty("ReleaseNameEvidence").GetString());
        Assert.Equal("TestIndexer", provenance.RootElement.GetProperty("IndexerName").GetString());
        Assert.Equal("TestClient", provenance.RootElement.GetProperty("DownloadClientName").GetString());
        Assert.Equal(sourcePath, provenance.RootElement.GetProperty("SourcePath").GetString());
        Assert.Equal(imported.FilePath, provenance.RootElement.GetProperty("DestinationPath").GetString());
        Assert.Contains("DELUNO", provenance.RootElement.GetProperty("MatchedRuleNames").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(snapshot.PlanHash, provenance.RootElement.GetProperty("PreferencePlanHash").GetString());
    }

    /// <summary>
    /// The name comes from the decision, not from the incoming file name.
    ///
    /// <para>A download client hands over whatever the release put on disk -
    /// here a file called <c>video.mkv</c>. Reading the name back out of that
    /// is how a release Deluno took <em>because</em> it was Netflix at WEB
    /// 1080p lands in the library saying neither.</para>
    /// </summary>
    [Fact]
    public async Task A_file_named_nothing_still_carries_the_decision_into_its_name()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T04:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        // Nothing in this name says Netflix, WEB, or 1080p.
        var sourcePath = Path.Combine(downloadsPath, "video.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        var quality = new SqliteQualityRepository(storage.Factory, timeProvider);
        var netflixGuide = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.Name.Equals("NF", StringComparison.Ordinal)
            && GuideFormatConditions.AppliesTo(format, "movies"));
        var netflix = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(netflixGuide.Name, "movies", 0, netflixGuide.TrashId, GuideFormatConditions.For(netflixGuide), true),
            CancellationToken.None);
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest("E2E Movies 1080p", "movies", "WEB 1080p", "WEB 1080p", netflix.Id, UpgradeUntilCutoff: true, UpgradeUnknownItems: false),
            CancellationToken.None);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath, qualityProfileId: profile.Id);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var realtime = new RecordingRealtimeEventPublisher();
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, realtime, new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id,
            "movies",
            "movie",
            "movie-sintel",
            "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO",
            "TestIndexer",
            "client-1",
            "TestClient",
            "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);

        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, qualityRepository: quality, downloadDispatchesRepository: dispatches);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                // The dispatch's EntityId above ("movie-sintel") is not a
                // real catalogued movie, so nothing anchors this import
                // except the Title text. This test is about naming/
                // preference-evaluation, not identity resolution, so it
                // needs a confirmed id to clear the "no confirmed metadata
                // match" guard (#643).
                new ImportPreviewRequest(sourcePath, null, "movies", "Sintel", 2010, [], [], null, "en", ImdbId: "tt1727587"),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var tracked = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            tracked.Add(item);
        }

        var imported = Assert.Single(tracked);
        Assert.Equal("Sintel (2010) [WEB 1080p] [NF].mkv", Path.GetFileName(imported.FilePath));

        // And when the owner's naming says none of it, Deluno's own record
        // still does: this is what the title page reads.
        var snapshot = await mediaState.GetLatestPreferenceEvaluationSnapshotAsync(
            MediaKind.Movie,
            imported.MovieId,
            library.Id,
            fileIdentity: null,
            CancellationToken.None,
            imported.FilePath,
            imported.FileSizeBytes);
        Assert.NotNull(snapshot);
        Assert.Equal("Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", snapshot.ReleaseNameEvidence);
        Assert.Equal([netflix.Id], snapshot.MatchedRuleIds);
    }

    /// <summary>
    /// Where a copy came from must outlive the dispatch that brought it. A
    /// successful import archives its dispatch row at once, and every
    /// provenance query hides archived rows, so a title asking later used to
    /// be told nothing about its own file.
    ///
    /// The same import is also the title's history: it must be filed against
    /// the title, name the release that was taken, and record the rename as
    /// its own step.
    /// </summary>
    /// <summary>
    /// #617, live on the rig: The Kid (1921) was imported as a new film called
    /// "The Kid 1921 CC 1080p BluRay x264-nikt0" while the film grabbed stayed
    /// wanted. An import that knows its grab lands on the grab's film, named as
    /// that film, whatever title it was queued with.
    /// </summary>
    [Fact]
    public async Task An_import_lands_on_the_film_it_was_grabbed_for_whatever_it_was_queued_as()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        const string release = "The Kid 1921 CC 1080p BluRay x264-nikt0";
        var sourcePath = Path.Combine(downloadsPath, release + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var kid = await movies.AddAsync(new CreateMovieRequest("The Kid", 1921, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", kid.Id, "The Kid 1921 CC 1080p BluRay x264 nikt0", "LimeTorrents", "client-1", "qBittorrent", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies, downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                // As Transfers queued it: titled with the release's name.
                new ImportPreviewRequest(sourcePath, null, "movies", "the kid 1921 cc 1080p bluray x264 nikt0", 1921, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.StartsWith(Path.Combine(movieRootPath, "The Kid (1921)"), result.Response!.Preview.DestinationPath, StringComparison.Ordinal);
        var only = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Equal(kid.Id, only.Id);
        Assert.True(only.HasFile);
    }

    /// <summary>#757: unchecking "Replace illegal characters" removes them on the import file-naming path too, not just the render-preview one.</summary>
    [Fact]
    public async Task ExecuteAsync_removes_illegal_characters_instead_of_dashing_them_when_replacement_is_off()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Say.What.2020.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year})",
            replaceIllegalCharacters: false);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Say What? A Story", 2020, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Say What A Story (2020).mkv", Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    /// <summary>#759: the chosen colon mode reaches the import file-naming path, not just the render preview.</summary>
    [Fact]
    public async Task ExecuteAsync_applies_the_chosen_colon_replacement_mode_to_the_destination_file_name()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Series.Title.2020.1080p.WEB-DL.x264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year})",
            colonReplacementMode: ColonReplacementModes.SpaceDashSpace);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Series: Title", 2020, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Series -  Title (2020).mkv", Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    /// <summary>
    /// #760/#979: a MediaInfo token whose field the probe read wins over the
    /// release name -- CreateService's default probe says h264, the release
    /// name says x265, and the name lies. Audio channels still comes from the
    /// name, because this probe has no audio stream to read: the two tokens
    /// disagree with the name on purpose, one falls back and one does not, so
    /// this covers both directions in one file.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_renders_MediaInfo_tokens_from_the_probe_over_a_disagreeing_release_name()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        // The name claims x265 (HEVC); CreateService's default probe
        // (SuccessfulProbeService) reads the file itself as h264 (H.264).
        // They disagree deliberately.
        const string releaseName = "Silent.Hill.2020.2160p.WEB-DL.DTS-HD.5.1.x265-DELUNO";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year}) [{MediaInfo VideoCodec} {MediaInfo AudioChannels}] {Original Filename}");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Silent Hill", 2020, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        // Codec: the probe (H.264), not the name's claimed HEVC.
        // Channels: the name's 5.1, since this probe read no audio stream.
        Assert.Equal(
            $"Silent Hill (2020) [H.264 5.1] {releaseName}.mkv",
            Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    /// <summary>
    /// #979: with no usable probe (ffprobe is unavailable on this install),
    /// the MediaInfo tokens fall back to the release name exactly as they
    /// always did.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_falls_back_to_the_release_name_when_the_probe_has_nothing()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        const string releaseName = "Silent.Hill.2020.2160p.WEB-DL.DTS-HD.5.1.x265-DELUNO";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year}) [{MediaInfo VideoCodec} {MediaInfo AudioChannels}] {Original Filename}");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            movies,
            mediaProbeService: new UnavailableProbeService());

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Silent Hill", 2020, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            $"Silent Hill (2020) [HEVC 5.1] {releaseName}.mkv",
            Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    /// <summary>
    /// #760: {Original Title} is sourced from the catalogue the automatic
    /// import path resolved, via ImportPreviewRequest.OriginalTitle -- never
    /// re-derived from the release name, which usually carries only the
    /// library's own title.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_renders_the_original_title_token_from_the_request()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        const string releaseName = "Pans.Labyrinth.2006.1080p.BluRay.DTS.x264-GRP";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year}) - {Original Title}");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Pan's Labyrinth", 2006, [], [], null, null,
                    OriginalTitle: "El Laberinto del Fauno"),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            "Pan's Labyrinth (2006) - El Laberinto del Fauno.mkv",
            Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    /// <summary>#760: with no original title supplied, the token falls back to nothing rather than an error or the literal token.</summary>
    [Fact]
    public async Task ExecuteAsync_falls_back_to_empty_for_the_original_title_token_with_no_data()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        const string releaseName = "Arrival.2016.1080p.BluRay.DTS.x264-GRP";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            movieFileFormat: "{Movie Title} ({Release Year}) [{Original Title}]");
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Arrival", 2016, [], [], null, null),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Arrival (2016).mkv", Path.GetFileName(result.Response!.Preview.DestinationPath));
    }

    [Fact]
    public async Task Where_a_copy_came_from_outlives_the_dispatch_that_brought_it()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x33, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        var quality = new SqliteQualityRepository(storage.Factory, timeProvider);
        var netflixGuide = GuidePackageCatalog.Current.CustomFormats.Single(format =>
            format.Name.Equals("NF", StringComparison.Ordinal)
            && GuideFormatConditions.AppliesTo(format, "movies"));
        var netflix = await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(netflixGuide.Name, "movies", 0, netflixGuide.TrashId, GuideFormatConditions.For(netflixGuide), true),
            CancellationToken.None);
        var profile = await quality.CreateQualityProfileAsync(
            new CreateQualityProfileRequest("E2E Movies 1080p", "movies", "WEB 1080p", "WEB 1080p", netflix.Id, UpgradeUntilCutoff: true, UpgradeUnknownItems: false),
            CancellationToken.None);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath, qualityProfileId: profile.Id);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var movie = await movies.AddAsync(new CreateMovieRequest("Sintel", 2010, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id,
            "movies",
            "movie",
            movie.Id,
            "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO",
            "TestIndexer",
            "client-1",
            "qBittorrent",
            "sent",
            notesJson: null,
            cancellationToken: CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies, qualityRepository: quality, downloadDispatchesRepository: dispatches);
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Sintel", 2010, [], [], null, "en"),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        // What the poller does the moment an import succeeds. After it, no
        // dispatch query can see the row at all.
        await dispatches.ArchiveDispatchAsync(dispatchId, "import_completed", CancellationToken.None);
        var visible = await dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { MediaType = "movies", EntityId = movie.Id },
            new DispatchPaginationOptions { PageSize = 10 },
            CancellationToken.None);
        Assert.Empty(visible.Items);

        var tracked = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            tracked.Add(item);
        }

        var imported = Assert.Single(tracked);
        var snapshot = await mediaState.GetLatestPreferenceEvaluationSnapshotAsync(
            MediaKind.Movie,
            imported.MovieId,
            library.Id,
            fileIdentity: null,
            CancellationToken.None,
            imported.FilePath,
            imported.FileSizeBytes);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Acquisition);
        Assert.Equal("TestIndexer", snapshot.Acquisition.IndexerName);
        Assert.Equal("qBittorrent", snapshot.Acquisition.DownloadClientName);
        Assert.Equal(dispatchId, snapshot.Acquisition.DispatchId);
        Assert.NotNull(snapshot.Acquisition.GrabbedUtc);

        var history = await jobStore.ListActivityAsync(50, "movie", movie.Id, CancellationToken.None);
        var completed = Assert.Single(history, item => item.Category == "filesystem.import.completed");
        Assert.Contains("Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", completed.Message, StringComparison.Ordinal);
        var named = Assert.Single(history, item => item.Category == "filesystem.import.named");
        Assert.True(named.Message.Contains("Sintel (2010) [WEB 1080p] [NF].mkv", StringComparison.Ordinal), $"named message was: {named.Message}");
        Assert.Contains(history, item => item.Category == "filesystem.import.started");
    }

    [Fact]
    public async Task An_import_from_a_release_chosen_on_search_is_filed_under_the_film_it_matched_not_the_generated_id()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T05:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Sintel.2010.1080p.WEB-DL-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x33, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var mediaState = new SqliteMediaStateRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, mediaState);
        var movie = await movies.AddAsync(new CreateMovieRequest("Sintel", 2010, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        const string generatedId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "movies", "release", generatedId, "Sintel.2010.1080p.WEB-DL-DELUNO", "TestIndexer", "client-1", "qBittorrent", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);

        var service = CreateService(storage, timeProvider, platform, libraries, movies, downloadDispatchesRepository: dispatches);
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Sintel", 2010, [], [], null, "en"),
                "copy",
                Overwrite: false,
                AllowCopyFallback: true,
                DispatchId: dispatchId),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        var onTheFilm = await jobStore.ListActivityAsync(50, "movie", movie.Id, CancellationToken.None);
        Assert.Contains(onTheFilm, item => item.Category == "filesystem.import.completed");
        var onTheGuid = await jobStore.ListActivityAsync(50, "movie", generatedId, CancellationToken.None);
        Assert.Empty(onTheGuid);
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

    /// <summary>
    /// #1174: "The.Simpsons.S38E01...DDP5.1..." once also linked S01E01, because the stray
    /// "5.1" read as absolute episode 1. A name that says S38E01 is for S38E01 only.
    /// </summary>
    [Fact]
    public async Task A_season_and_episode_name_links_only_that_episode_even_when_another_has_the_absolute_number()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var sourcePath = Path.Combine(downloadsPath, "The.Simpsons.S38E01.The.Childrens.Book.Job.1080p.DSNP.WEB-DL.DDP5.1.H.264-STRiKES.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("The Simpsons", 1989, "tt0096697"), CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "Simpsons Roasting on an Open Fire", null, timeProvider.GetUtcNow(), AbsoluteNumber: 1),
                new CatalogueEpisodeItem(38, 1, "The Children's Book Job", null, timeProvider.GetUtcNow(), AbsoluteNumber: 808)
            ],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: Path.GetFileName(sourcePath), MediaType: "tv",
                    Title: "The Simpsons", Year: 1989, Genres: [], Tags: [],
                    Studio: null, OriginalLanguage: null, SeriesId: series.Id),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        var detail = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.True(detail!.Episodes.Single(e => e.SeasonNumber == 38 && e.EpisodeNumber == 1).HasFile);
        Assert.False(detail.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).HasFile);
    }

    /// <summary>#1174: the one-off repair keeps the episode the file's name gives, clears the other, and is safe to repeat.</summary>
    [Fact]
    public async Task The_repair_clears_the_wrong_link_keeps_the_named_episode_and_writes_activity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        var seriesRoot = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(seriesRoot);
        var library = await CreateTvLibraryAsync(libraries, seriesRoot, Path.Combine(storage.DataRoot, "downloads"));

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var filePath = Path.Combine(seriesRoot, "The Simpsons (1989)", "The Simpsons - S38E01 - The Children's Book Job [WEB 1080p].mkv");
        await seriesRepository.ImportExistingAsync(
            library.Id, "The Simpsons", 1989, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            null, null,
            [
                new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: filePath, FileSizeBytes: 4096),
                new ImportedEpisodeItem(38, 1, HasFile: true, FilePath: filePath, FileSizeBytes: 4096)
            ],
            CancellationToken.None);

        var activity = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var cleared = await Deluno.Worker.Services.EpisodeFileLinkRepairService.RunAsync(seriesRepository, activity, CancellationToken.None);
        Assert.Equal(1, cleared);

        var seriesId = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None)).Id;
        var detail = await seriesRepository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        Assert.False(detail!.Episodes.Single(e => e.SeasonNumber == 1).HasFile);
        Assert.True(detail.Episodes.Single(e => e.SeasonNumber == 38).HasFile);
        var events = await activity.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(events, e => e.Category == Deluno.Worker.Services.EpisodeFileLinkRepairService.ActivityCategory
            && e.Message.Contains("S01E01") && e.Message.Contains("S38E01"));

        Assert.Equal(0, await Deluno.Worker.Services.EpisodeFileLinkRepairService.RunAsync(seriesRepository, activity, CancellationToken.None));
    }

    /// <summary>
    /// #1173: sets up The Simpsons with S38E01 "The Children's Book Job" and S38E02 "Diary of a Chimpy Kid", S38E02
    /// holding a file that came from <paramref name="releaseName"/>, and runs the repair.
    /// </summary>
    private async Task<(int Repaired, TestStorage Storage, SqliteSeriesCatalogRepository Series, SqliteJobStore Store, FakeJobScheduler Scheduler, string FilePath, string SeriesId, Deluno.Platform.RecycleBinService Bin, Func<Task<int>> Again)> RunMislabelledRepairAsync(
        string releaseName,
        string libraryFileName,
        string? earlierReleaseName = null,
        TimeSpan? earlierBy = null,
        string? packReleaseName = null,
        long? latestImportBytes = null)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        var seriesRoot = Path.Combine(storage.DataRoot, "series");
        var folder = Directory.CreateDirectory(Path.Combine(seriesRoot, "The Simpsons (1989)", "Season 38")).FullName;
        var library = await CreateTvLibraryAsync(libraries, seriesRoot, Path.Combine(storage.DataRoot, "downloads"));
        var filePath = Path.Combine(folder, libraryFileName);
        var firstPath = Path.Combine(folder, "The Simpsons - S38E01 - The Children's Book Job.mkv");
        await File.WriteAllBytesAsync(filePath, new byte[64]);
        await File.WriteAllBytesAsync(firstPath, new byte[32]);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        await seriesRepository.ImportExistingAsync(
            library.Id, "The Simpsons", 1989, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            null, null,
            [
                new ImportedEpisodeItem(38, 1, HasFile: true, FilePath: firstPath, FileSizeBytes: 32),
                new ImportedEpisodeItem(38, 2, HasFile: true, FilePath: filePath, FileSizeBytes: 64)
            ],
            CancellationToken.None);
        var seriesId = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None)).Id;
        await seriesRepository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(38, 1, "The Children's Book Job", null, timeProvider.GetUtcNow()),
                new CatalogueEpisodeItem(38, 2, "Diary of a Chimpy Kid", null, timeProvider.GetUtcNow())
            ],
            "provider",
            CancellationToken.None);
        var episodeId = (await seriesRepository.GetInventoryDetailAsync(seriesId, CancellationToken.None))!
            .Episodes.Single(e => e.EpisodeNumber == 2).EpisodeId;

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var store = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
        if (earlierReleaseName is not null)
        {
            // An older import to the same path, which the next one replaced (#1189).
            var earlierId = await store.RecordDownloadDispatchAsync(
                library.Id, "tv", "episode", episodeId, earlierReleaseName, "TestIndexer", "client-1", "qBittorrent", "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordImportOutcomeAsync(earlierId, "imported", filePath, null, null, CancellationToken.None);
            timeProvider.Advance(earlierBy ?? TimeSpan.FromHours(1));
        }

        var dispatchId = await store.RecordDownloadDispatchAsync(
            library.Id, "tv", "episode", episodeId, releaseName, "TestIndexer", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordImportOutcomeAsync(
            dispatchId, "imported", filePath, null, null, CancellationToken.None, importedFileBytes: latestImportBytes);

        if (packReleaseName is not null)
        {
            // #1189: a season-pack grab is recorded under the series, and its import path is the folder it was placed in.
            timeProvider.Advance(TimeSpan.FromHours(1));
            var packId = await store.RecordDownloadDispatchAsync(
                library.Id, "tv", "series", seriesId, packReleaseName, "TestIndexer", "client-1", "qBittorrent", "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordImportOutcomeAsync(packId, "imported", folder, null, null, CancellationToken.None);
        }

        var bin = new Deluno.Platform.RecycleBinService(
            Microsoft.Extensions.Options.Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
            timeProvider,
            NullLogger<Deluno.Platform.RecycleBinService>.Instance);
        var scheduler = new FakeJobScheduler();
        Task<int> Run() => Deluno.Worker.Services.MislabelledEpisodeFileRepairService.RunAsync(
            seriesRepository, libraries, bin, store, scheduler, store, CancellationToken.None);
        return (await Run(), storage, seriesRepository, store, scheduler, filePath, seriesId, bin, Run);
    }

    /// <summary>#1173: S38E02 holds a copy of S38E01; it is binned (Restore works), Missing, searched, and said in Activity.</summary>
    [Fact]
    public async Task A_file_whose_release_named_another_episodes_title_goes_to_the_recycle_bin_and_the_episode_is_searched()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        Assert.Equal(1, run.Repaired);
        Assert.False(File.Exists(run.FilePath));
        var binned = Assert.Single(await run.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(run.FilePath, binned.OriginalPath);
        Assert.True(File.Exists(binned.RecyclePath));

        var detail = await run.Series.GetInventoryDetailAsync(run.SeriesId, CancellationToken.None);
        var second = detail!.Episodes.Single(e => e.EpisodeNumber == 2);
        Assert.False(second.HasFile);
        Assert.Equal("missing", second.WantedStatus);
        Assert.True(detail.Episodes.Single(e => e.EpisodeNumber == 1).HasFile);
        Assert.Null(await LinkedFileAsync(storage, run.SeriesId, 2));

        var events = await run.Store.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(events, e => e.Category == Deluno.Worker.Services.MislabelledEpisodeFileRepairService.ActivityCategory
            && e.Message.Contains("The Simpsons S38E02 held a copy of S38E01")
            && e.Message.Contains("The Children's Book Job")
            && e.Message.Contains("recycle bin"));
        var search = Assert.Single(run.Scheduler.Requests);
        Assert.Contains(second.EpisodeId, search.PayloadJson);

        Assert.Equal(0, await run.Again());
        Assert.Single(run.Scheduler.Requests);
        Assert.Single(await run.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// #1189 (rig, 8 Oct): two downloads were imported to one path. The older release's name carries another episode's
    /// title; the newer one is right and its file is the one in place. Only the latest import is judged.
    /// </summary>
    [Fact]
    public async Task A_file_is_judged_by_the_latest_import_to_its_path_not_an_older_one_it_replaced()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 Diary of a Chimpy Kid 1080p DSNP WEB-DL DDP5 1 H 264-NTb",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            earlierReleaseName: "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX");
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        Assert.Empty(run.Scheduler.Requests);
        Assert.Empty(await run.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>#1189: the other way round, the newest import is the wrong one, so the file still goes.</summary>
    [Fact]
    public async Task A_file_whose_latest_import_is_the_wrong_episode_still_goes_to_the_recycle_bin()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            earlierReleaseName: "The Simpsons S38E02 Diary of a Chimpy Kid 1080p DSNP WEB-DL DDP5 1 H 264-NTb");
        using var storage = run.Storage;
        Assert.Equal(1, run.Repaired);
        Assert.False(File.Exists(run.FilePath));
    }

    /// <summary>
    /// #1189: an older, wrong-named per-episode import was replaced by a newer season-pack import, recorded under the
    /// series with the pack's folder as its path. The pack decides, so the correct file is kept.
    /// </summary>
    [Fact]
    public async Task An_episode_import_replaced_by_a_newer_season_pack_import_does_not_decide_and_the_file_is_kept()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            packReleaseName: "The Simpsons S38 1080p DSNP WEB-DL DDP5 1 H 264-NTb");
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        Assert.Empty(run.Scheduler.Requests);
        Assert.Empty(await run.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>#1189: a pack import is judged too. Its own name carries another episode's title, so the file goes to the bin.</summary>
    [Fact]
    public async Task A_season_pack_import_whose_own_name_is_wrong_for_the_episode_is_still_judged()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 Diary of a Chimpy Kid 1080p DSNP WEB-DL DDP5 1 H 264-NTb",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            packReleaseName: "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX");
        using var storage = run.Storage;
        Assert.Equal(1, run.Repaired);
        Assert.False(File.Exists(run.FilePath));
        Assert.Single(await run.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>#1189: the latest import recorded another size than the file in place; it cannot say what the file is, so it is kept and Activity says so.</summary>
    [Fact]
    public async Task A_file_that_is_not_the_size_its_latest_import_recorded_is_left_alone_and_Activity_says_so()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            latestImportBytes: 999);
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        Assert.Empty(await run.Bin.ListAsync(CancellationToken.None));
        var events = await run.Store.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(events, e => e.Category == Deluno.Worker.Services.MislabelledEpisodeFileRepairService.ActivityCategory
            && e.Message.Contains("cannot tell what it is") && e.Message.Contains("left it where it is"));
    }

    /// <summary>#1189: when the recorded size does match the file, the wrong-named import is still judged.</summary>
    [Fact]
    public async Task A_file_of_the_size_its_import_recorded_is_still_judged_by_that_import()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            latestImportBytes: 64);
        using var storage = run.Storage;
        Assert.Equal(1, run.Repaired);
        Assert.False(File.Exists(run.FilePath));
    }

    /// <summary>#1189: two imports at the same instant naming different releases cannot be told apart; the file is left and Activity says so.</summary>
    [Fact]
    public async Task A_file_two_imports_cannot_be_told_apart_on_is_left_alone_and_Activity_says_so()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv",
            earlierReleaseName: "The Simpsons S38E02 Diary of a Chimpy Kid 1080p DSNP WEB-DL DDP5 1 H 264-NTb",
            earlierBy: TimeSpan.Zero);
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        var events = await run.Store.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(events, e => e.Category == Deluno.Worker.Services.MislabelledEpisodeFileRepairService.ActivityCategory
            && e.Message.Contains("cannot tell which"));
    }

    /// <summary>#1173: a file whose release carries the episode's own title is left alone.</summary>
    [Fact]
    public async Task A_file_whose_release_carries_its_own_episode_title_is_left_alone()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-ETHEL",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        Assert.Empty(run.Scheduler.Requests);
        Assert.Empty(await run.Bin.ListAsync(CancellationToken.None));
    }

    /// <summary>#1173: a file whose release names no episode title at all is left alone.</summary>
    [Fact]
    public async Task A_file_whose_release_has_no_episode_title_is_left_alone()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 1080p WEB H264-GRP",
            "The Simpsons - S38E02.mkv");
        using var storage = run.Storage;
        Assert.Equal(0, run.Repaired);
        Assert.True(File.Exists(run.FilePath));
        Assert.Empty(run.Scheduler.Requests);
        Assert.Empty(await run.Bin.ListAsync(CancellationToken.None));
    }

    private static async Task<object?> ScalarAsync(TestStorage storage, string sql, string seriesId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = seriesId; command.Parameters.Add(id);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<bool> ShowIsMonitoredAsync(TestStorage storage, string seriesId)
        => Convert.ToInt64(await ScalarAsync(storage, "SELECT monitored FROM series_entries WHERE id = @id;", seriesId)) == 1;

    private static ILibraryFileCheckService CreateLibraryFileCheck(
        TestStorage storage,
        SqliteSeriesCatalogRepository series,
        SqliteJobStore store,
        Deluno.Platform.RecycleBinService bin)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        var reconciliation = new FilesystemReconciliationService(
            CreateLibrariesRepository(storage, timeProvider),
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            series,
            store,
            timeProvider,
            null,
            bin);
        return new LibraryFileCheckService(reconciliation, store, NullLogger<LibraryFileCheckService>.Instance, series);
    }

    /// <summary>
    /// The Simpsons, 8 Oct: the mislabelled repair binned S38E02's file and the library check then read the show-level
    /// note of that file as "removed outside Deluno" and unmonitored the whole show.
    /// </summary>
    [Fact]
    public async Task After_the_mislabelled_repair_a_library_check_leaves_the_show_monitored()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        Assert.True(await ShowIsMonitoredAsync(storage, run.SeriesId));

        // The older repair left the show-level note naming the file it binned.
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE series_wanted_state SET has_file = 1, file_path = @path WHERE series_id = @id;";
            var path = stale.CreateParameter(); path.ParameterName = "@path"; path.Value = run.FilePath; stale.Parameters.Add(path);
            var id = stale.CreateParameter(); id.ParameterName = "@id"; id.Value = run.SeriesId; stale.Parameters.Add(id);
            await stale.ExecuteNonQueryAsync();
        }

        var result = await CreateLibraryFileCheck(storage, run.Series, run.Store, run.Bin).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.MarkedMissing);
        Assert.True(await ShowIsMonitoredAsync(storage, run.SeriesId));
    }

    /// <summary>A show-level note that points into the recycle bin is corrected at start-up and the show stays monitored.</summary>
    [Fact]
    public async Task A_show_level_note_pointing_at_a_recycled_file_is_cleaned_and_the_show_stays_monitored()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        var binned = Assert.Single(await run.Bin.ListAsync(CancellationToken.None));
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE series_wanted_state SET has_file = 1, file_path = @path WHERE series_id = @id;";
            var path = stale.CreateParameter(); path.ParameterName = "@path"; path.Value = binned.RecyclePath; stale.Parameters.Add(path);
            var id = stale.CreateParameter(); id.ParameterName = "@id"; id.Value = run.SeriesId; stale.Parameters.Add(id);
            await stale.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await Deluno.Worker.Services.MislabelledEpisodeFileRepairService.HealShowFileNotesAsync(
            run.Series, run.Bin, CancellationToken.None));

        var note = (string?)await ScalarAsync(storage, "SELECT file_path FROM series_wanted_state WHERE series_id = @id;", run.SeriesId);
        Assert.DoesNotContain("recycle-bin", note ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(run.FilePath, note);
        Assert.True(await ShowIsMonitoredAsync(storage, run.SeriesId));
        Assert.Equal(0, await Deluno.Worker.Services.MislabelledEpisodeFileRepairService.HealShowFileNotesAsync(
            run.Series, run.Bin, CancellationToken.None));
    }

    /// <summary>
    /// The Simpsons on rc.30.8: an older library check had set the show-level note to has_file = 0 and kept its path, size
    /// and the reason "stopped wanting this show" on a show that is monitored. The start-up heal clears the note whatever
    /// has_file says and works the reason out again.
    /// </summary>
    [Fact]
    public async Task A_stale_show_level_note_with_no_file_flag_is_cleared_and_its_reason_recomputed()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE series_wanted_state SET has_file = 0, file_path = @path, file_size_bytes = 657145450, " +
                "wanted_reason = 'The file is gone from disk, so Deluno stopped wanting this show.' WHERE series_id = @id;";
            var path = stale.CreateParameter(); path.ParameterName = "@path"; path.Value = run.FilePath; stale.Parameters.Add(path);
            var id = stale.CreateParameter(); id.ParameterName = "@id"; id.Value = run.SeriesId; stale.Parameters.Add(id);
            await stale.ExecuteNonQueryAsync();
        }

        Assert.True(await Deluno.Worker.Services.MislabelledEpisodeFileRepairService.HealShowFileNotesAsync(
            run.Series, run.Bin, CancellationToken.None) >= 1);

        var note = (string?)await ScalarAsync(storage, "SELECT file_path FROM series_wanted_state WHERE series_id = @id;", run.SeriesId);
        Assert.NotEqual(run.FilePath, note);
        var reason = (string?)await ScalarAsync(storage, "SELECT wanted_reason FROM series_wanted_state WHERE series_id = @id;", run.SeriesId);
        Assert.DoesNotContain("stopped wanting", reason ?? string.Empty, StringComparison.Ordinal);
        Assert.True(await ShowIsMonitoredAsync(storage, run.SeriesId));
        Assert.Equal(0, await Deluno.Worker.Services.MislabelledEpisodeFileRepairService.HealShowFileNotesAsync(
            run.Series, run.Bin, CancellationToken.None));
    }

    /// <summary>A monitored show that carries the old "stopped wanting" reason, with no stale path at all, gets its reason worked out again.</summary>
    [Fact]
    public async Task A_monitored_show_never_keeps_the_reason_stopped_wanting_this_show()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE series_wanted_state SET file_path = NULL, file_size_bytes = NULL, has_file = 0, " +
                "wanted_reason = 'The file is gone from disk, so Deluno stopped wanting this show.' WHERE series_id = @id;";
            var id = stale.CreateParameter(); id.ParameterName = "@id"; id.Value = run.SeriesId; stale.Parameters.Add(id);
            await stale.ExecuteNonQueryAsync();
        }

        await Deluno.Worker.Services.MislabelledEpisodeFileRepairService.HealShowFileNotesAsync(run.Series, run.Bin, CancellationToken.None);

        var reason = (string?)await ScalarAsync(storage, "SELECT wanted_reason FROM series_wanted_state WHERE series_id = @id;", run.SeriesId);
        Assert.DoesNotContain("stopped wanting", reason ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>James, 30 Sep: a file deleted outside Deluno unmonitors that title; for TV that is the episode, never the show.</summary>
    [Fact]
    public async Task An_episode_file_deleted_outside_deluno_unmonitors_that_episode_only()
    {
        var run = await RunMislabelledRepairAsync(
            "The Simpsons S38E02 Diary of a Chimpy Kid 1080p FOX WEB H264-ETHEL",
            "The Simpsons - S38E02 - Diary of a Chimpy Kid.mkv");
        using var storage = run.Storage;
        File.Delete(run.FilePath);

        var result = await CreateLibraryFileCheck(storage, run.Series, run.Store, run.Bin).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.MarkedMissing);
        Assert.True(await ShowIsMonitoredAsync(storage, run.SeriesId));
        var detail = await run.Series.GetInventoryDetailAsync(run.SeriesId, CancellationToken.None);
        Assert.False(detail!.Episodes.Single(e => e.EpisodeNumber == 2).Monitored);
        Assert.True(detail.Episodes.Single(e => e.EpisodeNumber == 1).Monitored);
    }

    private static async Task<string?> LinkedFileAsync(TestStorage storage, string seriesId, int episodeNumber)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM episode_entries WHERE series_id = @id AND episode_number = @episode;";
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = seriesId; command.Parameters.Add(id);
        var episode = command.CreateParameter(); episode.ParameterName = "@episode"; episode.Value = episodeNumber; command.Parameters.Add(episode);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    /// <summary>#1174: the cleared episode's own file, sitting in the show's folder, is linked instead of leaving it Missing.</summary>
    [Fact]
    public async Task The_repair_relinks_the_cleared_episodes_own_file_from_the_shows_folder_and_says_so()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        var seriesRoot = Path.Combine(storage.DataRoot, "series");
        var season = Directory.CreateDirectory(Path.Combine(seriesRoot, "Chernobyl", "Season 1")).FullName;
        var library = await CreateTvLibraryAsync(libraries, seriesRoot, Path.Combine(storage.DataRoot, "downloads"));
        var fifth = Path.Combine(season, "Chernobyl.S01E05.Vichnaya.Pamyat.1080p.WEB-DL-GRP.mkv");
        var first = Path.Combine(season, "Chernobyl.S01E01.1.23.45.1080p.WEB-DL-GRP.mkv");
        await File.WriteAllBytesAsync(fifth, new byte[16]);
        await File.WriteAllBytesAsync(first, new byte[32]);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        await seriesRepository.ImportExistingAsync(
            library.Id, "Chernobyl", 2019, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            null, null,
            [
                new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: fifth, FileSizeBytes: 16),
                new ImportedEpisodeItem(1, 5, HasFile: true, FilePath: fifth, FileSizeBytes: 16)
            ],
            CancellationToken.None);

        var activity = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        Assert.Equal(1, await Deluno.Worker.Services.EpisodeFileLinkRepairService.RunAsync(seriesRepository, activity, CancellationToken.None));

        var seriesId = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None)).Id;
        var detail = await seriesRepository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        var one = detail!.Episodes.Single(e => e.EpisodeNumber == 1);
        Assert.True(one.HasFile);
        Assert.Equal(first, await LinkedFileAsync(storage, seriesId, 1));
        Assert.NotEqual("missing", one.WantedStatus);
        Assert.Equal(fifth, await LinkedFileAsync(storage, seriesId, 5));
        var events = await activity.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(events, e => e.Category == Deluno.Worker.Services.EpisodeFileLinkRepairService.ActivityCategory
            && e.Message.Contains("S01E01") && e.Message.Contains("now shows its own file") && e.Message.Contains("Chernobyl.S01E01"));

        Assert.Equal(0, await Deluno.Worker.Services.EpisodeFileLinkRepairService.RunAsync(seriesRepository, activity, CancellationToken.None));
    }

    /// <summary>
    /// #1174: an episode whose link the earlier repair cleared, with its own correctly named file still in the show's
    /// folder, is linked again by a library check, and by reviewing the existing files; a file named for another
    /// episode is never taken for it.
    /// </summary>
    [Theory]
    [InlineData("check")]
    [InlineData("review")]
    public async Task An_episode_that_shows_no_file_gets_its_own_file_from_the_shows_folder(string how)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        var seriesRoot = Path.Combine(storage.DataRoot, "series");
        var folder = Directory.CreateDirectory(Path.Combine(seriesRoot, "The Twilight Zone (1959)")).FullName;
        var library = await CreateTvLibraryAsync(libraries, seriesRoot, Path.Combine(storage.DataRoot, "downloads"));
        var first = Path.Combine(folder, "The Twilight Zone - S01E01 - Where Is Everybody [WEB 1080p].mkv");
        var second = Path.Combine(folder, "The Twilight Zone - S01E02 - One for the Angels [WEB 1080p].mkv");
        var stray = Path.Combine(folder, "The Twilight Zone - S01E09 - Mr. Denton on Doomsday [WEB 1080p].mkv");
        await File.WriteAllBytesAsync(first, new byte[16]);
        await File.WriteAllBytesAsync(second, new byte[24]);
        await File.WriteAllBytesAsync(stray, new byte[8]);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        await seriesRepository.ImportExistingAsync(
            library.Id, "The Twilight Zone", 1959, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            null, null,
            [
                new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: first, FileSizeBytes: 16),
                new ImportedEpisodeItem(1, 2, HasFile: false, FilePath: null, FileSizeBytes: null),
                new ImportedEpisodeItem(1, 3, HasFile: false, FilePath: null, FileSizeBytes: null)
            ],
            CancellationToken.None);
        var seriesId = Assert.Single(await seriesRepository.ListAsync(CancellationToken.None)).Id;
        Assert.Null(await LinkedFileAsync(storage, seriesId, 2));

        if (how == "check")
        {
            var relinked = Assert.Single(await seriesRepository.RelinkEpisodesWithTheirOwnFilesAsync(CancellationToken.None));
            Assert.Equal(2, relinked.EpisodeNumber);
            Assert.Empty(await seriesRepository.RelinkEpisodesWithTheirOwnFilesAsync(CancellationToken.None));
        }
        else
        {
            await seriesRepository.ImportExistingAsync(
                library.Id, "The Twilight Zone", 1959, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
                null, null,
                [
                    new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: first, FileSizeBytes: 16),
                    new ImportedEpisodeItem(1, 2, HasFile: true, FilePath: second, FileSizeBytes: 24)
                ],
                CancellationToken.None);
        }

        var detail = await seriesRepository.GetInventoryDetailAsync(seriesId, CancellationToken.None);
        var two = detail!.Episodes.Single(e => e.EpisodeNumber == 2);
        Assert.True(two.HasFile);
        Assert.NotEqual("missing", two.WantedStatus);
        Assert.Equal(second, await LinkedFileAsync(storage, seriesId, 2));
        Assert.False(detail.Episodes.Single(e => e.EpisodeNumber == 3).HasFile);
    }

    /// <summary>#761: a dated show is named with DailyEpisodeFileFormat, not the standard EpisodeFileFormat.</summary>
    [Fact]
    public async Task Preview_names_a_dated_series_episode_with_the_daily_format()
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
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            dailyEpisodeFileFormat: "{Series Title} - {Air-Date} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Daily Show", 2026, "tt1234567", SeriesType: SeriesTypes.Daily, NumberingScheme: SeriesNumberingSchemes.AirDate),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "The Episode", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
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
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Daily Show (2026)", "Daily Show - 2026-04-29 - The Episode.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#761: an absolute-numbered show is named with AnimeEpisodeFileFormat, not the standard EpisodeFileFormat.</summary>
    [Fact]
    public async Task Preview_names_an_absolute_numbered_series_episode_with_the_anime_format()
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
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            animeEpisodeFileFormat: "{Series Title} - {absolute:000} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Anime Show", 2026, "tt1234567", SeriesType: SeriesTypes.Anime, NumberingScheme: SeriesNumberingSchemes.Absolute),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "The Beginning", null, timeProvider.GetUtcNow(), AbsoluteNumber: 101)],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
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
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Anime Show (2026)", "Anime Show - 101 - The Beginning.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#761: a standard-numbered show keeps EpisodeFileFormat even when Daily/Anime formats differ from it.</summary>
    [Fact]
    public async Task Preview_names_a_standard_series_episode_with_the_standard_format()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S01E03.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            dailyEpisodeFileFormat: "{Series Title} - {Air-Date} - {Episode Title}",
            animeEpisodeFileFormat: "{Series Title} - {absolute:000} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "Good News About Hell", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Severance - S01E03 - Good News About Hell.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#762: a normal season lands in its own SeasonFolderFormat subfolder under the series folder.</summary>
    [Fact]
    public async Task Preview_places_a_normal_season_episode_in_its_season_subfolder()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S01E03.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            seasonFolderFormat: "Season {Season:00}",
            specialsFolderFormat: "Specials");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "Good News About Hell", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Season 01", "Severance - S01E03 - Good News About Hell.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #1171: a manual import has no download behind it, so the clients are
    /// asked by path. A file one of them is seeding is copied and the original
    /// is left exactly where the client expects it.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_manual_import_keeps_a_source_file_a_download_client_still_holds(bool clientHoldsFile, bool expectSourceKept)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
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
        var service = CreateService(storage, timeProvider, platform, libraries, movies, clientHeldFiles: new FakeClientHeldFileProbe(clientHoldsFile));

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: true, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.True(result.Response?.Executed);
        Assert.Equal(expectSourceKept, File.Exists(sourcePath));
    }

    /// <summary>
    /// #1171: "Show.S01E05.Title..." in a release-named folder, for a show
    /// already in the library, lands in that show's own folder with the
    /// episode's real title, never in a folder made from the release name.
    /// </summary>
    [Fact]
    public async Task A_manual_tv_import_is_filed_under_the_library_show_with_its_episode_title()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        var releaseFolder = Path.Combine(downloadsPath, "Chernobyl.S01E05.Vichnaya.Pamyat.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb");
        Directory.CreateDirectory(releaseFolder);
        var sourcePath = Path.Combine(releaseFolder, "Chernobyl.S01E05.Vichnaya.Pamyat.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("Chernobyl", 2019, "tt7366338"), CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 5, "Vichnaya Pamyat", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        ImportPreviewRequest Request(string? title = null, int? year = null) => new(
            SourcePath: releaseFolder, FileName: null, MediaType: "tv",
            Title: title, Year: year, Genres: null, Tags: null,
            Studio: null, OriginalLanguage: null);

        // No hints at all: the name alone finds the show.
        var preview = await service.PreviewAsync(Request(), CancellationToken.None);
        Assert.EndsWith(
            Path.Combine("Chernobyl (2019)", "Chernobyl - S01E05 - Vichnaya Pamyat.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Chernobyl (2019)", preview.MatchedTitle);
        Assert.False(preview.NeedsTitle);

        // The person's title and year decide the match.
        var hinted = await service.PreviewAsync(Request("Chernobyl", 2019), CancellationToken.None);
        Assert.Equal(preview.DestinationPath, hinted.DestinationPath);

        // Importing it files it there too.
        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(Preview: Request(), TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(result.Response?.Executed);
        Assert.True(File.Exists(Path.Combine(seriesRootPath, "Chernobyl (2019)", "Chernobyl - S01E05 - Vichnaya Pamyat.mkv")));
        Assert.False(Directory.Exists(Path.Combine(seriesRootPath, Path.GetFileName(releaseFolder))));
    }

    /// <summary>#1171: a TV file that matches nothing and has no title typed asks for one rather than inventing a folder.</summary>
    [Fact]
    public async Task A_manual_tv_import_that_matches_nothing_asks_for_the_title()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(seriesRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Unknown.Show.S01E01.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath, FileName: null, MediaType: "tv",
                Title: null, Year: null, Genres: null, Tags: null,
                Studio: null, OriginalLanguage: null),
            CancellationToken.None);

        Assert.True(preview.NeedsTitle);
        Assert.Null(preview.MatchedTitle);
    }

    /// <summary>#762: season 0 (a special) uses SpecialsFolderFormat instead of SeasonFolderFormat.</summary>
    [Fact]
    public async Task Preview_places_a_season_zero_episode_in_the_specials_subfolder()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S00E01.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            seasonFolderFormat: "Season {Season:00}",
            specialsFolderFormat: "Specials");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(0, 1, "Deleted Scene", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Specials", "Severance - S00E01 - Deleted Scene.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#762: blank SeasonFolderFormat/SpecialsFolderFormat keeps today's flat series folder.</summary>
    [Fact]
    public async Task Preview_keeps_a_flat_series_folder_when_season_folder_format_is_blank()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S01E03.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567"),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "Good News About Hell", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Severance - S01E03 - Good News About Hell.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Season", preview.DestinationPath);
    }

    /// <summary>#850: a series' own SeasonFolder override forces a subfolder even when the global format is blank.</summary>
    [Fact]
    public async Task Preview_forces_a_season_subfolder_when_the_series_overrides_a_blank_global_format()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S01E03.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567", SeasonFolder: true),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "Good News About Hell", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Season 01", "Severance - S01E03 - Good News About Hell.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#850: a series' own SeasonFolder override flattens the folder even when the global format is set.</summary>
    [Fact]
    public async Task Preview_keeps_a_flat_series_folder_when_the_series_overrides_a_configured_global_format()
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
        var sourcePath = Path.Combine(downloadsPath, "Severance.S01E03.1080p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            seasonFolderFormat: "Season {Season:00}",
            specialsFolderFormat: "Specials");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Severance", 2026, "tt1234567", SeasonFolder: false),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 3, "Good News About Hell", null, timeProvider.GetUtcNow())],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
            new ImportPreviewRequest(
                SourcePath: sourcePath,
                FileName: Path.GetFileName(sourcePath),
                MediaType: "tv",
                Title: "Severance",
                Year: 2026,
                Genres: [],
                Tags: [],
                Studio: null,
                OriginalLanguage: null,
                SeriesId: series.Id),
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("Severance (2026)", "Severance - S01E03 - Good News About Hell.mkv"),
            preview.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Season", preview.DestinationPath);
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
        // #763: a multi-episode file now gets a templated name too (Range,
        // the default style), instead of keeping the incoming filename as-is.
        Assert.Equal("Example Show - S01E02-E03 - Two + Three.mkv", Path.GetFileName(preview.DestinationPath));

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

    /// <summary>#763: MultiEpisodeStyle is read from settings all the way through the import path, checked against a 2- and a 3-episode file for every style.</summary>
    [Theory]
    [InlineData(MultiEpisodeStyles.Extend, "S01E02E03", 2)]
    [InlineData(MultiEpisodeStyles.Duplicate, "S01E02S01E03", 2)]
    [InlineData(MultiEpisodeStyles.Repeat, "S01E02-S01E03", 2)]
    [InlineData(MultiEpisodeStyles.Scene, "S01E02-E03", 2)]
    [InlineData(MultiEpisodeStyles.Range, "S01E02-E03", 2)]
    [InlineData(MultiEpisodeStyles.PrefixedRange, "S01E02-03", 2)]
    [InlineData(MultiEpisodeStyles.Extend, "S01E02E03E04", 3)]
    [InlineData(MultiEpisodeStyles.Duplicate, "S01E02S01E03S01E04", 3)]
    [InlineData(MultiEpisodeStyles.Repeat, "S01E02-S01E03-S01E04", 3)]
    [InlineData(MultiEpisodeStyles.Scene, "S01E02-E03-E04", 3)]
    [InlineData(MultiEpisodeStyles.Range, "S01E02-E04", 3)]
    [InlineData(MultiEpisodeStyles.PrefixedRange, "S01E02-04", 3)]
    public async Task Preview_names_a_multi_episode_file_per_the_selected_style(string style, string expectedToken, int episodeCount)
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
        var episodeToken = episodeCount == 2 ? "E02E03" : "E02E03E04";
        var sourcePath = Path.Combine(downloadsPath, $"Example.Show.S01{episodeToken}.2160p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath, multiEpisodeStyle: style);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var episodes = new List<CatalogueEpisodeItem>
        {
            new(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-3)),
            new(1, 3, "Three", null, timeProvider.GetUtcNow().AddDays(-2))
        };
        var expectedTitle = "Two + Three";
        if (episodeCount == 3)
        {
            episodes.Add(new CatalogueEpisodeItem(1, 4, "Four", null, timeProvider.GetUtcNow().AddDays(-1)));
            expectedTitle = "Two + Three + Four";
        }

        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("Example Show", 2026, "tt7654321"), CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(series.Id, episodes, "provider", CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
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
            CancellationToken.None);

        Assert.Equal($"Example Show - {expectedToken} - {expectedTitle}.mkv", Path.GetFileName(preview.DestinationPath));
    }

    /// <summary>
    /// #763: two scene-numbered episodes in one file that map, through the
    /// catalogue's own scene mapping, to two different canonical seasons
    /// (a real XEM-style shape for a show whose scene numbering runs across
    /// a season boundary) are left unmatched rather than guessed at.
    /// </summary>
    [Fact]
    public async Task Preview_keeps_the_incoming_name_when_a_multi_episode_files_episodes_resolve_to_different_seasons()
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
        var sourcePath = Path.Combine(downloadsPath, "Example.Show.S01E09E10.2160p.WEB-DL.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Example Show", 2026, "tt7654321", NumberingScheme: SeriesNumberingSchemes.Scene),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 9, "Season One Finale", null, timeProvider.GetUtcNow().AddDays(-2), SceneSeasonNumber: 1, SceneEpisodeNumber: 9),
                new CatalogueEpisodeItem(2, 1, "Season Two Premiere", null, timeProvider.GetUtcNow().AddDays(-1), SceneSeasonNumber: 1, SceneEpisodeNumber: 10)
            ],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);
        var preview = await service.PreviewAsync(
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
                NumberingScheme: SeriesNumberingSchemes.Scene),
            CancellationToken.None);

        Assert.Equal(Path.GetFileName(sourcePath), Path.GetFileName(preview.DestinationPath));
        Assert.Contains(preview.Warnings, warning => warning.Contains("more than one season", StringComparison.OrdinalIgnoreCase));
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
        // #892: a copy import's own source is checked, then deleted, once the
        // pack is committed - the same rule a single file already followed.
        Assert.Empty(Directory.GetFiles(packDirectory, "*.mkv"));

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

        // #892: a copy season-pack's own source is checked (it was, above -
        // every placed file's size was verified against the catalogued
        // source) and cleaned up once the pack is committed, the same rule a
        // single file already followed. The trade-off that comes with it: the
        // pack directory this request pointed at is now empty, so there is
        // nothing left there for a later preview to re-derive an
        // "already-committed" answer from by rescanning it - a single-file
        // copy import already worked this way (a retry against a cleaned-up
        // source gets an honest "not found", not a silent no-op). What still
        // holds is the one thing that actually matters: the library's own
        // record of the import.
        var retryPreview = await service.PreviewAsync(directoryRequest.Preview, CancellationToken.None);
        Assert.False(retryPreview.SourceExists);
        Assert.Null(retryPreview.Pack);

        var afterRestart = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var restartedDetail = await afterRestart.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(2, restartedDetail!.Episodes.Count(episode => episode.HasFile));

        var recovery = await seriesRepository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.Single(recovery.RecentCases, item => item.FailureKind == "unmatched");
        Assert.Contains(recovery.RecentCases, item => item.Summary.Contains("does not prove", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #652: a season pack with one extra the catalogue never listed as an
    /// episode - "S01E00 - Making of ..." is the shape from the bug - used to
    /// block the whole pack. It is skipped and named instead, and every file
    /// that does resolve is still imported.
    /// </summary>
    /// <summary>#662: a season pack queues one media-server refresh naming every placed episode - not one call per file.</summary>
    [Fact]
    public async Task Tv_pack_queues_a_single_media_server_refresh_naming_every_placed_episode()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(seriesRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, Path.Combine(storage.DataRoot, "movies"), downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath, qualityProfileId: null);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("Pack Refresh", 2026, "tt1112223"), CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);

        var scheduler = new FakeJobScheduler();
        var service = CreateService(
            storage, timeProvider, platform, libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: seriesRepository,
            mediaServerJobScheduler: scheduler);

        var packDirectory = Path.Combine(downloadsPath, "Pack.Refresh.S01.WEB-DL");
        Directory.CreateDirectory(packDirectory);
        foreach (var episodeNumber in new[] { 1, 2 })
        {
            var path = Path.Combine(packDirectory, $"Pack.Refresh.S01E{episodeNumber:D2}.WEB-DL.mkv");
            await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)(0x40 + episodeNumber), 4096).ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }

        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory,
                "Pack.Refresh.S01.WEB-DL.mkv",
                "tv",
                "Pack Refresh",
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

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Response!.PackFiles!.Count);

        var enqueued = Assert.Single(scheduler.Requests);
        Assert.Equal(MediaServerJobTypes.Refresh, enqueued.JobType);
        var payload = JsonSerializer.Deserialize<MediaServerRefreshJobPayload>(enqueued.PayloadJson!)!;
        Assert.Equal("tv", payload.MediaType);
        Assert.Equal(2, payload.Changes.Count);
        Assert.All(payload.Changes, change => Assert.Equal(MediaServerChangeKind.Created, change.Kind));
        Assert.Equal(
            result.Response.PackFiles!.Select(file => file.DestinationPath).OrderBy(path => path),
            payload.Changes.Select(change => change.Path).OrderBy(path => path));
    }

    [Fact]
    public async Task Tv_pack_skips_a_file_that_does_not_resolve_to_a_canonical_episode_and_imports_the_rest()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Extra Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Extra.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"Extra.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x70 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var extraPath = Path.Combine(packDirectory, "Extra.Pack.S01E00.Making.Of.mkv");
        await File.WriteAllBytesAsync(extraPath, Enumerable.Repeat((byte)0x99, 4096).ToArray());
        File.SetLastWriteTimeUtc(extraPath, DateTime.UtcNow.AddMinutes(-1));

        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.True(preview.Pack!.CanExecute, string.Join(" | ", preview.Pack.BlockReasons));
        Assert.Equal(3, preview.Pack.SourceFileCount);
        Assert.Equal(2, preview.Pack.EpisodeCount);
        var previewSkipped = Assert.Single(preview.Pack.SkippedFiles!);
        Assert.Equal(extraPath, previewSkipped.SourcePath);
        Assert.Contains("does not resolve", previewSkipped.Reason, StringComparison.OrdinalIgnoreCase);

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Skipped 1 file(s)", result.Message, StringComparison.Ordinal);
        Assert.Contains("Making.Of", result.Message, StringComparison.OrdinalIgnoreCase);
        var skipped = Assert.Single(result.Response!.SkippedFiles!);
        Assert.Equal(extraPath, skipped.SourcePath);
        Assert.Equal(2, result.Response.PackFiles!.Count);

        Assert.True(File.Exists(extraPath));
        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(2, detail!.Episodes.Count(episode => episode.HasFile));

        // #957: an unmatched season-pack file is not only named in the
        // response and Activity -- it raises the same kind of recovery case
        // a missing processor-output file does, so it shows on the show page
        // and on Health until dealt with. It named no episode, so only
        // "Import manually" applies, not "Search".
        var summary = await repository.GetImportRecoverySummaryAsync(CancellationToken.None);
        var unmatchedCase = Assert.Single(summary.RecentCases, item => item.FailureKind == "processor-unmatched-output");
        Assert.Contains("Making.Of", unmatchedCase.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not resolve to a catalogued episode", unmatchedCase.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Import what is there manually, or search again.", unmatchedCase.RecommendedAction);
    }

    /// <summary>
    /// #1072 (James, 30 Sep): Andor S02 has E01-E02 removed and unmonitored; a season pack that still holds them
    /// must never bring them back. Only the wanted episode is imported; the removed ones are left in the folder.
    /// </summary>
    [Fact]
    public async Task Tv_pack_never_brings_back_an_episode_that_was_removed_and_unmonitored()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Removed Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-3)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 3, "Three", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var before = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var removedIds = before!.Episodes.Where(episode => episode.EpisodeNumber <= 2).Select(episode => episode.EpisodeId).ToArray();
        await repository.UpdateEpisodeMonitoredAsync(removedIds, false, CancellationToken.None);

        var packDirectory = Path.Combine(downloadsPath, "Removed.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        var sources = new Dictionary<int, string>();
        foreach (var episode in new[] { 1, 2, 3 })
        {
            var source = Path.Combine(packDirectory, $"Removed.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x60 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
            sources[episode] = source;
        }

        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(result.Response!.PackFiles!);
        Assert.Equal(2, result.Response.SkippedFiles!.Count);
        Assert.All(result.Response.SkippedFiles, skipped => Assert.Contains("no longer wanted", skipped.Reason, StringComparison.Ordinal));
        Assert.True(File.Exists(sources[1]));
        Assert.True(File.Exists(sources[2]));

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.False(detail!.Episodes.Single(episode => episode.EpisodeNumber == 1).HasFile);
        Assert.False(detail.Episodes.Single(episode => episode.EpisodeNumber == 2).HasFile);
        Assert.True(detail.Episodes.Single(episode => episode.EpisodeNumber == 3).HasFile);
        Assert.False(detail.Episodes.Single(episode => episode.EpisodeNumber == 1).Monitored);
    }

    /// <summary>
    /// #889 (cleanup sheet E9): a season pack used to stop entirely the moment
    /// one matched file had its own problem - nine good episodes and one
    /// corrupt file imported none of them. Now the corrupt file is treated
    /// like any other thing Deluno cannot use: named and left alone, while
    /// every episode that is fine still imports, is removed from the client,
    /// and is not blocklisted.
    /// </summary>
    [Fact]
    public async Task Tv_pack_imports_good_episodes_when_one_matched_file_is_corrupt_and_never_blocklists()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Mixed Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-3)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 3, "Three", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Mixed.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        var episodePaths = new Dictionary<int, string>();
        foreach (var episode in new[] { 1, 2, 3 })
        {
            var source = Path.Combine(packDirectory, $"Mixed.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x50 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
            episodePaths[episode] = source;
        }
        var corruptPath = episodePaths[2];

        var scheduler = new FakeJobScheduler();
        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository,
            mediaProbeService: new PerPathProbeService(corruptPath, "moov atom not found"),
            mediaServerJobScheduler: scheduler);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, result.Response!.PackFiles!.Count);
        var skipped = Assert.Single(result.Response.SkippedFiles!);
        Assert.Equal(corruptPath, skipped.SourcePath);
        Assert.Contains("S01E02", skipped.Reason, StringComparison.Ordinal);
        Assert.Contains("moov atom not found", skipped.Reason, StringComparison.Ordinal);

        // The corrupt file is named, not deleted or blocklisted (#889's "your
        // call" for cleanup means Deluno never removes it on its own).
        Assert.True(File.Exists(corruptPath));
        var recovery = await repository.GetImportRecoverySummaryAsync(CancellationToken.None);
        Assert.DoesNotContain(recovery.RecentCases, item => item.FailureKind == "blocked" || item.Summary.Contains("blocklist", StringComparison.OrdinalIgnoreCase));

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.True(detail!.Episodes.Single(episode => episode.EpisodeNumber == 1).HasFile);
        Assert.True(detail.Episodes.Single(episode => episode.EpisodeNumber == 3).HasFile);
        // Still missing, not blocked.
        var missingEpisode = detail.Episodes.Single(episode => episode.EpisodeNumber == 2);
        Assert.False(missingEpisode.HasFile);

        // #957: the corrupt file matched an episode, so its case names that
        // episode and offers Search as well as Import manually -- the same
        // plumbing a missing processor-output file already uses (#930).
        var unmatchedCase = Assert.Single(recovery.RecentCases, item => item.FailureKind == "processor-unmatched-output");
        Assert.Contains("moov atom not found", unmatchedCase.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Search for this episode, or import it manually.", unmatchedCase.RecommendedAction);
        Assert.Contains($"\"episodeId\":\"{missingEpisode.EpisodeId}\"", unmatchedCase.DetailsJson, StringComparison.OrdinalIgnoreCase);

        // #889: a search is queued for exactly the missing episode - never
        // the whole show, which already has two episodes it does not need
        // searched again.
        var searchJob = Assert.Single(scheduler.Requests, request => request.RelatedEntityType == "episode" && request.RelatedEntityId == missingEpisode.EpisodeId);
        Assert.Contains("\"targetEntityId\":\"" + missingEpisode.EpisodeId + "\"", searchJob.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scheduler.Requests, request => request.RelatedEntityType == "episode" &&
            (request.RelatedEntityId == detail.Episodes.Single(episode => episode.EpisodeNumber == 1).EpisodeId ||
             request.RelatedEntityId == detail.Episodes.Single(episode => episode.EpisodeNumber == 3).EpisodeId));
    }

    /// <summary>
    /// #950: the only post-import client removal used to run from the
    /// single-file branch only, so a season pack was never removed from its
    /// client even with "Remove completed" on. Here every file in the pack
    /// matches a canonical episode - a full match - and the pack still gets
    /// the same removal a single file already gets.
    /// </summary>
    [Fact]
    public async Task Tv_pack_removes_the_completed_download_when_every_episode_matches_and_Remove_completed_is_on()
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
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Full Match Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Full.Match.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"Full.Match.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x80 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }

        var connections = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true,
                RemoveCompleted: true),
            CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", series.Id, "Full.Match.Pack.S01.WEB-DL", "TestIndexer", client.Id, client.Name, "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "full-match-pack-hash", 8192, completed: true, CancellationToken.None);

        var gateway = new FakeDownloadClientActionGateway();
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository,
            downloadDispatchesRepository: dispatches,
            connectionsRepository: connections,
            downloadClientActions: gateway);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true, DispatchId: dispatchId);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Response!.PackFiles!.Count);
        Assert.Empty(result.Response.SkippedFiles!);
        Assert.Equal(client.Id, gateway.LastClientId);
        Assert.Equal("full-match-pack-hash", gateway.LastQueueItemId);
    }

    /// <summary>
    /// #950: a partial match - one file in the pack does not resolve to a
    /// canonical episode and is skipped - still imports every episode that
    /// does match, and the pack is still removed from its client under the
    /// same "Remove completed" setting a single file already used.
    /// </summary>
    [Fact]
    public async Task Tv_pack_removes_the_completed_download_when_some_files_are_skipped_and_Remove_completed_is_on()
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
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Partial Match Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Partial.Match.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"Partial.Match.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x90 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var extraPath = Path.Combine(packDirectory, "Partial.Match.Pack.S01E00.Making.Of.mkv");
        await File.WriteAllBytesAsync(extraPath, Enumerable.Repeat((byte)0xAA, 4096).ToArray());
        File.SetLastWriteTimeUtc(extraPath, DateTime.UtcNow.AddMinutes(-1));

        var connections = new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true,
                RemoveCompleted: true),
            CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", series.Id, "Partial.Match.Pack.S01.WEB-DL", "TestIndexer", client.Id, client.Name, "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "partial-match-pack-hash", 12288, completed: true, CancellationToken.None);

        var gateway = new FakeDownloadClientActionGateway();
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository,
            downloadDispatchesRepository: dispatches,
            connectionsRepository: connections,
            downloadClientActions: gateway);
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true, DispatchId: dispatchId);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Response!.PackFiles!.Count);
        var skipped = Assert.Single(result.Response.SkippedFiles!);
        Assert.Equal(extraPath, skipped.SourcePath);
        Assert.Equal(client.Id, gateway.LastClientId);
        Assert.Equal("partial-match-pack-hash", gateway.LastQueueItemId);
    }

    /// <summary>
    /// #889: a whole-pack problem - here, nobody chose which show the pack
    /// belongs to, a setup gap rather than anything wrong with a release -
    /// still blocks every file (unlike a single file's own problem), but it
    /// is recorded as "unmatched", never "blocked": a setup gap or an outage
    /// is never treated as a bad release, whole-pack block or not.
    /// </summary>
    [Fact]
    public async Task Tv_pack_with_no_chosen_show_blocks_everything_but_never_blocklists()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(seriesRootPath);
        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, Path.Combine(storage.DataRoot, "movies"), downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var packDirectory = Path.Combine(downloadsPath, "No.Show.Chosen.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"No.Show.Chosen.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x60 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }

        var service = CreateService(
            storage, timeProvider, platform, libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository);
        var request = new ImportExecuteRequest(
            // No SeriesId: exactly the setup gap the pack cannot get past.
            new ImportPreviewRequest(packDirectory, null, "tv", "No Show Chosen", 2026, [], [], null, null),
            "copy", false, true);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("Choose the existing TV show", result.Message, StringComparison.Ordinal);

        var recovery = await repository.GetImportRecoverySummaryAsync(CancellationToken.None);
        var recorded = Assert.Single(recovery.RecentCases, item => item.Summary.Contains("No.Show.Chosen", StringComparison.OrdinalIgnoreCase) || item.Summary.Contains("Choose the existing TV show", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unmatched", recorded.FailureKind);
        Assert.DoesNotContain(recovery.RecentCases, item => item.FailureKind == "blocked" || item.Summary.Contains("blocklist", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #888: today's only behaviour, and every install's default - an
    /// unmatched file is held for manual import no matter how many times the
    /// same release fails to match. Nothing is ever blocklisted on its own.
    /// </summary>
    [Fact]
    public async Task Unmatched_import_is_never_blocklisted_by_default()
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
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Cannot Match", 2026, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        const string releaseName = "Cannot.Match.2026.WEB-DL-DELUNO";
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository, downloadDispatchesRepository: dispatches, blockedReleases: blockedReleases);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var sourcePath = Path.Combine(downloadsPath, $"cannot-match-{attempt}.mkv");
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x11, 2048).ToArray());
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
            var dispatchId = await jobStore.RecordDownloadDispatchAsync(
                library.Id, "tv", "series", series.Id, releaseName, "TestIndexer", "client-1", "TestClient", "sent",
                notesJson: null, cancellationToken: CancellationToken.None);

            var result = await service.ExecuteAsync(
                new ImportExecuteRequest(
                    new ImportPreviewRequest(sourcePath, null, "tv", series.Title, series.StartYear, [], [], null, null,
                        SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
                    "copy", false, true, DispatchId: dispatchId),
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(409, result.StatusCode);
        }

        Assert.Empty(await blockedReleases.ListKeysAsync(CancellationToken.None));
    }

    /// <summary>
    /// #888 (cleanup sheet E5): James chose a setting so an unmatched file
    /// does not have to be held for ever. With "retry once then treat it as
    /// bad" chosen, a second unmatched failure of the same release is
    /// blocklisted the same way any other confirmed-bad release would be.
    /// </summary>
    [Fact]
    public async Task Unmatched_import_is_blocklisted_after_one_retry_when_that_policy_is_chosen()
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
        await platform.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                await platform.GetAsync(CancellationToken.None),
                new PatchPlatformSettingsRequest(UnmatchedImportPolicy: UnmatchedImportPolicies.RetryOnceThenBad)),
            CancellationToken.None);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Cannot Match Retry", 2026, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        const string releaseName = "Cannot.Match.Retry.2026.WEB-DL-DELUNO";
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: repository, downloadDispatchesRepository: dispatches, blockedReleases: blockedReleases);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var sourcePath = Path.Combine(downloadsPath, $"cannot-match-retry-{attempt}.mkv");
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x22, 2048).ToArray());
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
            var dispatchId = await jobStore.RecordDownloadDispatchAsync(
                library.Id, "tv", "series", series.Id, releaseName, "TestIndexer", "client-1", "TestClient", "sent",
                notesJson: null, cancellationToken: CancellationToken.None);

            var result = await service.ExecuteAsync(
                new ImportExecuteRequest(
                    new ImportPreviewRequest(sourcePath, null, "tv", series.Title, series.StartYear, [], [], null, null,
                        SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
                    "copy", false, true, DispatchId: dispatchId),
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(409, result.StatusCode);
        }

        var blockedKeys = await blockedReleases.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For(releaseName, "TestIndexer"), blockedKeys);
    }

    /// <summary>
    /// #652: a season-0 special the catalogue DOES list is not an extra. It
    /// resolves like any other episode and is imported as that special.
    /// </summary>
    [Fact]
    public async Task Tv_pack_imports_an_episode_zero_special_the_catalogue_lists()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Special Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 0, "Making Of", null, timeProvider.GetUtcNow().AddDays(-3)),
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Special.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var episode in new[] { 0, 1, 2 })
        {
            var source = Path.Combine(packDirectory, $"Special.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x80 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }

        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var preview = await service.PreviewAsync(request.Preview, CancellationToken.None);
        Assert.True(preview.Pack!.CanExecute, string.Join(" | ", preview.Pack.BlockReasons));
        Assert.Equal(3, preview.Pack.EpisodeCount);
        Assert.Empty(preview.Pack.SkippedFiles ?? []);

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(result.Response!.SkippedFiles ?? []);
        Assert.Equal(3, result.Response.PackFiles!.Count);

        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(3, detail!.Episodes.Count(episode => episode.HasFile));
    }

    /// <summary>
    /// #652: a file still being written by the download client or a processor
    /// is transient, not a reason to refuse the pack. Deluno waits the pack's
    /// shared settle window and imports normally once it settles.
    /// </summary>
    [Fact]
    public async Task Tv_pack_with_a_file_that_is_not_stable_yet_is_retried_and_then_imported()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Locked Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "Locked.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        var stableSource = Path.Combine(packDirectory, "Locked.Pack.S01E01.mkv");
        await File.WriteAllBytesAsync(stableSource, Enumerable.Repeat((byte)0x21, 4096).ToArray());
        File.SetLastWriteTimeUtc(stableSource, DateTime.UtcNow.AddMinutes(-1));
        var lockedSource = Path.Combine(packDirectory, "Locked.Pack.S01E02.mkv");
        await File.WriteAllBytesAsync(lockedSource, Enumerable.Repeat((byte)0x22, 4096).ToArray());
        // Not yet stable: the settle-window wait has to let real time pass
        // before this file reads as ready, the same way the single-file
        // settle test above does.
        File.SetLastWriteTimeUtc(lockedSource, DateTime.UtcNow);

        var service = CreateService(
            storage,
            timeProvider,
            platform,
            libraries,
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            settleWindow: TimeSpan.FromSeconds(10));
        var request = new ImportExecuteRequest(
            new ImportPreviewRequest(
                packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
            "copy", false, true);

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Response!.PackFiles!.Count);
        var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        Assert.Equal(2, detail!.Episodes.Count(episode => episode.HasFile));
    }

    /// <summary>
    /// #652: skipping non-resolving files must not turn "nothing matched" into
    /// a quiet success. A pack with no resolvable episode still fails whole,
    /// exactly as before.
    /// </summary>
    [Fact]
    public async Task Tv_pack_with_zero_resolvable_episodes_still_fails()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("No Match Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-1))],
            "provider",
            CancellationToken.None);
        var packDirectory = Path.Combine(downloadsPath, "No.Match.Pack.S01");
        Directory.CreateDirectory(packDirectory);
        foreach (var suffix in new[] { "00", "99" })
        {
            var source = Path.Combine(packDirectory, $"No.Match.Pack.S01E{suffix}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)0x33, 4096).ToArray());
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
        Assert.Contains(preview.Pack.BlockReasons, reason => reason.Contains("No file in this pack resolves", StringComparison.OrdinalIgnoreCase));

        var result = await service.ExecuteAsync(request, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("No file in this pack resolves", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Directory.GetFiles(packDirectory, "*.mkv").Length);
        Assert.DoesNotContain((await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes, episode => episode.HasFile);
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
        var newBytesByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(newDirectory, $"Replace.Pack.S01E{episode:D2}.mkv");
            var bytes = Enumerable.Repeat((byte)(0x70 + episode), 4096).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
            newBytesByPath[source] = bytes;
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
            // #892: the copy import's own source is checked and cleaned up
            // once the pack is committed, so it is compared against the
            // bytes captured before the import ran, not re-read from disk.
            Assert.Equal(newBytesByPath[file.SourcePath], await File.ReadAllBytesAsync(file.DestinationPath));
            Assert.False(File.Exists(file.SourcePath));
        }
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.deluno-pack-backup*", SearchOption.AllDirectories));

        // #892: the replacement's own source is cleaned up the same way a
        // fresh pack's is, so the directory this request pointed at is now
        // empty - there is nothing left for a later request against that
        // same path to rescan into an "already-committed" answer. What still
        // holds is the library's own record of the replacement.
        Assert.Empty(Directory.EnumerateFiles(newDirectory));
        Assert.Equal(2, (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes.Count(episode => episode.HasFile));
    }

    /// <summary>
    /// #1254, next door: a season pack sent again is cleaned again into files that differ from the ones its
    /// episodes already own at the same paths. The pack import replaces its own episodes' files the first time
    /// instead of blocking on "already exists" and being retried until it gives up.
    /// </summary>
    [Theory]
    [InlineData("copy")]
    [InlineData("hardlink")]
    public async Task A_season_pack_sent_again_replaces_the_files_its_episodes_already_own(string transferMode)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-12T08:00:00Z"));
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Resent Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var packDirectory = Path.Combine(downloadsPath, "Resent.Pack.S01");
        async Task<ImportExecuteRequest> CleanPackAsync(byte seed)
        {
            Directory.CreateDirectory(packDirectory);
            foreach (var episode in new[] { 1, 2 })
            {
                var source = Path.Combine(packDirectory, $"Resent.Pack.S01E{episode:D2}.mkv");
                await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(seed + episode), 4096).ToArray());
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
            }
            return new ImportExecuteRequest(
                new ImportPreviewRequest(
                    packDirectory, null, "tv", series.Title, series.StartYear, [], [], null, null,
                    SeriesId: series.Id, SeriesType: SeriesTypes.Standard, NumberingScheme: SeriesNumberingSchemes.Standard),
                transferMode, false, true);
        }

        Assert.True((await service.ExecuteAsync(await CleanPackAsync(0x20), CancellationToken.None)).Succeeded);
        var owned = new List<string>();
        foreach (var episode in (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes)
        {
            owned.Add((await repository.GetEpisodeFilePathAsync(episode.EpisodeId, CancellationToken.None))!);
        }

        var sentAgain = await service.ExecuteAsync(
            (await CleanPackAsync(0x70)) with { DispatchId = "season-dispatch-2" },
            CancellationToken.None);

        Assert.True(sentAgain.Succeeded, sentAgain.Message);
        Assert.NotEqual(409, sentAgain.StatusCode);
        foreach (var path in owned)
        {
            Assert.True(File.Exists(path));
            Assert.All(await File.ReadAllBytesAsync(path), value => Assert.True(value >= 0x71));
        }
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.deluno-pack-backup*", SearchOption.AllDirectories));
        if (transferMode == "copy")
        {
            Assert.Empty(Directory.EnumerateFiles(packDirectory));
        }
        Assert.Equal(2, (await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None))!.Episodes.Count(episode => episode.HasFile));
    }

    /// <summary>
    /// #945: a single-file upgrade already recycles the sidecar subtitles
    /// beside the video it replaces (#893). A season-pack replacement went
    /// through a different path - <c>RecycleReplacedFilesAsync</c> was never
    /// called for it - and left every replaced episode's subtitles behind.
    /// </summary>
    [Fact]
    public async Task Tv_pack_replacement_also_removes_the_replaced_episodes_subtitles()
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
        var series = await repository.AddAsync(new CreateSeriesRequest("Subtitle Pack", 2026, null), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(1, 1, "One", null, timeProvider.GetUtcNow().AddDays(-2)),
                new CatalogueEpisodeItem(1, 2, "Two", null, timeProvider.GetUtcNow().AddDays(-1))
            ],
            "provider",
            CancellationToken.None);
        var service = CreateService(storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider));

        var oldDirectory = Path.Combine(downloadsPath, "old", "Subtitle.Pack.S01");
        Directory.CreateDirectory(oldDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(oldDirectory, $"Subtitle.Pack.S01E{episode:D2}.mkv");
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

        // Subtitles left beside each installed episode - the same shape
        // Bazarr (or a person) leaves beside a single movie file.
        var subtitlePaths = new List<string>();
        foreach (var target in targets)
        {
            var baseName = Path.GetFileNameWithoutExtension(target.ExpectedPath);
            var directory = Path.GetDirectoryName(target.ExpectedPath)!;
            var subtitlePath = Path.Combine(directory, $"{baseName}.en.srt");
            await File.WriteAllTextAsync(subtitlePath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
            subtitlePaths.Add(subtitlePath);
        }
        Assert.All(subtitlePaths, path => Assert.True(File.Exists(path)));

        var newDirectory = Path.Combine(downloadsPath, "new", "Subtitle.Pack.S01");
        Directory.CreateDirectory(newDirectory);
        foreach (var episode in new[] { 1, 2 })
        {
            var source = Path.Combine(newDirectory, $"Subtitle.Pack.S01E{episode:D2}.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)(0x70 + episode), 4096).ToArray());
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        }
        var replacementRequest = oldRequest with
        {
            Preview = oldRequest.Preview with { SourcePath = newDirectory },
            Overwrite = true,
            ForceReplacement = true,
            DispatchId = "season-dispatch-subtitles",
            ReplacementTargets = targets
        };

        var replaced = await service.ExecuteAsync(replacementRequest, CancellationToken.None);
        Assert.True(replaced.Succeeded, replaced.Message);

        Assert.All(subtitlePaths, path => Assert.False(
            File.Exists(path),
            $"the replaced episode's subtitle {path} should have been recycled with its video"));
        Assert.Contains("subtitle", replaced.Message, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// The bug live on the rig (#656): The Wild Robot's scheduled upgrade
    /// imported "The Wild Robot (2024) [Bluray 1080p].mkv" and pointed the
    /// catalogue at it, but its "[WEB 720p]" predecessor - a different name,
    /// because quality is part of the naming scheme - was never touched and
    /// sat in the folder doubling disk use. An upgrade import must find and
    /// remove the file it replaces even when the new file's name differs
    /// from the old one.
    /// </summary>
    [Fact]
    public async Task Movie_upgrade_with_a_different_name_removes_the_file_it_replaced()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));

        // Already catalogued, the way a title Deluno is tracking always is
        // before its first grab.
        var catalogued = await movies.AddAsync(new CreateMovieRequest("The Wild Robot", 2024, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));

        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 WEB 720p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstRequest = new ImportPreviewRequest(
            SourcePath: firstSource, FileName: null, MediaType: "movies",
            Title: "The Wild Robot", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(firstRequest, "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var trackedAfterFirst = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            trackedAfterFirst.Add(item);
        }
        var afterFirst = Assert.Single(trackedAfterFirst);
        var originalFilePath = afterFirst.FilePath;
        Assert.EndsWith("[WEB 720p].mkv", originalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(originalFilePath));

        // The scheduled cycle grabbed a better release for the same, already
        // catalogued film - the dispatch carries the film's real id, the way
        // WorkPlanner's grab does.
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 REPACK 1080p BluRay DD 7 1 x264-playHD", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);

        var secondSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.REPACK.1080p.BluRay.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x22, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));

        var secondRequest = new ImportPreviewRequest(
            SourcePath: secondSource, FileName: null, MediaType: "movies",
            Title: "The Wild Robot", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                secondRequest, "copy", Overwrite: true, AllowCopyFallback: true,
                ForceReplacement: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        var newFilePath = secondResult.Response!.Preview.DestinationPath;
        Assert.EndsWith("[Bluray 1080p].mkv", newFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(originalFilePath, newFilePath, StringComparer.OrdinalIgnoreCase);

        // Both files existed side by side before the fix; now only the new one does.
        Assert.True(File.Exists(newFilePath));
        Assert.False(File.Exists(originalFilePath));
        Assert.Equal(
            Directory.GetParent(originalFilePath)!.FullName,
            Directory.GetParent(newFilePath)!.FullName,
            ignoreCase: true);

        // The activity message says what happened to the old file.
        Assert.Contains("previous", secondResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WEB 720p].mkv", secondResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaced", secondResult.Message, StringComparison.OrdinalIgnoreCase);

        var trackedAfterSecond = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            trackedAfterSecond.Add(item);
        }
        var afterSecond = Assert.Single(trackedAfterSecond);
        Assert.Equal(newFilePath, afterSecond.FilePath);
    }

    /// <summary>
    /// #893: an upgrade recycles the video it replaces (#656), but left the old
    /// video's subtitle files sitting beside it forever - nothing moved them and
    /// nothing ever looked at that folder again. They must go with the video.
    /// </summary>
    [Fact]
    public async Task Movie_upgrade_also_removes_the_old_file_s_subtitles()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));

        var catalogued = await movies.AddAsync(new CreateMovieRequest("The Wild Robot", 2024, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));

        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 WEB 720p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstRequest = new ImportPreviewRequest(
            SourcePath: firstSource, FileName: null, MediaType: "movies",
            Title: "The Wild Robot", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(firstRequest, "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var trackedAfterFirst = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            trackedAfterFirst.Add(item);
        }
        var originalFilePath = Assert.Single(trackedAfterFirst).FilePath;

        // Subtitles Bazarr (or a person) left beside the first file - one
        // matching its exact name, one relying on the "lone video" rule.
        var originalBaseName = Path.GetFileNameWithoutExtension(originalFilePath);
        var originalDirectory = Path.GetDirectoryName(originalFilePath)!;
        var englishSubtitlePath = Path.Combine(originalDirectory, $"{originalBaseName}.en.srt");
        await File.WriteAllTextAsync(englishSubtitlePath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        Assert.True(File.Exists(englishSubtitlePath));

        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 REPACK 1080p BluRay DD 7 1 x264-playHD", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);

        var secondSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.REPACK.1080p.BluRay.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x22, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));

        var secondRequest = new ImportPreviewRequest(
            SourcePath: secondSource, FileName: null, MediaType: "movies",
            Title: "The Wild Robot", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                secondRequest, "copy", Overwrite: true, AllowCopyFallback: true,
                ForceReplacement: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        Assert.False(File.Exists(originalFilePath), "the replaced video should have been recycled");
        Assert.False(File.Exists(englishSubtitlePath), "the replaced video's subtitle should have been recycled with it");
        Assert.Contains("subtitle", secondResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#662: an upgrade queues both the new file (Created) and the file #656 recycled (Deleted), in one job.</summary>
    [Fact]
    public async Task An_upgrade_queues_a_media_server_refresh_with_the_new_file_created_and_the_old_one_deleted()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var scheduler = new FakeJobScheduler();
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider),
            mediaServerJobScheduler: scheduler);
        var catalogued = await movies.AddAsync(new CreateMovieRequest("The Wild Robot", 2024, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));
        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 WEB 720p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(firstSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);
        var originalFilePath = firstResult.Response!.Preview.DestinationPath;
        scheduler.Requests.Clear();

        var secondDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 REPACK 1080p BluRay", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var secondSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.REPACK.1080p.BluRay.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x22, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(secondSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: true, AllowCopyFallback: true, ForceReplacement: true, DispatchId: secondDispatchId),
            CancellationToken.None);
        Assert.True(secondResult.Succeeded, secondResult.Message);
        var newFilePath = secondResult.Response!.Preview.DestinationPath;

        var request = Assert.Single(scheduler.Requests);
        var payload = JsonSerializer.Deserialize<MediaServerRefreshJobPayload>(request.PayloadJson!)!;
        Assert.Contains(payload.Changes, change => change.Path == newFilePath && change.Kind == MediaServerChangeKind.Created);
        Assert.Contains(payload.Changes, change => change.Path == originalFilePath && change.Kind == MediaServerChangeKind.Deleted);
    }

    /// <summary>
    /// #931: an upgrade replacing an old file with a better one is Deluno's own
    /// removal of that old file (recycled via #656/#662, above), never a file
    /// vanishing outside Deluno. It must stay monitored and the reconciliation's
    /// "removed outside Deluno" sentence must never appear for it.
    /// </summary>
    [Fact]
    public async Task An_upgrade_replacing_the_old_file_never_writes_the_removed_outside_Deluno_line()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var catalogued = await movies.AddAsync(new CreateMovieRequest("The Wild Robot", 2024, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));
        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 WEB 720p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(firstSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var secondDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "The Wild Robot 2024 REPACK 1080p BluRay", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var secondSource = Path.Combine(downloadsPath, "The.Wild.Robot.2024.REPACK.1080p.BluRay.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x22, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(secondSource, null, "movies", "The Wild Robot", 2024, [], [], null, null),
                "copy", Overwrite: true, AllowCopyFallback: true, ForceReplacement: true, DispatchId: secondDispatchId),
            CancellationToken.None);
        Assert.True(secondResult.Succeeded, secondResult.Message);

        var afterUpgrade = Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.True(afterUpgrade.Monitored, "an upgrade must never unmonitor the title it just improved");
        Assert.True(afterUpgrade.HasFile);

        var activity = await jobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.DoesNotContain(
            activity,
            entry => entry.Message.Contains("removed outside Deluno", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>#662: a fresh import queues one media-server refresh naming the new file, and nothing replaced.</summary>
    [Fact]
    public async Task A_fresh_import_queues_a_media_server_refresh_for_the_new_file()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var scheduler = new FakeJobScheduler();
        var service = CreateService(storage, timeProvider, platform, libraries, movies, mediaServerJobScheduler: scheduler);

        var sourcePath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "movies", "Arrival", 2016, [], [], null, null),
                "copy", Overwrite: true, AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        var request = Assert.Single(scheduler.Requests);
        Assert.Equal(MediaServerJobTypes.Refresh, request.JobType);
        var payload = JsonSerializer.Deserialize<MediaServerRefreshJobPayload>(request.PayloadJson!)!;
        Assert.Equal("movies", payload.MediaType);
        var change = Assert.Single(payload.Changes);
        Assert.Equal(result.Response!.Preview.DestinationPath, change.Path);
        Assert.Equal(MediaServerChangeKind.Created, change.Kind);
    }

    /// <summary>
    /// The same scenario, but the new release renames to the exact path the
    /// old file already occupied (#656's guard: never touch a path when old
    /// and new are the same file). This is the pre-existing backup-then-
    /// recycle path, and the new replaced-file cleanup must not also try to
    /// act on a path that is simply the file just written.
    /// </summary>
    [Fact]
    public async Task Movie_upgrade_onto_the_same_path_is_not_treated_as_a_second_replaced_file()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var catalogued = await movies.AddAsync(new CreateMovieRequest("Sintel", 2010, null), CancellationToken.None);

        var firstSource = Path.Combine(downloadsPath, "Sintel.2010.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x33, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));
        var request = new ImportPreviewRequest(
            SourcePath: firstSource, FileName: null, MediaType: "movies",
            Title: "Sintel", Year: 2010, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "Sintel 2010 WEB 1080p", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(request, "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: firstDispatchId),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);
        var destinationPath = firstResult.Response!.Preview.DestinationPath;

        // A second copy of the very same release quality - the new file
        // renames onto the exact path the old one already occupies.
        var secondSource = Path.Combine(downloadsPath, "Sintel.2010.WEB.1080p.repack.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x44, 4096).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));
        var secondDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "Sintel 2010 WEB 1080p REPACK", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var secondRequest = request with { SourcePath = secondSource, FileName = null };
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                secondRequest, "copy", Overwrite: true, AllowCopyFallback: true,
                ForceReplacement: true, DispatchId: secondDispatchId, ExpectedExistingPath: destinationPath),
            CancellationToken.None);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        Assert.Equal(destinationPath, secondResult.Response!.Preview.DestinationPath);
        Assert.True(File.Exists(destinationPath));
        // No second "file it replaced" cleanup line - the destination path
        // and the only tracked prior path are one and the same file.
        Assert.DoesNotContain("file it replaced", secondResult.Message, StringComparison.OrdinalIgnoreCase);

        var tracked = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            tracked.Add(item);
        }
        Assert.Single(tracked);
        Assert.Single(Directory.GetFiles(Directory.GetParent(destinationPath)!.FullName, "*.mkv"));
    }

    /// <summary>
    /// The episode mirror of #656: a single-file episode upgrade (not a
    /// season pack) that renames to a different quality tag must find and
    /// remove the episode file it replaces.
    /// </summary>
    [Fact]
    public async Task Episode_upgrade_with_a_different_name_removes_the_file_it_replaced()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(
            platform,
            movieRootPath,
            downloadsPath,
            seriesRootPath,
            episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality}]");
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);

        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(
            new CreateSeriesRequest("Test Show", 2024, null),
            CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, timeProvider.GetUtcNow().AddYears(-1))],
            "provider",
            CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies, seriesCatalogRepository: seriesRepository);

        var firstSource = Path.Combine(downloadsPath, "Test.Show.S01E01.WEB.720p.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x55, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstSource, DateTime.UtcNow.AddMinutes(-1));
        var firstRequest = new ImportPreviewRequest(
            SourcePath: firstSource, FileName: null, MediaType: "tv",
            Title: "Test Show", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null,
            SeriesId: series.Id);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(firstRequest, "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var detailAfterFirst = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episodeAfterFirst = Assert.Single(detailAfterFirst!.Episodes, item => item.HasFile);
        var originalFilePath = await seriesRepository.GetEpisodeFilePathAsync(episodeAfterFirst.EpisodeId, CancellationToken.None);
        Assert.NotNull(originalFilePath);
        Assert.EndsWith("[WEB 720p].mkv", originalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(originalFilePath));

        var secondSource = Path.Combine(downloadsPath, "Test.Show.S01E01.BluRay.1080p.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x66, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondSource, DateTime.UtcNow.AddMinutes(-1));
        var secondRequest = new ImportPreviewRequest(
            SourcePath: secondSource, FileName: null, MediaType: "tv",
            Title: "Test Show", Year: 2024, Genres: [], Tags: [], Studio: null, OriginalLanguage: null,
            SeriesId: series.Id);
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                secondRequest, "copy", Overwrite: true, AllowCopyFallback: true, ForceReplacement: true),
            CancellationToken.None);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        var newFilePath = secondResult.Response!.Preview.DestinationPath;
        Assert.EndsWith("[Bluray 1080p].mkv", newFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(originalFilePath, newFilePath, StringComparer.OrdinalIgnoreCase);

        Assert.True(File.Exists(newFilePath));
        Assert.False(File.Exists(originalFilePath!));
        Assert.Contains("previous", secondResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaced", secondResult.Message, StringComparison.OrdinalIgnoreCase);

        var detailAfterSecond = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episodeAfterSecond = Assert.Single(detailAfterSecond!.Episodes, item => item.HasFile);
        Assert.Equal(newFilePath, await seriesRepository.GetEpisodeFilePathAsync(episodeAfterSecond.EpisodeId, CancellationToken.None));
    }

    /// <summary>
    /// The processor hand-back path (#656): a refine-before-import upgrade
    /// builds its ImportExecuteRequest the same way ProcessorOutputImportQueue
    /// does - ProcessedFromPath set to the original download - and must
    /// remove the differently-named file it replaces exactly like a direct
    /// download upgrade does.
    /// </summary>
    [Fact]
    public async Task Processor_handback_upgrade_with_a_different_name_removes_the_file_it_replaced()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var refinedPath = Path.Combine(storage.DataRoot, "refined");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            downloadDispatchesRepository: new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider));
        var catalogued = await movies.AddAsync(new CreateMovieRequest("Interstellar", 2014, null), CancellationToken.None);

        var firstDownload = Directory.CreateDirectory(Path.Combine(downloadsPath, "Interstellar.2014.WEB.720p-DELUNO")).FullName;
        var firstSource = Path.Combine(firstDownload, "Interstellar.2014.WEB.720p-DELUNO.mkv");
        await File.WriteAllBytesAsync(firstSource, Enumerable.Repeat((byte)0x77, 4096).ToArray());
        var firstProcessed = Path.Combine(
            Directory.CreateDirectory(Path.Combine(refinedPath, "Interstellar.2014.WEB.720p-DELUNO")).FullName,
            "Interstellar.2014.WEB.720p-DELUNO.mkv");
        await File.WriteAllBytesAsync(firstProcessed, Enumerable.Repeat((byte)0x77, 4096).ToArray());
        File.SetLastWriteTimeUtc(firstProcessed, DateTime.UtcNow.AddMinutes(-1));

        var firstDispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "Interstellar.2014.WEB.720p-DELUNO", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);
        var firstRequest = new ImportPreviewRequest(
            SourcePath: firstProcessed, FileName: null, MediaType: "movies",
            Title: "Interstellar", Year: 2014, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var firstResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                firstRequest, "copy", Overwrite: false, AllowCopyFallback: true,
                DispatchId: firstDispatchId, ProcessedFromPath: firstDownload),
            CancellationToken.None);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var trackedAfterFirst = new List<MovieTrackedFileItem>();
        await foreach (var item in movies.StreamTrackedFilesAsync(library.Id, CancellationToken.None))
        {
            trackedAfterFirst.Add(item);
        }
        var afterFirst = Assert.Single(trackedAfterFirst);
        var originalFilePath = afterFirst.FilePath;
        Assert.EndsWith("[WEB 720p].mkv", originalFilePath, StringComparison.OrdinalIgnoreCase);

        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            libraryId: library.Id, mediaType: "movies", entityType: "movie", entityId: catalogued.Id,
            releaseName: "Interstellar.2014.BluRay.1080p-DELUNO", indexerName: "TestIndexer",
            downloadClientId: "client-1", downloadClientName: "TestClient",
            status: "sent", notesJson: null, cancellationToken: CancellationToken.None);

        var secondDownload = Directory.CreateDirectory(Path.Combine(downloadsPath, "Interstellar.2014.BluRay.1080p-DELUNO")).FullName;
        var secondSource = Path.Combine(secondDownload, "Interstellar.2014.BluRay.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(secondSource, Enumerable.Repeat((byte)0x88, 8192).ToArray());
        var secondProcessed = Path.Combine(
            Directory.CreateDirectory(Path.Combine(refinedPath, "Interstellar.2014.BluRay.1080p-DELUNO")).FullName,
            "Interstellar.2014.BluRay.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(secondProcessed, Enumerable.Repeat((byte)0x88, 8192).ToArray());
        File.SetLastWriteTimeUtc(secondProcessed, DateTime.UtcNow.AddMinutes(-1));

        var secondRequest = new ImportPreviewRequest(
            SourcePath: secondProcessed, FileName: null, MediaType: "movies",
            Title: "Interstellar", Year: 2014, Genres: [], Tags: [], Studio: null, OriginalLanguage: null);
        var secondResult = await service.ExecuteAsync(
            new ImportExecuteRequest(
                secondRequest, "copy", Overwrite: true, AllowCopyFallback: true,
                ForceReplacement: true, DispatchId: dispatchId, ProcessedFromPath: secondDownload),
            CancellationToken.None);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        var newFilePath = secondResult.Response!.Preview.DestinationPath;
        Assert.EndsWith("[Bluray 1080p].mkv", newFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(newFilePath));
        Assert.False(File.Exists(originalFilePath));
        Assert.Contains("replaced", secondResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A processor's failures are relative, and every other check Deluno makes
    /// is absolute (#574).
    ///
    /// <para>A remux that stops early yields a perfectly valid file: readable,
    /// with a video stream, well over two minutes. It passes every guard and
    /// lands in the library as three minutes of a two-hour film. The only way
    /// to see it is to compare what came back with what went in.</para>
    /// </summary>
    [Fact]
    public async Task A_processed_file_far_shorter_than_its_download_is_not_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 180);

        var result = await scene.ImportAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("3m 0s", result.Message);
        Assert.Contains("2h 0m", result.Message);
        Assert.False(File.Exists(scene.DestinationPath));
    }

    [Fact]
    public async Task A_processed_file_the_same_length_as_its_download_is_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 7200);

        var result = await scene.ImportAsync();

        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>
    /// A processor may legitimately transcode a file far smaller, or keep one
    /// audio track out of five. Neither is evidence of anything, so neither is
    /// refused — only losing all of something is.
    /// </summary>
    [Fact]
    public async Task A_processed_file_that_is_merely_smaller_is_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 7200, processedAudioTracks: 1, sourceAudioTracks: 5);

        var result = await scene.ImportAsync();

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task A_processed_file_with_no_audio_at_all_is_not_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 7200, processedAudioTracks: 0, sourceAudioTracks: 2);

        var result = await scene.ImportAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("no audio", result.Message);
    }

    /// <summary>
    /// #783: manual import scans a folder rather than one file at a time.
    /// <see cref="ManualImportFolderScanner"/> is what finds the files and
    /// guesses each one's title/year; this proves that guess feeds straight
    /// into the same <see cref="IImportPipelineService.PreviewAsync"/> a
    /// single-file manual import already used, so a folder scan's rows carry
    /// the identical destination-path/quality/warnings review.
    /// </summary>
    [Fact]
    public async Task Folder_scan_finds_every_video_file_and_previews_its_guessed_title()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads-folder-scan");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies-folder-scan");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);

        var arrival = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p-GROUP.mkv");
        var dune = Path.Combine(downloadsPath, "Dune.Part.Two.2024.2160p.UHD.BluRay.x265-GROUP.mkv");
        var readme = Path.Combine(downloadsPath, "readme.txt");
        await File.WriteAllTextAsync(arrival, "movie");
        await File.WriteAllTextAsync(dune, "movie");
        await File.WriteAllTextAsync(readme, "not a video");

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(storage, timeProvider, platform, libraries, movies);

        var files = ManualImportFolderScanner.FindVideoFiles(downloadsPath);

        // The text file is not a video file, so only the two releases show up.
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(readme, files);

        var previews = new List<(string File, string? Title, int? Year, ImportPreviewResponse Preview)>();
        foreach (var file in files)
        {
            var (title, year) = ManualImportFolderScanner.GuessTitleAndYear(Path.GetFileName(file));
            var preview = await service.PreviewAsync(
                new ImportPreviewRequest(
                    SourcePath: file,
                    FileName: null,
                    MediaType: "movies",
                    Title: title,
                    Year: year,
                    Genres: null,
                    Tags: null,
                    Studio: null,
                    OriginalLanguage: null),
                CancellationToken.None);
            previews.Add((file, title, year, preview));
        }

        var arrivalGuess = previews.Single(item => item.File == arrival);
        Assert.Equal("Arrival", arrivalGuess.Title);
        Assert.Equal(2016, arrivalGuess.Year);
        Assert.True(arrivalGuess.Preview.SourceExists);
        Assert.Contains("Arrival", arrivalGuess.Preview.DestinationPath);

        var duneGuess = previews.Single(item => item.File == dune);
        Assert.Equal("Dune Part Two", duneGuess.Title);
        Assert.Equal(2024, duneGuess.Year);
    }

    [Theory]
    [InlineData("Arrival.2016.WEB.1080p-GROUP.mkv", "Arrival", 2016)]
    [InlineData("The.Matrix.1999.BluRay.1080p.x264-GROUP.mkv", "The Matrix", 1999)]
    [InlineData("Some.Show.S01E02.720p.HDTV.x264-GROUP.mkv", "Some Show", null)]
    [InlineData("random_video_file.mkv", "random video file", null)]
    public void Folder_scan_guesses_title_and_year_from_a_release_style_file_name(string fileName, string expectedTitle, int? expectedYear)
    {
        var (title, year) = ManualImportFolderScanner.GuessTitleAndYear(fileName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Fact]
    public async Task A_processed_file_with_no_video_at_all_is_not_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 7200, processedHasVideo: false);

        var result = await scene.ImportAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("No video stream", result.Message);
    }

    /// <summary>
    /// A processor may legitimately be set up to produce a lower resolution
    /// than its source -- that is deliberate, not a fault (#919).
    /// </summary>
    [Fact]
    public async Task A_processed_file_at_a_lower_resolution_the_processor_produces_on_purpose_is_imported()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 7200, processedWidth: 1280, processedHeight: 720);

        var result = await scene.ImportAsync();

        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>
    /// #919: on a fault, a person is offered the three named options, never
    /// asked to fix the processor themselves in different words each time.
    /// </summary>
    [Fact]
    public async Task A_fault_is_held_as_a_recovery_case_offering_the_three_options()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 180);

        await scene.ImportAsync();

        var summary = await scene.Movies.GetImportRecoverySummaryAsync(CancellationToken.None);
        var recoveryCase = Assert.Single(summary.RecentCases);
        Assert.Equal("processedFileIsLess", recoveryCase.FailureKind);
        Assert.Equal("Import the original, run the processor again, or search for another release.", recoveryCase.RecommendedAction);
    }

    /// <summary>
    /// #919: on a fault, the broken processed copy is deleted -- the
    /// original, already shown good at Checkpoint A, is kept regardless (the
    /// original itself is never probed by this scene, only referenced by
    /// ProcessedFromPath, so its continued existence is not re-asserted
    /// here).
    /// </summary>
    [Fact]
    public async Task A_fault_deletes_the_broken_processed_copy()
    {
        var scene = await ProcessedSceneAsync(processedSeconds: 180);
        Assert.True(File.Exists(scene.ProcessedFilePath));

        var result = await scene.ImportAsync();

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(scene.ProcessedFilePath));
    }

    /// <summary>
    /// #919: "lost every track in a wanted language" -- the original had a
    /// Japanese track, and the library's profile requires one, but the
    /// processed file has only English. A fault, held the same way any other
    /// processor loss is.
    /// </summary>
    [Fact]
    public async Task A_processed_file_that_lost_the_librarys_wanted_language_is_a_fault()
    {
        var scene = await ProcessedSceneAsync(
            processedSeconds: 7200,
            sourceAudioLanguage: "jpn",
            processedAudioLanguage: "eng",
            requireLanguage: "ja");

        var result = await scene.ImportAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("no audio track in Japanese", result.Message);
    }

    /// <summary>The same library requirement, but the processed file kept the wanted track -- no fault.</summary>
    [Fact]
    public async Task A_processed_file_that_kept_the_librarys_wanted_language_is_not_a_fault()
    {
        var scene = await ProcessedSceneAsync(
            processedSeconds: 7200,
            sourceAudioLanguage: "jpn",
            processedAudioLanguage: "jpn",
            requireLanguage: "ja");

        var result = await scene.ImportAsync();

        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>A library with no language requirement never faults over a language it never asked for.</summary>
    [Fact]
    public async Task A_processed_file_with_a_different_language_is_not_a_fault_when_nothing_requires_one()
    {
        var scene = await ProcessedSceneAsync(
            processedSeconds: 7200,
            sourceAudioLanguage: "jpn",
            processedAudioLanguage: "eng");

        var result = await scene.ImportAsync();

        Assert.True(result.Succeeded, result.Message);
    }

    private sealed record ProcessedScene(
        Func<Task<ImportPipelineResult>> Import,
        string DestinationPath,
        string ProcessedFilePath,
        SqliteMovieCatalogRepository Movies)
    {
        public Task<ImportPipelineResult> ImportAsync() => Import();
    }

    /// <summary>
    /// #918, cleanup sheet E4: a file that opens and is found broken is a bad
    /// release the reader is told about in plain words, not left to the log
    /// alone — the same activity trail every other import failure already
    /// gets.
    /// </summary>
    [Fact]
    public async Task A_file_the_probe_rejects_writes_the_reason_to_activity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var probe = new RejectingProbeService("Corrupt container; no readable video or audio.");
        var service = CreateService(storage, timeProvider, platform, libraries, movies, mediaProbeService: probe);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        // The service wrote Activity through its own internal SqliteJobStore;
        // reading it back through a second instance against the same
        // database is how a test outside that instance confirms it landed.
        var activity = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var recorded = await activity.ListActivityAsync(10, null, null, CancellationToken.None);
        var entry = Assert.Single(recorded, item => item.Category == "filesystem.import.failed");
        Assert.Contains("Corrupt container", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1166: "The.Twilight.Zone.2019.S01E09..." grabbed for the 1959 show is the 2019 reboot. Whatever got it here,
    /// the import refuses it (409), imports nothing, blocklists the release and searches for the show again.
    /// </summary>
    [Fact]
    public async Task A_file_for_the_wrong_year_of_a_show_is_refused_blocklisted_and_searched_again()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        const string releaseName = "The.Twilight.Zone.2019.S01E09.1080p.WEB.x264-worldmkv";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("The Twilight Zone", 1959, null), CancellationToken.None);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", series.Id, releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: seriesRepository, downloadDispatchesRepository: dispatches,
            blockedReleases: blockedReleases, jobScheduler: jobStore);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "tv", series.Title, series.StartYear, [], [], null, null, SeriesId: series.Id),
                "copy", false, true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("(2019), not (1959)", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.mkv", SearchOption.AllDirectories));

        Assert.Contains(BlockedReleaseKeys.For(releaseName, "TestIndexer"), await blockedReleases.ListKeysAsync(CancellationToken.None));
        var jobs = await jobStore.ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.JobType == LibrarySearchJobTypes.For("tv") &&
            job.PayloadJson != null &&
            job.PayloadJson.Contains($"\"targetEntityId\":\"{series.Id}\"", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #1173: "The Simpsons S38E02 The Childrens Book Job" is S38E01's title. Imported as S38E02 it is refused with a
    /// reason naming the other episode, blocklisted, and S38E02 is searched for again.
    /// </summary>
    [Fact]
    public async Task A_file_carrying_another_episodes_title_is_refused_and_the_reason_names_that_episode()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        Directory.CreateDirectory(seriesRootPath);
        const string releaseName = "The Simpsons S38E02 The Childrens Book Job 1080p DSNP WEB-DL DDP5 1 H 264-FLUX";
        var sourcePath = Path.Combine(downloadsPath, releaseName + ".mkv");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, seriesRootPath);
        await CreateTvLibraryAsync(libraries, seriesRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await seriesRepository.AddAsync(new CreateSeriesRequest("The Simpsons", 1989, "tt0096697"), CancellationToken.None);
        await seriesRepository.SyncEpisodeCatalogueAsync(
            series.Id,
            [
                new CatalogueEpisodeItem(38, 1, "The Children's Book Job", null, timeProvider.GetUtcNow().AddDays(-30), AbsoluteNumber: 808),
                new CatalogueEpisodeItem(38, 2, "Diary of a Chimpy Kid", null, timeProvider.GetUtcNow().AddDays(-23), AbsoluteNumber: 809)
            ],
            "provider",
            CancellationToken.None);
        var detail = await seriesRepository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
        var episode = detail!.Episodes.Single(item => item.SeasonNumber == 38 && item.EpisodeNumber == 2);

        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "episode", episode.EpisodeId, releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        var service = CreateService(
            storage, timeProvider, platform, libraries, new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            seriesCatalogRepository: seriesRepository, downloadDispatchesRepository: dispatches,
            blockedReleases: blockedReleases, jobScheduler: jobStore);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                new ImportPreviewRequest(sourcePath, null, "tv", series.Title, series.StartYear, [], [], null, null, SeriesId: series.Id),
                "copy", false, true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("S38E01's \"The Children's Book Job\"", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(seriesRootPath, "*.mkv", SearchOption.AllDirectories));
        Assert.Contains(BlockedReleaseKeys.For(releaseName, "TestIndexer"), await blockedReleases.ListKeysAsync(CancellationToken.None));
        var jobs = await jobStore.ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.PayloadJson != null &&
            job.PayloadJson.Contains($"\"targetEntityId\":\"{episode.EpisodeId}\"", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #954: a broken file blocklisted at import (ImportFailurePolicy.
    /// MediaProbeRejected => Immediately) is a bad release Deluno already
    /// knows will fail the same way again, so the movie it was for is
    /// searched again at once rather than waiting for the library's next
    /// scheduled missing/upgrade pass. Activity says so in plain words.
    /// </summary>
    [Fact]
    public async Task A_broken_file_blocklisted_at_import_queues_a_replacement_search_at_once()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var probe = new RejectingProbeService("Corrupt container; no readable video or audio.");
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        const string releaseName = "Arrival.2016.WEB.1080p-DELUNO";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies, mediaProbeService: probe,
            downloadDispatchesRepository: dispatches, blockedReleases: blockedReleases, jobScheduler: jobStore);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        var blockedKeys = await blockedReleases.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For(releaseName, "TestIndexer"), blockedKeys);

        var jobs = await jobStore.ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.JobType == LibrarySearchJobTypes.For("movies") &&
            job.PayloadJson != null &&
            job.PayloadJson.Contains("\"targetEntityId\":\"movie-arrival\"", StringComparison.OrdinalIgnoreCase));

        var activity = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var recorded = await activity.ListActivityAsync(10, null, null, CancellationToken.None);
        var entry = Assert.Single(recorded, item => item.Category == "release.blocked");
        Assert.Contains("replacement search", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #954: a setup problem, not a fact about the release, never blocklists
    /// and never queues a search — a fresh copy of the same file would fail
    /// the same way until the setup problem is fixed, so searching again
    /// would only waste an indexer query on the same outcome.
    /// </summary>
    [Fact]
    public async Task A_setup_problem_failure_does_not_queue_a_replacement_search()
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
        // #765: below the configured minimum free space -- an environment
        // problem, not a fact about the release (ImportFailurePolicy.
        // InsufficientFreeSpace => BlockDecision.Never).
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath, minimumFreeSpaceMb: 500);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        const string releaseName = "Arrival.2016.WEB.1080p-DELUNO";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival-setup", releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        // 100MB free, below the 500MB floor configured above.
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies, freeSpaceBytesProbe: _ => 100L * 1024 * 1024,
            downloadDispatchesRepository: dispatches, blockedReleases: blockedReleases, jobScheduler: jobStore);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(await blockedReleases.ListKeysAsync(CancellationToken.None));

        var jobs = await jobStore.ListAsync(200, CancellationToken.None);
        Assert.DoesNotContain(jobs, job =>
            job.PayloadJson != null &&
            job.PayloadJson.Contains("\"targetEntityId\":\"movie-arrival-setup\"", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The probe as the real service now answers an unreadable file: plain words, with ffmpeg's own text as the detail.</summary>
    private sealed class UnreadableVideoProbeService(string raw) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(FfprobeMediaProbeService.Failed(raw));
    }

    /// <summary>
    /// A file ffprobe cannot read is told in plain words in Activity and on the download, and ffmpeg's own text
    /// (with its file path) is kept in the activity details only.
    /// </summary>
    [Fact]
    public async Task An_unreadable_file_is_told_in_plain_words_with_ffmpegs_text_in_the_details_only()
    {
        const string raw = "[matroska,webm @ 000001b280590a00] EBML header parsing failed: Invalid data found when processing input";
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, "Arrival.2016.WEB.1080p.mkv");
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.WEB.1080p-DELUNO", "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies, mediaProbeService: new UnreadableVideoProbeService(raw),
            downloadDispatchesRepository: dispatches, blockedReleases: blockedReleases, jobScheduler: jobStore);

        var result = await service.ExecuteAsync(
            new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: sourcePath, FileName: null, MediaType: "movies",
                    Title: "Arrival", Year: 2016, Genres: null, Tags: null,
                    Studio: null, OriginalLanguage: null),
                TransferMode: "copy", Overwrite: false, AllowCopyFallback: true, DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("EBML", result.Message ?? string.Empty);

        var recorded = await jobStore.ListActivityAsync(20, null, null, CancellationToken.None);
        var failed = Assert.Single(recorded, item => item.Category == "filesystem.import.failed");
        Assert.Contains("can't be read as video", failed.Message);
        Assert.DoesNotContain("EBML", failed.Message);
        Assert.DoesNotContain("Invalid data", failed.Message);
        Assert.Contains("EBML header parsing failed", failed.DetailsJson);
    }

    [Fact]
    public void Ffprobes_own_words_for_a_bad_or_unreachable_file_are_never_the_message()
    {
        const string raw = @"[matroska,webm @ 000001b280590a00] EBML header parsing failed C:\Weir\Ready\Movies\x.mkv: Invalid data found when processing input";
        var failed = FfprobeMediaProbeService.Failed(raw);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(ProbeFailureWords.FailedMessage, failed.Message);
        Assert.Equal(raw, failed.Detail);

        var unreadable = FfprobeMediaProbeService.Unreadable("x.mkv: Permission denied");
        Assert.Equal("unreadable", unreadable.Status);
        Assert.DoesNotContain("Permission denied", unreadable.Message);
        Assert.Equal("x.mkv: Permission denied", unreadable.Detail);
    }

    private sealed class RejectingProbeService(string message) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo("failed", "test", message, null, null, null, [], [], []));
    }

    /// <summary>#1013: a file Deluno could not read to check -- not judged bad, just unreadable at that moment.</summary>
    private sealed class UnreadableProbeService : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo("unreadable", "test", "Deluno could not read this file to check it.", null, null, null, [], [], []));
    }

    private sealed record ForceImportScene(
        TestStorage Storage,
        ImportPipelineService Service,
        string SourcePath,
        string MovieRoot,
        SqliteJobStore JobStore) : IDisposable
    {
        public void Dispose() => Storage.Dispose();

        public Task<ImportPipelineResult> ImportAsync(string dispatchId, bool overwrite = false)
            => Service.ExecuteAsync(
                new ImportExecuteRequest(
                    new ImportPreviewRequest(SourcePath, null, "movies", "Silent Hill", 2020, [], [], null, null),
                    "copy",
                    Overwrite: overwrite,
                    AllowCopyFallback: true,
                    DispatchId: dispatchId),
                CancellationToken.None);

        public int ImportedFileCount => Directory.Exists(MovieRoot)
            ? Directory.EnumerateFiles(MovieRoot, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static async Task<ForceImportScene> ForceImportSceneAsync(
        IMediaProbeService probe, bool forceImport, int maxTries = 3,
        string fileName = "Silent.Hill.2020.1080p.WEB-DL.x264-DELUNO.mkv", long sizeBytes = 120L * 1024 * 1024)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        await InitializeAllAsync(storage, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(movieRootPath);
        var sourcePath = Path.Combine(downloadsPath, fileName);
        await using (var stream = new FileStream(sourcePath, FileMode.Create, FileAccess.Write))
        {
            // Big enough to pass the size floor Force Import keeps for a file it could not read (sparse where the file system allows).
            await stream.WriteAsync(Enumerable.Repeat((byte)0x21, 4096).ToArray());
            stream.SetLength(sizeBytes);
        }

        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);
        await platform.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                await platform.GetAsync(CancellationToken.None),
                new PatchPlatformSettingsRequest(ForceImportEnabled: forceImport, ForceImportMaxTries: maxTries)),
            CancellationToken.None);
        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = CreateService(
            storage, timeProvider, platform, libraries, movies,
            mediaProbeService: probe,
            forceImportAttempts: new SqliteForceImportAttemptsRepository(storage.Factory, timeProvider));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        return new ForceImportScene(storage, service, sourcePath, movieRootPath, jobStore);
    }

    /// <summary>#1013: with Force Import off, an unreadable file stays stopped however often it is tried.</summary>
    [Fact]
    public async Task Force_import_is_off_by_default_so_an_unreadable_file_stays_stopped()
    {
        using var scene = await ForceImportSceneAsync(new UnreadableProbeService(), forceImport: false);

        Assert.False((await scene.ImportAsync("dispatch-1")).Succeeded);
        Assert.False((await scene.ImportAsync("dispatch-1")).Succeeded);
        Assert.False((await scene.ImportAsync("dispatch-1")).Succeeded);
        Assert.Equal(0, scene.ImportedFileCount);
    }

    /// <summary>
    /// #1013: the first stop is left to the ordinary retry; the next one is
    /// forced, imported, and said out loud in Activity in plain words.
    /// </summary>
    [Fact]
    public async Task Force_import_imports_past_an_unreadable_file_after_the_first_stop_and_says_so_in_Activity()
    {
        using var scene = await ForceImportSceneAsync(new UnreadableProbeService(), forceImport: true);

        var first = await scene.ImportAsync("dispatch-1");
        Assert.False(first.Succeeded);
        Assert.Equal(0, scene.ImportedFileCount);

        var second = await scene.ImportAsync("dispatch-1");
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(1, scene.ImportedFileCount);

        var activity = await scene.JobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        var forced = Assert.Single(activity, entry => entry.Category == "filesystem.import.forced");
        Assert.Contains("could not read the file to check it", forced.Message, StringComparison.Ordinal);
        Assert.Contains("forced try 1 of 3", forced.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1013 -- the safety guard. A file Deluno read and found wrong is never
    /// forced, however many tries are allowed and however often it is tried.
    /// </summary>
    [Fact]
    public async Task Force_import_refuses_a_file_Deluno_read_and_rejected()
    {
        using var scene = await ForceImportSceneAsync(new RejectingProbeService("Corrupt container; no readable video or audio."), forceImport: true, maxTries: 25);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False((await scene.ImportAsync("dispatch-1")).Succeeded);
        }

        Assert.Equal(0, scene.ImportedFileCount);
        var activity = await scene.JobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.DoesNotContain(activity, entry => entry.Category == "filesystem.import.forced");
    }

    /// <summary>
    /// #1072: an unreadable file never got the no-video and likely-sample checks, so Force Import keeps what
    /// it can tell without reading: a sample-looking name, or a file too small to be a feature or episode.
    /// </summary>
    [Theory]
    [InlineData("Silent.Hill.2020.1080p.sample.mkv", 120L * 1024 * 1024)]
    [InlineData("Silent.Hill.2020.1080p.WEB-DL.x264-DELUNO.mkv", 30L * 1024 * 1024)]
    public async Task Force_import_never_imports_an_unreadable_file_that_looks_like_a_sample(string fileName, long sizeBytes)
    {
        using var scene = await ForceImportSceneAsync(new UnreadableProbeService(), forceImport: true, fileName: fileName, sizeBytes: sizeBytes);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.False((await scene.ImportAsync("dispatch-1")).Succeeded);
        }

        Assert.Equal(0, scene.ImportedFileCount);
        var activity = await scene.JobStore.ListActivityAsync(50, null, null, CancellationToken.None);
        Assert.DoesNotContain(activity, entry => entry.Category == "filesystem.import.forced");
    }

    [Theory]
    [InlineData("/tv/Show/Show.S01E01.720p.mkv", 500L * 1024 * 1024, false)]
    [InlineData("/dl/Movie.2020/Sample/movie-sample.mkv", 500L * 1024 * 1024, true)]
    [InlineData("/dl/Movie.2020/Movie.2020-trailer.mkv", 500L * 1024 * 1024, true)]
    [InlineData("/dl/Movie.2020/Movie.2020.mkv", null, true)]
    [InlineData("/dl/Movie.2020/Movie.2020.mkv", 1024L, true)]
    public void A_file_that_could_not_be_read_is_told_to_be_a_sample_only_by_what_can_be_seen_outside_it(string path, long? size, bool sample)
        => Assert.Equal(sample, ImportPipelineService.LooksLikeASampleWithoutProbing(path, size));

    [Theory]
    [InlineData("/dl/Trailer.Park.Boys.S01E01.720p/Trailer.Park.Boys.S01E01.720p.mkv", "Trailer Park Boys", false)]
    [InlineData("/dl/Trailer.Park.Boys.S01E01.720p/Trailer.Park.Boys.S01E01.720p-trailer.mkv", "Trailer Park Boys", true)]
    [InlineData("/dl/Movie.2020/movie-sample.mkv", "Movie", true)]
    [InlineData("/dl/Sample.Movie.2020/Sample.Movie.2020.mkv", "Sample Movie", false)]
    public void A_sample_or_trailer_word_that_is_part_of_the_title_does_not_make_a_file_a_sample(string path, string title, bool sample)
        => Assert.Equal(sample, ImportPipelineService.LooksLikeASampleWithoutProbing(path, 500L * 1024 * 1024, 100, title));

    [Theory]
    [InlineData(30, 40L * 1024 * 1024, false)]
    [InlineData(30, 20L * 1024 * 1024, true)]
    [InlineData(500, 200L * 1024 * 1024, true)]
    public void The_smallest_file_that_can_be_forced_is_the_configured_size(int minimumMegabytes, long size, bool sample)
        => Assert.Equal(sample, ImportPipelineService.LooksLikeASampleWithoutProbing("/tv/Show/Show.S01E01.720p.mkv", size, minimumMegabytes));

    /// <summary>#1072: ffprobe's own operating-system errors are "could not be reached", never "bad release".</summary>
    [Theory]
    [InlineData("/mnt/nas/a.mkv: Input/output error", true)]
    [InlineData("a.mkv: Permission denied", true)]
    [InlineData("The process cannot access the file because it is being used by another process.", true)]
    [InlineData("a.mkv: Invalid data found when processing input", false)]
    [InlineData("moov atom not found", false)]
    [InlineData("", false)]
    public void Probe_errors_that_are_about_reaching_the_file_are_told_from_errors_about_the_file(string error, bool unreachable)
        => Assert.Equal(unreachable, FfprobeMediaProbeService.LooksLikeTheFileCouldNotBeReached(error));

    /// <summary>#1013: it fills a gap only. It never puts an unchecked file on top of one already there.</summary>
    [Fact]
    public async Task Force_import_never_replaces_a_file_that_is_already_in_the_library()
    {
        using var scene = await ForceImportSceneAsync(new UnreadableProbeService(), forceImport: true);

        Assert.False((await scene.ImportAsync("dispatch-1", overwrite: true)).Succeeded);
        Assert.False((await scene.ImportAsync("dispatch-1", overwrite: true)).Succeeded);
        Assert.Equal(0, scene.ImportedFileCount);
    }

    /// <summary>#979: an install with no ffprobe on the path -- unavailable, not failed, so it never blocks an import, only removes the probe's naming evidence.</summary>
    private sealed class UnavailableProbeService : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo("unavailable", "test", "ffprobe is unavailable.", null, null, null, [], [], []));
    }

    private static async Task<ProcessedScene> ProcessedSceneAsync(
        double processedSeconds,
        int processedAudioTracks = 2,
        int sourceAudioTracks = 2,
        bool processedHasVideo = true,
        int processedWidth = 1920,
        int processedHeight = 1080,
        string sourceAudioLanguage = "eng",
        string processedAudioLanguage = "eng",
        // A #769 code ("ja", "en", ...) the library's profile requires, or null for no language opinion.
        string? requireLanguage = null)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-23T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);

        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var movieRootPath = Path.Combine(storage.DataRoot, "movies");
        var refinedPath = Path.Combine(storage.DataRoot, "refined");
        Directory.CreateDirectory(movieRootPath);

        // The download the processor was given: a folder, as they usually are.
        var download = Directory.CreateDirectory(Path.Combine(downloadsPath, "Interstellar.2014.WEB.1080p-DELUNO")).FullName;
        var sourceFile = Path.Combine(download, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(sourceFile, new byte[8192]);

        // What it handed back.
        var processed = Path.Combine(Directory.CreateDirectory(Path.Combine(refinedPath, "Interstellar.2014.WEB.1080p-DELUNO")).FullName, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        await File.WriteAllBytesAsync(processed, new byte[4096]);
        File.SetLastWriteTimeUtc(processed, DateTime.UtcNow.AddMinutes(-1));

        var platform = CreatePlatformRepository(storage, timeProvider);
        var libraries = CreateLibrariesRepository(storage, timeProvider);
        await SaveSettingsAsync(platform, movieRootPath, downloadsPath);

        IQualityRepository? qualityRepository = null;
        string? qualityProfileId = null;
        if (requireLanguage is not null)
        {
            var quality = new SqliteQualityRepository(storage.Factory, timeProvider);
            var profile = await quality.CreateQualityProfileAsync(
                new CreateQualityProfileRequest(
                    "Requires a language", "movies", "WEB 1080p", "WEB 1080p", null,
                    UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
                    LanguagePreference: new ProfileLanguagePreference([requireLanguage], RequireLanguage: true)),
                CancellationToken.None);
            qualityRepository = quality;
            qualityProfileId = profile.Id;
        }

        await CreateMovieLibraryAsync(libraries, movieRootPath, downloadsPath, qualityProfileId: qualityProfileId);

        var probe = new ProcessedProbeService(sourceFile, processed, processedSeconds, processedAudioTracks, sourceAudioTracks, processedHasVideo, processedWidth, processedHeight, sourceAudioLanguage, processedAudioLanguage);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var service = new ImportPipelineService(
            platform, libraries,
            movies,
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            probe,
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            null,
            new NullImportResolutionsRepository(),
            new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider),
            new SqliteConnectionsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage)),
            NullLogger<ImportPipelineService>.Instance,
            null,
            qualityRepository: qualityRepository);

        var libraryId = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None)).Id;
        var destination = Path.Combine(movieRootPath, "Interstellar (2014)", "Interstellar (2014).mkv");
        return new ProcessedScene(
            () => service.ExecuteAsync(
                new ImportExecuteRequest(
                    Preview: new ImportPreviewRequest(
                        SourcePath: processed, FileName: null, MediaType: "movies",
                        Title: "Interstellar", Year: 2014, Genres: null, Tags: null,
                        Studio: null, OriginalLanguage: null, LibraryId: libraryId),
                    TransferMode: "copy", Overwrite: false, AllowCopyFallback: true,
                    ProcessedFromPath: download),
                CancellationToken.None),
            destination,
            processed,
            movies);
    }

    /// <summary>Answers for two named files, so a comparison has two sides.</summary>
    private sealed class ProcessedProbeService(
        string sourceFile,
        string processedFile,
        double processedSeconds,
        int processedAudioTracks,
        int sourceAudioTracks,
        bool processedHasVideo = true,
        int processedWidth = 1920,
        int processedHeight = 1080,
        string sourceAudioLanguage = "eng",
        string processedAudioLanguage = "eng") : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            var isProcessed = string.Equals(Path.GetFullPath(path), Path.GetFullPath(processedFile), StringComparison.OrdinalIgnoreCase);
            var isSource = string.Equals(Path.GetFullPath(path), Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase);
            if (!isProcessed && !isSource)
            {
                return Task.FromResult(new MediaProbeInfo("unavailable", "test", "Not one of the two files.", null, null, null, [], [], []));
            }

            var width = isProcessed ? processedWidth : 1920;
            var height = isProcessed ? processedHeight : 1080;
            var hasVideo = !isProcessed || processedHasVideo;
            return Task.FromResult(new MediaProbeInfo(
                Status: "succeeded",
                Tool: "test",
                Message: null,
                DurationSeconds: isProcessed ? processedSeconds : 7200,
                Container: "matroska",
                Bitrate: 12_000_000,
                VideoStreams: hasVideo
                    ? [new MediaVideoStreamInfo(0, "h264", "High", width, height, "yuv420p", 23.976, 10_000_000, "eng")]
                    : [],
                AudioStreams: Enumerable.Range(0, isProcessed ? processedAudioTracks : sourceAudioTracks)
                    .Select(index => new MediaAudioStreamInfo(index + 1, "eac3", null, 6, "5.1", 48_000, 640_000, isProcessed ? processedAudioLanguage : sourceAudioLanguage))
                    .ToArray(),
                SubtitleStreams: []));
        }
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
        IQualityRepository? qualityRepository = null,
        IDownloadDispatchesRepository? downloadDispatchesRepository = null,
        IJobScheduler? mediaServerJobScheduler = null,
        IJobScheduler? jobScheduler = null,
        TimeSpan? settleWindow = null,
        IOutboundNotificationService? outboundNotificationService = null,
        INotificationService? notificationService = null,
        IConnectionsRepository? connectionsRepository = null,
        Deluno.Recovery.Services.IDownloadClientActionGateway? downloadClientActions = null,
        Func<string, long?>? freeSpaceBytesProbe = null,
        IBlockedReleaseRepository? blockedReleases = null,
        IImportFailureRuleRepository? failureRules = null,
        Deluno.Connections.Data.ISeerrAcquisitionsRepository? seerrAcquisitions = null,
        Deluno.Connections.Data.ISeerrRepository? seerrRepository = null,
        Deluno.Integrations.Seerr.ISeerrClient? seerrClient = null,
        IMediaTagStore? mediaTagStore = null,
        IForceImportAttemptsRepository? forceImportAttempts = null,
        Deluno.Contracts.IClientHeldFileProbe? clientHeldFiles = null)
        => new(
            platform,
            librariesRepository,
            movies,
            seriesCatalogRepository ?? new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            mediaProbeService ?? new SuccessfulProbeService(),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            outboundNotificationService,
            new NullImportResolutionsRepository(),
            downloadDispatchesRepository,
            connectionsRepository,
            NullLogger<ImportPipelineService>.Instance,
            null,
            null,
            qualityRepository,
            blockedReleases: blockedReleases,
            failureRules: failureRules,
            mediaServerJobScheduler: mediaServerJobScheduler,
            jobScheduler: jobScheduler,
            settleWindow: settleWindow ?? TimeSpan.Zero,
            notificationService: notificationService,
            downloadClientActions: downloadClientActions,
            // #765: a fixed, always-plenty probe by default so file-size
            // fixtures on a real (if nearly full) CI disk never trip the
            // guard by accident; individual tests override it to exercise
            // the guard deliberately.
            freeSpaceBytesProbe: freeSpaceBytesProbe ?? (_ => long.MaxValue),
            seerrAcquisitions: seerrAcquisitions,
            seerrRepository: seerrRepository,
            seerrClient: seerrClient,
            mediaTagStore: mediaTagStore,
            forceImportAttempts: forceImportAttempts,
            clientHeldFiles: clientHeldFiles);

    private sealed class FakeClientHeldFileProbe(bool held) : Deluno.Contracts.IClientHeldFileProbe
    {
        public Task<bool> IsHeldByAnyClientAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(held);
    }

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

    private static ImportRestartRecoveryService CreateRestartRecoveryService(
        ILibrariesRepository librariesRepository,
        IImportPipelineService importPipelineService,
        IActivityFeedRepository activityFeedRepository)
        => new(
            librariesRepository,
            importPipelineService,
            activityFeedRepository,
            NullLogger<ImportRestartRecoveryService>.Instance);

    private static async Task CreateMovieLibraryAsync(
        ILibrariesRepository librariesRepository,
        string movieRootPath,
        string downloadsPath,
        string cleanupMode = "keep-source",
        bool removeEmptySourceFolders = false,
        string? qualityProfileId = null)
    {
        var request = new CreateLibraryRequest(
            Name: "Movies",
            MediaType: "movies",
            Purpose: "Main",
            RootPath: movieRootPath,
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
            SearchIntervalMinutes: 6,
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
                SearchIntervalMinutes: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

    private static async Task SaveSettingsAsync(
        SqlitePlatformSettingsRepository platform,
        string movieRootPath,
        string downloadsPath,
        string? seriesRootPath = null,
        string episodeFileFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
        string movieFileFormat = "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
        bool replaceIllegalCharacters = true,
        string? colonReplacementMode = null,
        // #761: default to mirroring episodeFileFormat, so a caller that only
        // sets the standard episode format keeps naming daily/anime episodes
        // the same way it always did, unless it deliberately asks for
        // dedicated Daily/Anime formats.
        string? dailyEpisodeFileFormat = null,
        string? animeEpisodeFileFormat = null,
        bool setPermissionsOnImport = false,
        string? folderChmod = null,
        string? chownGroup = null,
        string? seasonFolderFormat = null,
        string? specialsFolderFormat = null,
        string? multiEpisodeStyle = null,
        bool renameOnImport = true,
        bool renameFoldersOnImport = false,
        int? minimumFreeSpaceMb = null,
        bool skipFreeSpaceCheck = false)
    {
        await platform.SaveAsync(
            new UpdatePlatformSettingsRequest(
                MovieRootPath: movieRootPath,
                SeriesRootPath: seriesRootPath,
                DownloadsPath: downloadsPath,
                IncompleteDownloadsPath: null,
                AutoStartJobs: false,
                EnableNotifications: false,
                RenameOnImport: renameOnImport,
                UseHardlinks: false,
                CleanupEmptyFolders: false,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: movieFileFormat,
                EpisodeFileFormat: episodeFileFormat,
                HostBindAddress: null,
                HostPort: 5099,
                UrlBase: null,
                RequireAuthentication: true,
                UiTheme: "system",
                DefaultMovieView: "grid",
                DefaultShowView: "grid",
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "broker",
                MetadataBrokerUrl: null,
                MetadataTmdbApiKey: null,
                MetadataOmdbApiKey: null,
                ReleaseNeverGrabPatterns: null,
                ReplaceIllegalCharacters: replaceIllegalCharacters,
                ColonReplacementMode: colonReplacementMode,
                DailyEpisodeFileFormat: dailyEpisodeFileFormat ?? episodeFileFormat,
                AnimeEpisodeFileFormat: animeEpisodeFileFormat ?? episodeFileFormat,
                SetPermissionsOnImport: setPermissionsOnImport,
                FolderChmod: folderChmod,
                ChownGroup: chownGroup,
                SeasonFolderFormat: seasonFolderFormat,
                SpecialsFolderFormat: specialsFolderFormat,
                MultiEpisodeStyle: multiEpisodeStyle,
                RenameFoldersOnImport: renameFoldersOnImport,
                MinimumFreeSpaceMb: minimumFreeSpaceMb,
                SkipFreeSpaceCheck: skipFreeSpaceCheck),
            CancellationToken.None);
    }

    /// <summary>#662: records what would have been queued, without a real job store.</summary>
    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<EnqueueJobRequest> Requests { get; } = [];

        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new JobQueueItem(
                Id: "job-" + Requests.Count,
                JobType: request.JobType,
                Source: request.Source,
                Status: "pending",
                PayloadJson: request.PayloadJson,
                Attempts: 0,
                CreatedUtc: DateTimeOffset.UnixEpoch,
                ScheduledUtc: request.ScheduledUtc ?? DateTimeOffset.UnixEpoch,
                StartedUtc: null,
                CompletedUtc: null,
                LeasedUntilUtc: null,
                WorkerId: null,
                LastError: null,
                RelatedEntityType: request.RelatedEntityType,
                RelatedEntityId: request.RelatedEntityId,
                IdempotencyKey: request.IdempotencyKey,
                DedupeKey: request.DedupeKey,
                MaxAttempts: request.MaxAttempts ?? 1,
                LastAttemptUtc: null,
                NextAttemptUtc: null));
        }
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

    /// <summary>
    /// #889: lets a test make ffprobe fail for exactly one file in a pack,
    /// the way a genuinely corrupt episode would, while every other file
    /// probes cleanly.
    /// </summary>
    private sealed class PerPathProbeService(string failingPath, string failureMessage) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => string.Equals(path, failingPath, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(new MediaProbeInfo(
                    Status: "failed",
                    Tool: "test",
                    Message: failureMessage,
                    DurationSeconds: null,
                    Container: null,
                    Bitrate: null,
                    VideoStreams: [],
                    AudioStreams: [],
                    SubtitleStreams: []))
                : new SuccessfulProbeService().ProbeAsync(path, cancellationToken);
    }

    private sealed class ProbeMustNotRunService : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("ffprobe should not run for an unstable source file.");
    }

    private sealed class RecordingOutboundNotificationService : IOutboundNotificationService
    {
        public List<string> DispatchedCategories { get; } = [];

        /// <summary>#990: the titleTags each dispatch carried, by event category (last write wins for a repeated category).</summary>
        public Dictionary<string, IReadOnlyList<string>?> DispatchedTitleTags { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            DispatchedCategories.Add(eventCategory);
            DispatchedTitleTags[eventCategory] = titleTags;
            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeNotificationService(NotificationPreferences preferences) : INotificationService
    {
        public Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(preferences);

        public Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
