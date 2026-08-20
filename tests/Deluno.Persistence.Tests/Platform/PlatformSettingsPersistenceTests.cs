using Deluno.Persistence.Tests.Support;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class PlatformSettingsPersistenceTests
{
    [Fact]
    public async Task SaveAsync_persists_user_configurable_settings_in_an_isolated_database()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        var saved = await repository.SaveAsync(
            new UpdatePlatformSettingsRequest(
                AppInstanceName: "Deluno Test",
                MovieRootPath: @"D:\Media\Movies",
                SeriesRootPath: @"D:\Media\TV",
                DownloadsPath: @"D:\Downloads\Complete",
                IncompleteDownloadsPath: @"D:\Downloads\Incomplete",
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: true,
                CleanupEmptyFolders: true,
                RemoveCompletedDownloads: false,
                UnmonitorWhenCutoffMet: true,
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1",
                HostPort: 5099,
                UrlBase: "/deluno",
                RequireAuthentication: true,
                UiTheme: "dark",
                UiDensity: "expanded",
                DefaultMovieView: "grid",
                DefaultShowView: "list",
                MetadataNfoEnabled: true,
                MetadataArtworkEnabled: true,
                MetadataCertificationCountry: "AU",
                MetadataLanguage: "en-AU",
                MetadataProviderMode: "broker",
                MetadataBrokerUrl: "https://metadata.example.test",
                MetadataTmdbApiKey: "tmdb-secret",
                MetadataOmdbApiKey: "omdb-secret",
                ReleaseNeverGrabPatterns: "cam\nhardcoded subs",
                SearchScoringMode: SearchScoringModes.MlOnly,
                ImportRecoveryRetentionDays: 60,
                MdbListApiKey: "mdblist-secret",
                DownloadHealthStrikeThreshold: 5,
                CleanupBlockReleaseAfterThreshold: true,
                CleanupQueueReplacementAfterThreshold: true,
                CleanupRemoveClientEntryAfterThreshold: true,
                CleanupPurgePayloadAfterThreshold: true),
            CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.Equal("Deluno Test", saved.AppInstanceName);
        Assert.Equal(@"D:\Media\Movies", loaded.MovieRootPath);
        Assert.Equal(@"D:\Media\TV", loaded.SeriesRootPath);
        Assert.True(loaded.AutoStartJobs);
        Assert.True(loaded.RenameOnImport);
        Assert.True(loaded.UseHardlinks);
        Assert.True(loaded.UnmonitorWhenCutoffMet);
        Assert.Equal("expanded", loaded.UiDensity);
        Assert.Equal("broker", loaded.MetadataProviderMode);
        Assert.Equal("https://metadata.example.test", loaded.MetadataBrokerUrl);
        Assert.True(loaded.MetadataTmdbApiKeyConfigured);
        Assert.True(loaded.MetadataOmdbApiKeyConfigured);
        Assert.True(loaded.MdbListApiKeyConfigured);
        Assert.Equal("cam\nhardcoded subs", loaded.ReleaseNeverGrabPatterns);
        Assert.Equal(SearchScoringModes.MlOnly, loaded.SearchScoringMode);
        Assert.Equal(60, loaded.ImportRecoveryRetentionDays);
        Assert.Equal(5, loaded.DownloadHealthStrikeThreshold);
        Assert.True(loaded.CleanupBlockReleaseAfterThreshold);
        Assert.True(loaded.CleanupQueueReplacementAfterThreshold);
        Assert.True(loaded.CleanupRemoveClientEntryAfterThreshold);
        Assert.True(loaded.CleanupPurgePayloadAfterThreshold);
    }

    [Fact]
    public async Task ImportRecoveryRetentionDays_defaults_to_30_when_not_configured()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        // Read without any prior save — should return default 30
        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.Equal(SearchScoringModes.Hybrid, loaded.SearchScoringMode);
        Assert.Equal(30, loaded.ImportRecoveryRetentionDays);
    }

    [Fact]
    public async Task Fresh_install_does_not_invent_libraries_or_quality_profiles()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var qualityRepository = new SqliteQualityRepository(storage.Factory, timeProvider);

        Assert.Empty(await librariesRepository.ListLibrariesAsync(CancellationToken.None));
        Assert.Empty(await qualityRepository.ListQualityProfilesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fresh_install_uses_the_managed_metadata_broker_without_a_user_provider_key()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-15T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var settings = await repository.GetAsync(CancellationToken.None);

        Assert.Equal("broker", settings.MetadataProviderMode);
        Assert.Equal("https://deluno-metadata-gateway.ejmdigital.workers.dev", settings.MetadataBrokerUrl);
        Assert.True(settings.MetadataBrokerConfigured);
        Assert.False(settings.MetadataTmdbApiKeyConfigured);
        Assert.False(settings.MetadataOmdbApiKeyConfigured);
    }

    [Fact]
    public async Task SetupProgress_persists_independently_from_the_non_secret_setup_draft()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var saved = await repository.SaveSetupProgressAsync(
            new UpdateSetupProgressRequest(99, IsSkipped: false, IsCompleted: true),
            CancellationToken.None);
        var loaded = await repository.GetSetupProgressAsync(CancellationToken.None);

        Assert.Equal(4, saved.LastCompletedStep);
        Assert.True(saved.IsCompleted);
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public async Task SetupDraft_persists_resumable_choices_without_credentials_and_can_be_cleared()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var saved = await repository.SaveSetupDraftAsync(new UpdateSetupDraftRequest(
            MediaIntent: "movies",
            MovieRootPath: "D:\\Media\\Movies",
            DownloadsPath: "D:\\Downloads",
            QualityPreset: "balanced1080p",
            FormatGoal: "balanced",
            IndexerUrl: "https://indexer.example/api",
            ClientHost: "download-client.local",
            FirstTitle: "Example Movie"), CancellationToken.None);

        var loaded = await repository.GetSetupDraftAsync(CancellationToken.None);
        Assert.Equal(saved, loaded);
        Assert.Equal("D:\\Media\\Movies", loaded.MovieRootPath);
        Assert.Equal("Example Movie", loaded.FirstTitle);
        Assert.DoesNotContain("ApiKey", string.Join(',', typeof(UpdateSetupDraftRequest).GetProperties().Select(property => property.Name)));
        Assert.DoesNotContain("Password", string.Join(',', typeof(UpdateSetupDraftRequest).GetProperties().Select(property => property.Name)));

        await repository.ClearSetupDraftAsync(CancellationToken.None);
        var cleared = await repository.GetSetupDraftAsync(CancellationToken.None);
        Assert.Equal(string.Empty, cleared.MovieRootPath);
        Assert.Equal(string.Empty, cleared.FirstTitle);
    }

    [Fact]
    public async Task DownloadHealth_observations_are_durable_rate_limited_and_can_be_temporarily_ignored()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T01:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteDownloadHealthRepository(storage.Factory, timeProvider);
        var observation = new DownloadHealthObservation("client-1", "queue-1", "Example.Movie.2026.1080p", "no-throughput", "warning", "0 MB/s");

        var first = Assert.Single(await repository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None));
        var repeatedSnapshot = Assert.Single(await repository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None));

        Assert.Equal(1, first.StrikeCount);
        Assert.Equal(1, repeatedSnapshot.StrikeCount);
        Assert.False(await repository.IsDownloadReleaseBlockedAsync("client-1", "Example Movie 2026 1080p", CancellationToken.None));

        timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T01:31:00Z"));
        repository = new SqliteDownloadHealthRepository(storage.Factory, timeProvider);
        await repository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None);
        timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T02:02:00Z"));
        repository = new SqliteDownloadHealthRepository(storage.Factory, timeProvider);
        var third = Assert.Single(await repository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None));

        Assert.Equal(3, third.StrikeCount);
        Assert.True(await repository.IsDownloadReleaseBlockedAsync("client-1", "Example.Movie.2026.1080p", CancellationToken.None));

        var ignored = await repository.IgnoreDownloadHealthFindingAsync("client-1", "queue-1", "no-throughput", 7, CancellationToken.None);
        Assert.NotNull(ignored);
        Assert.False(await repository.IsDownloadReleaseBlockedAsync("client-1", "Example.Movie.2026.1080p", CancellationToken.None));
    }

    [Fact]
    public async Task Intake_list_exclusions_are_durable_scoped_to_the_list_and_can_be_restored()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T01:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteIntakeRepository(storage.Factory, timeProvider);
        var source = await repository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                "Weekend films",
                "imdb",
                "https://example.test/weekend.csv",
                "movies",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "any",
                24,
                SearchOnAdd: false,
                IsEnabled: true),
            CancellationToken.None);

        var permanent = await repository.CreateIntakeListExclusionAsync(
            source.Id,
            new CreateIntakeListExclusionRequest("Arrival", 2016, "tt2543164", null),
            CancellationToken.None);
        var temporary = await repository.CreateIntakeListExclusionAsync(
            source.Id,
            new CreateIntakeListExclusionRequest("Dune", 2021, "tt1160419", 7),
            CancellationToken.None);

        Assert.NotNull(permanent);
        Assert.NotNull(temporary);
        Assert.Null(permanent!.ExpiresUtc);
        Assert.Equal(timeProvider.GetUtcNow().AddDays(7), temporary!.ExpiresUtc);

        var reloaded = new SqliteIntakeRepository(storage.Factory, timeProvider);
        var active = await reloaded.ListActiveIntakeListExclusionsAsync(source.Id, CancellationToken.None);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, item => item.EntryKey == "imdb:tt2543164");
        Assert.Contains(active, item => item.EntryKey == "imdb:tt1160419");

        Assert.True(await reloaded.DeleteIntakeListExclusionAsync(source.Id, permanent.Id, CancellationToken.None));
        var restored = await reloaded.ListActiveIntakeListExclusionsAsync(source.Id, CancellationToken.None);
        Assert.Single(restored);
        Assert.Equal(temporary.Id, restored[0].Id);
    }

    [Fact]
    public async Task Intake_title_origins_are_durable_per_list_and_preserve_first_seen_time()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T01:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteIntakeRepository(storage.Factory, timeProvider);
        var first = await repository.RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(
                "list-1", "Weekend films", "imdb", "movies", "movie-1", "imdb:tt2543164", "Arrival", 2016, "tt2543164"),
            CancellationToken.None);

        Assert.NotNull(first);
        timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-15T01:00:00Z"));
        repository = new SqliteIntakeRepository(storage.Factory, timeProvider);
        var refreshed = await repository.RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(
                "list-1", "Weekend films renamed", "imdb", "movies", "movie-1", "imdb:tt2543164", "Arrival", 2016, "tt2543164"),
            CancellationToken.None);
        await repository.RecordIntakeTitleOriginAsync(
            new CreateIntakeTitleOriginRequest(
                "list-2", "Awards watchlist", "trakt", "movies", "movie-1", "imdb:tt2543164", "Arrival", 2016, "tt2543164"),
            CancellationToken.None);

        var origins = await repository.ListIntakeTitleOriginsAsync("movies", "movie-1", CancellationToken.None);

        Assert.Equal(2, origins.Count);
        Assert.NotNull(refreshed);
        Assert.Equal(first!.Id, refreshed!.Id);
        Assert.Equal(first.FirstSeenUtc, refreshed.FirstSeenUtc);
        Assert.Equal(timeProvider.GetUtcNow(), refreshed.LastSeenUtc);
        Assert.Contains(origins, item => item.SourceName == "Weekend films renamed" && item.Provider == "imdb");
        Assert.Contains(origins, item => item.SourceName == "Awards watchlist" && item.Provider == "trakt");
    }

    [Fact]
    public async Task SetGlobalAutomationEnabledAsync_only_changes_the_background_worker_switch()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var paused = await repository.SetGlobalAutomationEnabledAsync(false, CancellationToken.None);
        var resumed = await repository.SetGlobalAutomationEnabledAsync(true, CancellationToken.None);

        Assert.False(paused.AutoStartJobs);
        Assert.True(resumed.AutoStartJobs);
    }
}
