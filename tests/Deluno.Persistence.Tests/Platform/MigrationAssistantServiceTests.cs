using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Movies.Data;
using Deluno.Movies.Migration;
using Deluno.Series.Data;
using Deluno.Series.Migration;
using Deluno.Quality.Data;
using Deluno.Connections.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class MigrationAssistantServiceTests
{
    [Fact]
    public async Task PreviewAsync_maps_radarr_export_without_applying_changes()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var report = await service.PreviewAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.True(report.Valid);
        Assert.Equal("radarr", report.SourceKind);
        Assert.Contains(report.Operations, operation => operation.TargetType == "quality-profile" && operation.Action == "create");
        Assert.Contains(report.Operations, operation => operation.TargetType == "library" && operation.Action == "create");
        Assert.Contains(report.Operations, operation => operation.TargetType == "indexer" && operation.Action == "create");
        Assert.Contains(report.Operations, operation => operation.TargetType == "download-client" && operation.Action == "create");
        Assert.Contains(report.Operations, operation => operation.TargetType == "intake-source" && operation.Action == "create");
        Assert.Equal(2, report.Summary.TitleCount);
        Assert.Equal(1, report.Summary.MonitoredCount);
        Assert.Equal(1, report.Summary.WantedCount);
        Assert.All(report.Operations.SelectMany(operation => operation.Data), pair =>
        {
            if (pair.Key.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("[redacted]", pair.Value);
            }
        });

        var repository = CreateRepository(storage);
        var librariesRepository = CreateLibrariesRepository(storage);
        var libraries = await librariesRepository.ListLibrariesAsync(CancellationToken.None);
        Assert.DoesNotContain(libraries, library => library.RootPath == "/mnt/media/migrated-movies");
        Assert.Empty(await repository.ListMigrationAuditReportsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyAsync_creates_supported_configuration_and_second_preview_skips_duplicates()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var applied = await service.ApplyAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.True(applied.Report.Valid);
        Assert.Contains(applied.Applied, item => item.TargetType == "quality-profile");
        Assert.Contains(applied.Applied, item => item.TargetType == "library");
        Assert.Contains(applied.Applied, item => item.TargetType == "indexer");
        Assert.Contains(applied.Applied, item => item.TargetType == "download-client");

        var repository = CreateRepository(storage);
        var librariesRepository = CreateLibrariesRepository(storage);
        var libraries = await librariesRepository.ListLibrariesAsync(CancellationToken.None);
        var profiles = await CreateQualityRepository(storage).ListQualityProfilesAsync(CancellationToken.None);
        var indexers = await CreateConnectionsRepository(storage).ListIndexersAsync(CancellationToken.None);
        var clients = await CreateConnectionsRepository(storage).ListDownloadClientsAsync(CancellationToken.None);

        Assert.Contains(libraries, library => library.RootPath == "/mnt/media/migrated-movies");
        Assert.Contains(profiles, profile => profile.Name == "Migrated UHD");
        Assert.Contains(indexers, indexer => indexer.BaseUrl == "https://indexer.example/api");
        Assert.Contains(clients, client => client.Host == "qbittorrent");

        Assert.DoesNotContain(applied.Report.Operations.SelectMany(operation => operation.Data.Values), value => value == "secret");

        var audit = Assert.Single(await repository.ListMigrationAuditReportsAsync(10, CancellationToken.None));
        Assert.Equal("radarr", audit.SourceKind);
        Assert.Equal("Radarr test", audit.SourceName);
        Assert.Equal(applied.AuditReportId, audit.Id);
        Assert.Contains(audit.Applied, item => item.TargetType == "library" && item.Result == "created");
        Assert.DoesNotContain(audit.PreflightReport.Operations.SelectMany(operation => operation.Data.Values), value => value == "secret");
        Assert.Contains(audit.ResultReport.Operations, operation => operation.TargetType == "library" && operation.Action == "skip");

        var loadedAudit = await repository.GetMigrationAuditReportAsync(audit.Id, CancellationToken.None);
        Assert.NotNull(loadedAudit);
        Assert.Equal(audit.Id, loadedAudit.Id);
        Assert.Equal(audit.AppliedUtc, loadedAudit.AppliedUtc);
        Assert.Equal(audit.PreflightReport.SourceName, loadedAudit.PreflightReport.SourceName);
        Assert.Equal(audit.ResultReport.Summary, loadedAudit.ResultReport.Summary);
        Assert.Equal(audit.Applied, loadedAudit.Applied);

        var secondPreview = await service.PreviewAsync(CreateRadarrRequest(), CancellationToken.None);
        Assert.Contains(secondPreview.Operations, operation => operation.TargetType == "library" && operation.Action == "skip");
        Assert.Contains(secondPreview.Operations, operation => operation.TargetType == "quality-profile" && operation.Action == "skip");
        Assert.DoesNotContain(secondPreview.Operations, operation => operation.TargetType == "library" && operation.Action == "create");
    }

    [Fact]
    public async Task ApplyAsync_applies_only_the_explicitly_selected_safe_operations()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);
        var request = CreateRadarrRequest();
        var preview = await service.PreviewAsync(request, CancellationToken.None);
        var selectedIndexer = Assert.Single(preview.Operations, operation => operation.TargetType == "indexer" && operation.Action == "create");

        var applied = await service.ApplyAsync(
            request with { SelectedOperationIds = [selectedIndexer.Id] },
            CancellationToken.None);

        Assert.Single(applied.Applied);
        Assert.Equal("indexer", applied.Applied[0].TargetType);

        var repository = CreateRepository(storage);
        var librariesRepository = CreateLibrariesRepository(storage);
        Assert.Single(await CreateConnectionsRepository(storage).ListIndexersAsync(CancellationToken.None));
        Assert.Empty(await librariesRepository.ListLibrariesAsync(CancellationToken.None));
        Assert.DoesNotContain(await CreateQualityRepository(storage).ListQualityProfilesAsync(CancellationToken.None), profile => profile.Name == "Migrated UHD");
        Assert.Empty(await CreateConnectionsRepository(storage).ListDownloadClientsAsync(CancellationToken.None));

        var audit = Assert.Single(await repository.ListMigrationAuditReportsAsync(10, CancellationToken.None));
        Assert.Single(audit.Applied);
        Assert.Equal(selectedIndexer.Id, audit.Applied[0].OperationId);
    }

    [Fact]
    public async Task PreviewAsync_reports_invalid_payload_without_throwing()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var report = await service.PreviewAsync(
            new MigrationImportRequest("sonarr", "Broken export", "{ not-json"),
            CancellationToken.None);

        Assert.False(report.Valid);
        Assert.NotEmpty(report.Errors);
        Assert.Empty(report.Operations);
    }

    [Fact]
    public async Task PreviewAsync_reports_same_name_different_configuration_as_conflict()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);
        var librariesRepository = CreateLibrariesRepository(storage);
        await librariesRepository.CreateLibraryAsync(
            new CreateLibraryRequest(
                "Conflicting Movies",
                "movies",
                "Existing",
                "/media/existing",
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
                SearchIntervalHours: 6,
                RetryDelayHours: 3,
                MaxItemsPerRun: 10),
            CancellationToken.None);

        var report = await service.PreviewAsync(
            new MigrationImportRequest(
                "radarr",
                "Radarr conflict",
                """
                {
                  "rootFolders": [
                    { "name": "Conflicting Movies", "path": "/media/incoming" }
                  ]
                }
                """),
            CancellationToken.None);

        Assert.Contains(report.Operations, operation =>
            operation.TargetType == "library" &&
            operation.Name == "Conflicting Movies" &&
            operation.Action == "conflict" &&
            !operation.CanApply);
    }

    [Fact]
    public async Task PreviewAsync_maps_arr_tmdb_and_imdb_list_ids_without_guessing_other_list_types()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var report = await service.PreviewAsync(
            new MigrationImportRequest("radarr", "Arr list IDs", """
                {
                  "importLists": [
                    { "name": "TMDb favourites", "implementation": "TMDb", "fields": [{ "name": "listId", "value": "12345" }] },
                    { "name": "IMDb watchlist", "implementation": "IMDb", "fields": [{ "name": "listId", "value": "ls012345678" }] },
                    { "name": "Unknown custom list", "implementation": "Custom", "fields": [{ "name": "listId", "value": "not-a-url" }] }
                  ]
                }
                """),
            CancellationToken.None);

        var tmdb = Assert.Single(report.Operations, operation => operation.Name == "TMDb favourites");
        Assert.Equal("create", tmdb.Action);
        Assert.Equal("tmdb", tmdb.Data["provider"]);
        Assert.Equal("12345", tmdb.Data["feedUrl"]);

        var imdb = Assert.Single(report.Operations, operation => operation.Name == "IMDb watchlist");
        Assert.Equal("create", imdb.Action);
        Assert.Equal("imdb", imdb.Data["provider"]);
        Assert.Equal("ls012345678", imdb.Data["feedUrl"]);

        var unsupported = Assert.Single(report.Operations, operation => operation.Name == "Unknown custom list");
        Assert.Equal("unsupported", unsupported.Action);
        Assert.False(unsupported.CanApply);
    }

    [Fact]
    public async Task ApplyAsync_imports_deduplicated_movie_and_series_catalog_records_when_library_mapping_is_unambiguous()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var migrationRepository = new SqliteMigrationAuditRepository(storage.Factory, timeProvider);
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var service = new MigrationAssistantService(migrationRepository, librariesRepository, CreateQualityRepository(storage), CreateConnectionsRepository(storage), CreateIntakeRepository(storage),
        [
            new MovieMigrationCatalogImporter(movies),
            new SeriesMigrationCatalogImporter(series)
        ]);
        var request = new MigrationImportRequest("custom", "Combined stack", """
            {
              "radarr": {
                "rootFolders": [{ "path": "/media/movies" }],
                "movies": [{ "title": "Dune Part Two", "year": 2024, "imdbId": "tt15239678", "monitored": true, "hasFile": false }]
              },
              "sonarr": {
                "rootFolders": [{ "path": "/media/tv" }],
                "series": [{ "title": "Severance", "year": 2022, "imdbId": "tt11280740", "monitored": true, "hasFile": true }]
              }
            }
            """);

        var applied = await service.ApplyAsync(request, CancellationToken.None);

        var importedMovies = await movies.ListAsync(CancellationToken.None);
        Assert.True(importedMovies.Count == 1, $"Applied: {string.Join(", ", applied.Applied.Select(item => $"{item.TargetType}:{item.Result}"))}; warnings: {string.Join(" | ", applied.Report.Warnings)}");
        var movie = Assert.Single(importedMovies);
        var show = Assert.Single(await series.ListAsync(CancellationToken.None));
        Assert.True(movie.Monitored);
        Assert.True(show.Monitored);
        Assert.Contains(applied.Applied, item => item.TargetType == "movie" && item.Result == "created");
        Assert.Contains(applied.Applied, item => item.TargetType == "series" && item.Result == "created");

        var libraries = await librariesRepository.ListLibrariesAsync(CancellationToken.None);
        var movieLibrary = libraries.Single(item => item.MediaType == "movies");
        var tvLibrary = libraries.Single(item => item.MediaType == "tv");
        Assert.Equal("missing", (await movies.GetMovieWantedStateAsync(movie.Id, movieLibrary.Id, CancellationToken.None))!.WantedStatus);
        Assert.Equal("waiting", (await series.GetSeriesWantedStateAsync(show.Id, tvLibrary.Id, CancellationToken.None))!.WantedStatus);

        var repeated = await service.ApplyAsync(request, CancellationToken.None);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
        Assert.Single(await series.ListAsync(CancellationToken.None));
        Assert.Contains(repeated.Applied, item => item.TargetType == "movie" && item.Result == "skipped");
        Assert.Contains(repeated.Applied, item => item.TargetType == "series" && item.Result == "skipped");
    }

    [Fact]
    public async Task ApplyAsync_records_a_recoverable_audit_when_a_catalog_stage_fails_and_a_retry_skips_saved_configuration()
    {
        using var storage = TestStorage.Create();
        await CreateServiceAsync(storage);
        var repository = CreateRepository(storage);
        var librariesRepository = CreateLibrariesRepository(storage);
        var failingService = new MigrationAssistantService(repository, librariesRepository, CreateQualityRepository(storage), CreateConnectionsRepository(storage), CreateIntakeRepository(storage), [new ThrowingCatalogImporter()]);

        var failed = await failingService.ApplyAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.Contains(failed.Applied, item => item.Result == "failed" && item.TargetType == "movies");
        Assert.Contains(failed.Report.Errors, error => error.Contains("stopped while importing movies catalogue", StringComparison.OrdinalIgnoreCase));
        var failedAudit = Assert.Single(await repository.ListMigrationAuditReportsAsync(10, CancellationToken.None));
        Assert.Contains(failedAudit.Applied, item => item.Result == "failed");
        Assert.Contains(failedAudit.ResultReport.Errors, error => error.Contains("retry", StringComparison.OrdinalIgnoreCase));

        var retry = await new MigrationAssistantService(repository, librariesRepository, CreateQualityRepository(storage), CreateConnectionsRepository(storage), CreateIntakeRepository(storage)).ApplyAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.Empty(retry.Report.Errors);
        Assert.Empty(retry.Applied);
        Assert.Equal(2, (await repository.ListMigrationAuditReportsAsync(10, CancellationToken.None)).Count);
    }

    /// <summary>
    /// A tracker the old app called private polices sharing, and #288 is what
    /// Deluno does about that. Carrying the label across a migration but not
    /// the obligation would let Deluno reclaim after three days on a site that
    /// bans for it — and nobody migrating from Prowlarr should have to know
    /// that and go back through every source by hand.
    ///
    /// This is the only job the privacy field has. Nothing else reads it.
    /// </summary>
    [Theory]
    [InlineData("private", true)]
    // Prowlarr writes it camel-cased, and semi-private trackers police sharing
    // exactly the same way.
    [InlineData("semiPrivate", true)]
    [InlineData("public", false)]
    // An export that does not say gets no claim either way. This used to
    // default to "private", which was a harmless mislabel while nothing read it
    // and would now put a strict rule on an open index.
    [InlineData(null, false)]
    public async Task ApplyAsync_gives_an_imported_private_tracker_the_strict_sharing_rule(string? privacy, bool expectStrict)
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        await service.ApplyAsync(CreateRadarrRequest(privacy), CancellationToken.None);

        var indexer = Assert.Single(await CreateConnectionsRepository(storage).ListIndexersAsync(CancellationToken.None));

        if (expectStrict)
        {
            Assert.Equal(SharingPolicy.Strict.Mode, indexer.SharingMode);
            Assert.Equal(SharingPolicy.Strict.ForHours, indexer.SharingForHours);
            Assert.Equal(SharingPolicy.Strict.UntilRatio, indexer.SharingUntilRatio);
            Assert.Equal(SharingPolicy.Strict.StuckAction, indexer.SharingStuckAction);
            Assert.Equal(SharingPolicy.Strict.StuckAfterDays, indexer.SharingStuckAfterDays);
        }
        else
        {
            // Every field null means "inherit the global rule", which is what a
            // source Deluno knows nothing special about should do.
            Assert.Null(indexer.SharingMode);
            Assert.Null(indexer.SharingForHours);
            Assert.Null(indexer.SharingUntilRatio);
            Assert.Null(indexer.SharingStuckAction);
            Assert.Null(indexer.SharingStuckAfterDays);
        }
    }

    private static MigrationImportRequest CreateRadarrRequest(string? indexerPrivacy = null)
    {
        var privacyLine = indexerPrivacy is null ? string.Empty : $"\"privacy\": \"{indexerPrivacy}\",";
        var payload =
            $$"""
            {
              "qualityProfiles": [
                {
                  "id": 80,
                  "name": "Migrated UHD",
                  "cutoff": 3,
                  "items": [
                    { "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } },
                    { "allowed": true, "quality": { "id": 3, "name": "Remux 2160p" } }
                  ]
                }
              ],
              "rootFolders": [
                { "path": "/mnt/media/migrated-movies" }
              ],
              "indexers": [
                {
                  "name": "Migrated Torrent",
                  "protocol": "torrent",
                  {{privacyLine}}
                  "baseUrl": "https://indexer.example/api",
                  "apiKey": "secret",
                  "categories": [2000, 2010],
                  "enable": true
                }
              ],
              "downloadClients": [
                {
                  "name": "Migrated qBittorrent",
                  "implementation": "QBittorrent",
                  "host": "qbittorrent",
                  "port": 8080,
                  "fields": [
                    { "name": "category", "value": "movies" },
                    { "name": "username", "value": "deluno" },
                    { "name": "password", "value": "secret" }
                  ],
                  "enable": true
                }
              ],
              "importLists": [
                {
                  "name": "IMDb Watchlist",
                  "implementation": "IMDb",
                  "fields": [
                    { "name": "listUrl", "value": "https://www.imdb.com/list/ls123/" }
                  ]
                }
              ],
              "movies": [
                { "title": "Dune Part Two", "monitored": true, "hasFile": true },
                { "title": "Anora", "monitored": false, "hasFile": false }
              ]
            }
            """;

        return new MigrationImportRequest("radarr", "Radarr test", payload);
    }

    private static async Task<MigrationAssistantService> CreateServiceAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new MigrationAssistantService(CreateRepository(storage), CreateLibrariesRepository(storage), CreateQualityRepository(storage), CreateConnectionsRepository(storage), CreateIntakeRepository(storage));
    }

    private static SqliteIntakeRepository CreateIntakeRepository(TestStorage storage)
    {
        return new SqliteIntakeRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")));
    }

    private static SqliteMigrationAuditRepository CreateRepository(TestStorage storage)
    {
        return new SqliteMigrationAuditRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")));
    }

    private static SqliteLibrariesRepository CreateLibrariesRepository(TestStorage storage)
    {
        return new SqliteLibrariesRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")));
    }

    private static SqliteQualityRepository CreateQualityRepository(TestStorage storage)
    {
        return new SqliteQualityRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")));
    }

    private static SqliteConnectionsRepository CreateConnectionsRepository(TestStorage storage)
    {
        return new SqliteConnectionsRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")),
            TestSecretProtection.Create(storage));
    }

    private sealed class ThrowingCatalogImporter : IMigrationCatalogImporter
    {
        public string MediaType => "movies";

        public Task<MigrationCatalogImportResult> ImportAsync(
            MigrationCatalogImportRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Synthetic catalog failure.");
    }
}
