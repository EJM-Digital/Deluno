using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
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

        var repository = CreateRepository(storage);
        var libraries = await repository.ListLibrariesAsync(CancellationToken.None);
        Assert.DoesNotContain(libraries, library => library.RootPath == "/mnt/media/migrated-movies");
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
        var libraries = await repository.ListLibrariesAsync(CancellationToken.None);
        var profiles = await repository.ListQualityProfilesAsync(CancellationToken.None);
        var indexers = await repository.ListIndexersAsync(CancellationToken.None);
        var clients = await repository.ListDownloadClientsAsync(CancellationToken.None);

        Assert.Contains(libraries, library => library.RootPath == "/mnt/media/migrated-movies");
        Assert.Contains(profiles, profile => profile.Name == "Migrated UHD");
        Assert.Contains(indexers, indexer => indexer.BaseUrl == "https://indexer.example/api");
        Assert.Contains(clients, client => client.Host == "qbittorrent");

        var secondPreview = await service.PreviewAsync(CreateRadarrRequest(), CancellationToken.None);
        Assert.Contains(secondPreview.Operations, operation => operation.TargetType == "library" && operation.Action == "skip");
        Assert.Contains(secondPreview.Operations, operation => operation.TargetType == "quality-profile" && operation.Action == "skip");
        Assert.DoesNotContain(secondPreview.Operations, operation => operation.TargetType == "library" && operation.Action == "create");
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
        var repository = CreateRepository(storage);
        await repository.CreateLibraryAsync(
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

    private static MigrationImportRequest CreateRadarrRequest()
    {
        const string payload =
            """
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

        return new MigrationAssistantService(CreateRepository(storage));
    }

    private static SqlitePlatformSettingsRepository CreateRepository(TestStorage storage)
    {
        return new SqlitePlatformSettingsRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")),
            TestSecretProtection.Create(storage));
    }
}
