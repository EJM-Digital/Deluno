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
using Deluno.Series.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
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
        var inventory = report.Inventory;
        Assert.NotNull(inventory);
        Assert.Equal(7, inventory.InputRowCount);
        Assert.Equal(7, inventory.AccountedRowCount);
        Assert.Equal(0, inventory.UnaccountedRowCount);
        Assert.All(inventory.Entries, entry => Assert.True(entry.Complete));
        Assert.Equal(2, Assert.Single(inventory.Entries, entry => entry.Category == "monitored-state").InputRowCount);
        var titleInventory = Assert.Single(inventory.Entries, entry => entry.Category == "monitored-state");
        Assert.Equal(1, titleInventory.ClassificationCounts["source-reports-installed-file"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["quality-profile-assigned"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["quality-profile-unassigned"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["library-assigned"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["library-unassigned"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["probed-media-facts"]);
        Assert.Equal(1, titleInventory.ClassificationCounts["matched-format-history"]);
        var titleOperation = Assert.Single(report.Operations, operation => operation.TargetType == "monitored-state");
        Assert.Equal("1", titleOperation.Data["installedFileCount"]);
        Assert.Equal("1", titleOperation.Data["qualityProfileAssignmentCount"]);
        Assert.Equal("1", titleOperation.Data["libraryAssignmentCount"]);
        Assert.Equal("1", titleOperation.Data["probedMediaFactsCount"]);
        Assert.Equal("1", titleOperation.Data["matchedFormatHistoryCount"]);
        Assert.Equal(1, Assert.Single(inventory.Entries, entry => entry.Category == "quality-profile").InputRowCount);
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
    public async Task PreviewAsync_preserves_custom_formats_and_keeps_profiles_with_unresolved_matchers_review_only()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var report = await service.PreviewAsync(
            new MigrationImportRequest("radarr", "Radarr custom formats", """
                {
                  "customFormats": [
                    {
                      "id": 17,
                      "name": "Prefer WEB-DL",
                      "trashId": "trash-web",
                      "score": 500,
                      "upgradeAllowed": true,
                      "specifications": [
                        { "name": "WEB-DL", "implementation": "SourceSpecification", "fields": [{ "name": "value", "value": "web-dl" }], "negate": false, "required": true }
                      ]
                    }
                  ],
                  "qualityProfiles": [
                    {
                      "id": 80,
                      "name": "Migrated with formats",
                      "cutoff": "WEB 1080p",
                      "customFormats": [{ "id": 17, "score": 500 }],
                      "items": [{ "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } }]
                    }
                  ]
                }
                """),
            CancellationToken.None);

        var customFormat = Assert.Single(report.Operations, operation => operation.TargetType == "custom-format");
        Assert.Equal("report", customFormat.Action);
        Assert.False(customFormat.CanApply);
        Assert.Equal("trash-web", customFormat.Data["trashId"]);
        Assert.Contains("required", customFormat.Data["rawJson"]!, StringComparison.OrdinalIgnoreCase);

        var plan = Assert.Single(report.Operations, operation => operation.TargetType == "release-preference-plan");
        Assert.Equal("report", plan.Action);
        Assert.False(plan.CanApply);
        Assert.Equal("True", plan.Data["requiresReview"]);
        Assert.False(string.IsNullOrWhiteSpace(plan.Data["planHash"]));
        Assert.Contains("quality", plan.Data["planJson"]!, StringComparison.OrdinalIgnoreCase);

        var profile = Assert.Single(report.Operations, operation => operation.TargetType == "quality-profile");
        Assert.Equal("create", profile.Action);
        Assert.False(profile.CanApply);
        Assert.Contains("custom-format", profile.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_imports_a_reviewed_guide_format_and_keeps_the_profile_reference()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.First(format =>
            format.MappingStatus == GuideMappingStatus.Reviewed
            && format.MappedTraitIds is { Count: > 0 });

        var result = await service.ApplyAsync(
            new MigrationImportRequest("radarr", "Reviewed guide export", $$"""
                {
                  "customFormats": [
                    {
                      "id": 17,
                      "name": "{{guideFormat.Name}}",
                      "trashId": "{{guideFormat.TrashId}}",
                      "score": 500,
                      "upgradeAllowed": true,
                      "specifications": [
                        { "name": "source", "implementation": "SourceSpecification", "fields": [{ "name": "value", "value": "web-dl" }], "negate": false, "required": true }
                      ]
                    }
                  ],
                  "qualityProfiles": [
                    {
                      "id": 80,
                      "name": "Reviewed guide profile",
                      "cutoff": "WEB 1080p",
                      "customFormats": [{ "id": 17, "score": 500 }],
                      "items": [{ "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } }]
                    }
                  ]
                }
                """),
            CancellationToken.None);

        Assert.Contains(result.Applied, item => item.TargetType == "custom-format" && item.Result == "created");
        Assert.Contains(result.Applied, item => item.TargetType == "quality-profile" && item.Result == "created");
        var customFormat = Assert.Single(await CreateQualityRepository(storage).ListCustomFormatsAsync(CancellationToken.None));
        Assert.Equal(guideFormat.TrashId, customFormat.TrashId);
        Assert.NotEmpty(customFormat.Conditions);
        var profile = Assert.Single(await CreateQualityRepository(storage).ListQualityProfilesAsync(CancellationToken.None));
        Assert.Contains(customFormat.Id, profile.CustomFormatIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain(result.Report.Operations, operation =>
            operation.TargetType == "custom-format" && operation.Action == "report");
    }

    [Fact]
    public async Task Migration_preserves_profile_scoped_format_scores_in_immutable_plans_and_is_idempotent()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage, includeReleasePreferencePlans: true);
        var guideFormat = GuidePackageCatalog.Current.CustomFormats.First(format =>
            format.MappingStatus == GuideMappingStatus.Reviewed
            && format.MappedTraitIds is { Count: > 0 });
        var request = new MigrationImportRequest("radarr", "Profile assignments", $$"""
            {
              "customFormats": [
                {
                  "id": 17,
                  "name": "Profile-scoped assignment",
                  "trashId": "{{guideFormat.TrashId}}",
                  "score": 100,
                  "upgradeAllowed": true,
                  "specifications": [
                    { "name": "source", "implementation": "SourceSpecification", "fields": [{ "name": "value", "value": "web-dl" }], "negate": false, "required": true }
                  ]
                }
              ],
              "qualityProfiles": [
                {
                  "id": 801,
                  "name": "Low preference profile",
                  "cutoff": "WEB 1080p",
                  "customFormats": [{ "id": 17, "score": 125 }],
                  "items": [{ "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } }]
                },
                {
                  "id": 802,
                  "name": "High preference profile",
                  "cutoff": "WEB 1080p",
                  "customFormats": [{ "id": 17, "score": 875 }],
                  "items": [{ "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } }]
                }
              ]
            }
            """);

        var preview = await service.PreviewAsync(request, CancellationToken.None);
        var plans = preview.Operations
            .Where(operation => operation.TargetType == "release-preference-plan")
            .ToArray();
        Assert.Equal(2, plans.Length);
        Assert.All(plans, operation =>
        {
            Assert.Equal("create", operation.Action);
            Assert.True(operation.CanApply);
        });

        var compiled = plans
            .Select(operation => ReleasePreferencePlanCodec.Deserialize(operation.Data["planJson"]!))
            .OrderBy(plan => plan.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, compiled.Length);
        var lowSource = Assert.Single(compiled[0].Sources!, source => source.SourceId == guideFormat.TrashId);
        var highSource = Assert.Single(compiled[1].Sources!, source => source.SourceId == guideFormat.TrashId);
        Assert.Equal("125", lowSource.AssignedScore);
        Assert.Equal("875", highSource.AssignedScore);
        Assert.Equal(guideFormat.OriginalScore.ToString(System.Globalization.CultureInfo.InvariantCulture), lowSource.OriginalScore);
        Assert.NotEqual(compiled[0].PlanHash, compiled[1].PlanHash);

        var applied = await service.ApplyAsync(request, CancellationToken.None);
        Assert.Equal(2, applied.Applied.Count(item => item.TargetType == "release-preference-plan"));
        var storedPlans = await new SqliteReleasePreferencePlanRepository(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")))
            .ListAsync("movies", CancellationToken.None);
        Assert.Equal(2, storedPlans.Count);

        var migratedProfiles = await CreateQualityRepository(storage)
            .ListQualityProfilesAsync(CancellationToken.None);
        Assert.Equal(2, migratedProfiles.Count);
        foreach (var profile in migratedProfiles)
        {
            Assert.NotNull(profile.ReleasePreferencePlan);
            var reference = profile.ReleasePreferencePlan!;
            var storedPlan = Assert.Single(storedPlans, item =>
                string.Equals(item.Plan.Id, reference.PlanId, StringComparison.Ordinal)
                && string.Equals(item.Plan.Version, reference.Version, StringComparison.Ordinal));
            Assert.Equal(storedPlan.PlanHash, reference.PlanHash);
        }

        var secondPreview = await service.PreviewAsync(request, CancellationToken.None);
        Assert.Equal(2, secondPreview.Operations.Count(operation =>
            operation.TargetType == "release-preference-plan" && operation.Action == "skip"));
        Assert.DoesNotContain(secondPreview.Operations, operation =>
            operation.TargetType == "release-preference-plan" && operation.Action == "create");
    }

    [Fact]
    public async Task ApplyAsync_can_explicitly_retain_an_opaque_legacy_format_without_making_it_typed_intent()
    {
        using var storage = TestStorage.Create();
        var service = await CreateServiceAsync(storage);

        var request = new MigrationImportRequest("radarr", "Advanced legacy export", """
                {
                  "customFormats": [
                    {
                      "id": 17,
                      "name": "Opaque legacy matcher",
                      "trashId": "unknown-trash-id",
                      "score": 999,
                      "upgradeAllowed": false,
                      "specifications": [
                        { "name": "release group", "implementation": "ReleaseGroupSpecification", "fields": [{ "name": "value", "value": "trusted" }], "negate": true, "required": false }
                      ]
                    }
                  ],
                  "qualityProfiles": [
                    {
                      "id": 80,
                      "name": "Advanced legacy profile",
                      "cutoff": "WEB 1080p",
                      "customFormats": [{ "id": 17, "score": 999 }],
                      "items": [{ "allowed": true, "quality": { "id": 1, "name": "WEB 1080p" } }]
                    }
                  ]
                }
                """) with { AllowAdvancedLegacyRules = true };
        var preview = await service.PreviewAsync(request, CancellationToken.None);
        var previewCustomOperation = Assert.Single(preview.Operations, operation => operation.TargetType == "custom-format");
        Assert.Equal("create", previewCustomOperation.Action);
        Assert.True(previewCustomOperation.CanApply);
        Assert.Equal("UnmappedAdvanced", previewCustomOperation.Data["classification"]);
        Assert.Equal("advanced-legacy", previewCustomOperation.Data["activation"]);
        Assert.Contains(preview.Operations, operation =>
            operation.TargetType == "quality-profile"
            && operation.Name == "Advanced legacy profile"
            && operation.CanApply);

        var result = await service.ApplyAsync(request,
            CancellationToken.None);

        Assert.Contains(result.Applied, item => item.TargetType == "custom-format" && item.Result == "created");
        Assert.Contains(result.Applied, item => item.TargetType == "quality-profile" && item.Result == "created");
        var customFormat = Assert.Single(await CreateQualityRepository(storage).ListCustomFormatsAsync(CancellationToken.None));
        Assert.Equal(999, customFormat.Score);
        Assert.False(customFormat.UpgradeAllowed);
        Assert.Contains("negate", customFormat.Conditions, StringComparison.OrdinalIgnoreCase);
        var profile = Assert.Single(await CreateQualityRepository(storage).ListQualityProfilesAsync(CancellationToken.None));
        Assert.Contains(customFormat.Id, profile.CustomFormatIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
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
    public async Task ApplyAsync_records_verified_backup_evidence_when_host_backup_is_available()
    {
        using var storage = TestStorage.Create();
        await CreateServiceAsync(storage);
        var backup = new RecordingMigrationBackupService();
        var service = new MigrationAssistantService(
            CreateRepository(storage),
            CreateLibrariesRepository(storage),
            CreateQualityRepository(storage),
            CreateConnectionsRepository(storage),
            CreateIntakeRepository(storage),
            backupService: backup);

        var result = await service.ApplyAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.True(result.Report.Valid);
        Assert.Equal("pre-migration", backup.LastReason);
        Assert.NotNull(result.Backup);
        var audit = Assert.Single(await CreateRepository(storage).ListMigrationAuditReportsAsync(10, CancellationToken.None));
        Assert.Equal(result.Backup, audit.Backup);
        Assert.Equal("manifest-and-restore-preview-verified", audit.Backup!.Verification);
    }

    [Fact]
    public async Task ApplyAsync_blocks_all_writes_when_verified_backup_fails()
    {
        using var storage = TestStorage.Create();
        await CreateServiceAsync(storage);
        var service = new MigrationAssistantService(
            CreateRepository(storage),
            CreateLibrariesRepository(storage),
            CreateQualityRepository(storage),
            CreateConnectionsRepository(storage),
            CreateIntakeRepository(storage),
            backupService: new FailingMigrationBackupService());

        var result = await service.ApplyAsync(CreateRadarrRequest(), CancellationToken.None);

        Assert.False(result.Report.Valid);
        Assert.Empty(result.Applied);
        Assert.Contains(result.Report.Errors, error => error.Contains("automatic verified backup failed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await CreateLibrariesRepository(storage).ListLibrariesAsync(CancellationToken.None));
        Assert.Empty(await CreateQualityRepository(storage).ListQualityProfilesAsync(CancellationToken.None));
        Assert.Empty(await CreateConnectionsRepository(storage).ListIndexersAsync(CancellationToken.None));
        Assert.Empty(await CreateRepository(storage).ListMigrationAuditReportsAsync(10, CancellationToken.None));
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
                "series": [{ "title": "Severance", "year": 2022, "imdbId": "tt11280740", "monitored": true, "hasFile": true, "seriesType": "daily", "numberingScheme": "airdate", "numberingSource": "owner" }]
              }
            }
            """);

        var preview = await service.PreviewAsync(request, CancellationToken.None);
        var titleOperations = preview.Operations
            .Where(operation => operation.TargetType == "monitored-state")
            .ToDictionary(operation => operation.Data["mediaType"]!, StringComparer.Ordinal);
        Assert.Equal(2, titleOperations.Count);
        Assert.Equal("0", titleOperations["movies"].Data["installedFileCount"]);
        Assert.Equal("1", titleOperations["tv"].Data["installedFileCount"]);
        Assert.Equal(2, preview.Inventory!.Entries.Count(entry => entry.Category == "monitored-state"));

        var applied = await service.ApplyAsync(request, CancellationToken.None);

        var importedMovies = await movies.ListAsync(CancellationToken.None);
        Assert.True(importedMovies.Count == 1, $"Applied: {string.Join(", ", applied.Applied.Select(item => $"{item.TargetType}:{item.Result}"))}; warnings: {string.Join(" | ", applied.Report.Warnings)}");
        var movie = Assert.Single(importedMovies);
        var show = Assert.Single(await series.ListAsync(CancellationToken.None));
        Assert.True(movie.Monitored);
        Assert.True(show.Monitored);
        var numbering = await series.GetNumberingAsync(show.Id, CancellationToken.None);
        Assert.NotNull(numbering);
        Assert.Equal(SeriesTypes.Daily, numbering.SeriesType);
        Assert.Equal(SeriesNumberingSchemes.AirDate, numbering.NumberingScheme);
        Assert.Equal(SeriesNumberingSources.Owner, numbering.NumberingSource);
        Assert.Contains(applied.Applied, item => item.TargetType == "movie" && item.Result == "created");
        Assert.Contains(applied.Applied, item => item.TargetType == "series" && item.Result == "created");

        var libraries = await librariesRepository.ListLibrariesAsync(CancellationToken.None);
        var movieLibrary = libraries.Single(item => item.MediaType == "movies");
        var tvLibrary = libraries.Single(item => item.MediaType == "tv");
        Assert.Equal("missing", (await movies.GetMovieWantedStateAsync(movie.Id, movieLibrary.Id, CancellationToken.None))!.WantedStatus);
        Assert.Equal("covered", (await series.GetSeriesWantedStateAsync(show.Id, tvLibrary.Id, CancellationToken.None))!.WantedStatus);

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
                {
                  "title": "Dune Part Two",
                  "monitored": true,
                  "hasFile": true,
                  "qualityProfileId": 80,
                  "rootFolderPath": "/mnt/media/migrated-movies",
                  "movieFile": {
                    "mediaInfo": { "videoCodec": "HEVC", "audioCodec": "TrueHD" },
                    "customFormats": [{ "id": 17, "name": "TrueHD Atmos" }]
                  }
                },
                { "title": "Anora", "monitored": false, "hasFile": false }
              ]
            }
            """;

        return new MigrationImportRequest("radarr", "Radarr test", payload);
    }

    private static async Task<MigrationAssistantService> CreateServiceAsync(
        TestStorage storage,
        bool includeReleasePreferencePlans = false)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new MigrationAssistantService(
            CreateRepository(storage),
            CreateLibrariesRepository(storage),
            CreateQualityRepository(storage),
            CreateConnectionsRepository(storage),
            CreateIntakeRepository(storage),
            releasePreferencePlanRepository: includeReleasePreferencePlans
                ? new SqliteReleasePreferencePlanRepository(
                    storage.Factory,
                    new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T00:00:00Z")))
                : null);
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

    private sealed class RecordingMigrationBackupService : IMigrationBackupService
    {
        public string? LastReason { get; private set; }

        public Task<MigrationBackupReceipt> CreateVerifiedBackupAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            LastReason = reason;
            return Task.FromResult(new MigrationBackupReceipt(
                "backup-1",
                "backup-1.zip",
                1024,
                DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
                reason,
                "manifest-and-restore-preview-verified"));
        }
    }

    private sealed class FailingMigrationBackupService : IMigrationBackupService
    {
        public Task<MigrationBackupReceipt> CreateVerifiedBackupAsync(
            string reason,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Synthetic backup failure.");
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
