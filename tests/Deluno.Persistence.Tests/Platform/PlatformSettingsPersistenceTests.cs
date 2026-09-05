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

    /// <summary>
    /// How often Deluno looks at whether the files it thinks you have are still
    /// there. Declared configurable since the day the pass was written and
    /// wired to nothing — the System screen printed "6h · configured" beside it
    /// while nothing configured anything. DESIGN-007, the console's schedules.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 24)]
    [InlineData(168, 168)]
    // Clamped to the same 1..168 SystemTasks.IntervalForHours accepts, so a
    // value that survives being saved is a value the scheduler will use. Two
    // different bounds would let the screen show a cadence Deluno never runs at.
    [InlineData(0, 1)]
    [InlineData(10_000, 168)]
    public async Task The_file_check_cadence_round_trips_within_the_hours_the_scheduler_accepts(int asked, int stored)
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        // The same path the PATCH endpoint takes: read, merge, save.
        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(LibraryFileCheckHours: asked));
        await repository.SaveAsync(merged, CancellationToken.None);

        Assert.Equal(stored, (await repository.GetAsync(CancellationToken.None)).LibraryFileCheckHours);
    }

    /// <summary>
    /// And an installation that has never been asked gets the six hours the
    /// pass was declared with.
    /// </summary>
    [Fact]
    public async Task The_file_check_cadence_defaults_to_six_hours()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        Assert.Equal(6, (await repository.GetAsync(CancellationToken.None)).LibraryFileCheckHours);
    }

    private static async Task<SqlitePlatformSettingsRepository> SettingsAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));
    }

    [Fact]
    public async Task Calendar_week_header_formats_round_trip_with_canonical_values()
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

        var initial = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("ddd d/M", initial.CalendarWeekHeaderFormat);

        var savedMonthFirst = await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                initial,
                new PatchPlatformSettingsRequest(CalendarWeekHeaderFormat: "ddd m/d")),
            CancellationToken.None);
        Assert.Equal("ddd m/d", savedMonthFirst.CalendarWeekHeaderFormat);

        var savedDayMonth = await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                savedMonthFirst,
                new PatchPlatformSettingsRequest(CalendarWeekHeaderFormat: "ddd d/M")),
            CancellationToken.None);
        Assert.Equal("ddd d/M", savedDayMonth.CalendarWeekHeaderFormat);
        Assert.Equal("ddd d/M", (await repository.GetAsync(CancellationToken.None)).CalendarWeekHeaderFormat);
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
    public async Task WorkflowVerification_is_false_until_a_dispatched_import_marks_it_true()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        Assert.False((await repository.GetAsync(CancellationToken.None)).WorkflowVerified);

        var marked = await repository.MarkWorkflowVerifiedAsync(CancellationToken.None);

        Assert.True(marked.WorkflowVerified);
        Assert.True((await repository.GetAsync(CancellationToken.None)).WorkflowVerified);
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
        Assert.Equal("Excluded from import list by user", permanent.Reason);
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

    [Fact]
    public async Task Settings_patch_preserves_a_value_changed_after_the_callers_snapshot_was_loaded()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var staleSnapshot = await repository.GetAsync(CancellationToken.None);

        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                staleSnapshot,
                new PatchPlatformSettingsRequest(AppInstanceName: "Changed in General")),
            CancellationToken.None);

        // The second save represents a different settings page. Its PATCH
        // starts from a fresh server snapshot, not the stale loader payload.
        var currentSnapshot = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                currentSnapshot,
                new PatchPlatformSettingsRequest(UiTheme: "dark")),
            CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("Changed in General", loaded.AppInstanceName);
        Assert.Equal("dark", loaded.UiTheme);
    }
}
