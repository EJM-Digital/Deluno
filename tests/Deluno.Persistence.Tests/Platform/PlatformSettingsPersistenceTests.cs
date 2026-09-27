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
                MovieFolderFormat: "{Movie Title} ({Release Year})",
                SeriesFolderFormat: "{Series Title} ({Series Year})",
                MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
                EpisodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                HostBindAddress: "127.0.0.1",
                HostPort: 5099,
                UrlBase: "/deluno",
                RequireAuthentication: true,
                UiTheme: "dark",
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
    public async Task SetPermissionsOnImport_and_its_chmod_and_group_persist_and_default_off()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        var beforeSave = await repository.GetAsync(CancellationToken.None);
        Assert.False(beforeSave.SetPermissionsOnImport);
        Assert.Null(beforeSave.FolderChmod);
        Assert.Null(beforeSave.ChownGroup);

        var current = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                current,
                new PatchPlatformSettingsRequest(
                    SetPermissionsOnImport: true,
                    FolderChmod: "0755",
                    ChownGroup: "media")),
            CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.True(loaded.SetPermissionsOnImport);
        Assert.Equal("0755", loaded.FolderChmod);
        Assert.Equal("media", loaded.ChownGroup);
    }

    [Fact]
    public async Task FolderChmod_that_is_not_a_valid_octal_mode_is_dropped_rather_than_stored_malformed()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        var current = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                current,
                new PatchPlatformSettingsRequest(FolderChmod: "not-octal")),
            CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.Null(loaded.FolderChmod);
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

    /// <summary>
    /// #904's "on by default" (commit 2e9f2125) never shipped in a release —
    /// v1.0.0-rc.22 already read a missing value here as off — so #936 does
    /// not change this pair's behaviour at all, only reaffirms it and closes
    /// off the possibility of the never-released "on by default" ever
    /// landing. A brand-new install (a freshly created database, with no
    /// setting stored at all) reads both off, exactly as every released
    /// build has. See AutomationDefaultsOffMigrationTests for why the
    /// migration deliberately does not freeze either of these two keys, only
    /// media.renameOnImport.
    /// </summary>
    [Fact]
    public async Task Cleanup_removal_and_purge_default_to_off_for_a_new_install()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.False(loaded.CleanupRemoveClientEntryAfterThreshold);
        Assert.False(loaded.CleanupPurgePayloadAfterThreshold);
    }

    /// <summary>
    /// Once either is explicitly turned on, it stays on until turned off
    /// again — the comparison only changes what a missing value means.
    /// </summary>
    [Fact]
    public async Task Cleanup_removal_and_purge_stay_on_once_explicitly_turned_on()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(
                CleanupRemoveClientEntryAfterThreshold: true,
                CleanupPurgePayloadAfterThreshold: true));
        await repository.SaveAsync(merged, CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.True(loaded.CleanupRemoveClientEntryAfterThreshold);
        Assert.True(loaded.CleanupPurgePayloadAfterThreshold);
    }

    /// <summary>Explicitly turning either off is remembered too, not just the on case.</summary>
    [Fact]
    public async Task Cleanup_removal_and_purge_stay_off_once_explicitly_turned_off()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(
                CleanupRemoveClientEntryAfterThreshold: false,
                CleanupPurgePayloadAfterThreshold: false));
        await repository.SaveAsync(merged, CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.False(loaded.CleanupRemoveClientEntryAfterThreshold);
        Assert.False(loaded.CleanupPurgePayloadAfterThreshold);
    }

    /// <summary>#936: renaming on import follows the same reversed default as the cleanup settings above.</summary>
    [Fact]
    public async Task RenameOnImport_defaults_to_off_for_a_new_install_and_stays_on_once_turned_on()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        Assert.False((await repository.GetAsync(CancellationToken.None)).RenameOnImport);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(RenameOnImport: true));
        await repository.SaveAsync(merged, CancellationToken.None);

        Assert.True((await repository.GetAsync(CancellationToken.None)).RenameOnImport);
    }

    /// <summary>#936: the one global switch for automatic subtitle fetching, off for a new install and saved through the same PATCH path as every other automatic behaviour.</summary>
    [Fact]
    public async Task SubtitleFetchAutomaticEnabled_defaults_to_off_for_a_new_install_and_stays_on_once_turned_on()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        Assert.False((await repository.GetAsync(CancellationToken.None)).SubtitleFetchAutomaticEnabled);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(SubtitleFetchAutomaticEnabled: true));
        await repository.SaveAsync(merged, CancellationToken.None);

        Assert.True((await repository.GetAsync(CancellationToken.None)).SubtitleFetchAutomaticEnabled);
    }

    /// <summary>#757: on by default (today's replace-with-dash rendering), and stays off once turned off.</summary>
    [Fact]
    public async Task ReplaceIllegalCharacters_defaults_to_on_for_a_new_install_and_stays_off_once_turned_off()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        Assert.True((await repository.GetAsync(CancellationToken.None)).ReplaceIllegalCharacters);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(ReplaceIllegalCharacters: false));
        await repository.SaveAsync(merged, CancellationToken.None);

        Assert.False((await repository.GetAsync(CancellationToken.None)).ReplaceIllegalCharacters);
    }

    /// <summary>#759: Smart Replace by default, and an unrecognised value normalizes back to it rather than being stored verbatim.</summary>
    [Fact]
    public async Task ColonReplacementMode_defaults_to_smart_replace_and_normalizes_an_unknown_value()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        Assert.Equal(ColonReplacementModes.SmartReplace, (await repository.GetAsync(CancellationToken.None)).ColonReplacementMode);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(ColonReplacementMode: ColonReplacementModes.SpaceDashSpace));
        await repository.SaveAsync(merged, CancellationToken.None);

        Assert.Equal(ColonReplacementModes.SpaceDashSpace, (await repository.GetAsync(CancellationToken.None)).ColonReplacementMode);

        var invalid = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(ColonReplacementMode: "not-a-real-mode"));
        await repository.SaveAsync(invalid, CancellationToken.None);

        Assert.Equal(ColonReplacementModes.SmartReplace, (await repository.GetAsync(CancellationToken.None)).ColonReplacementMode);
    }

    /// <summary>#761: Daily/Anime episode formats default sensibly and round-trip like any other naming format.</summary>
    [Fact]
    public async Task DailyAndAnimeEpisodeFileFormat_default_and_round_trip()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.Contains("{Air-Date}", defaults.DailyEpisodeFileFormat);
        Assert.Contains("{absolute:000}", defaults.AnimeEpisodeFileFormat);

        var merged = PlatformSettingsPatchMerger.Apply(
            defaults,
            new PatchPlatformSettingsRequest(
                DailyEpisodeFileFormat: "{Series Title} - {Air-Date}",
                AnimeEpisodeFileFormat: "{Series Title} - {absolute:000}"));
        await repository.SaveAsync(merged, CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("{Series Title} - {Air-Date}", loaded.DailyEpisodeFileFormat);
        Assert.Equal("{Series Title} - {absolute:000}", loaded.AnimeEpisodeFileFormat);
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

        // #936 added an Automation step before Finish, so the setup guide now
        // has 6 steps (indices 0-5) and the clamp moved from 4 to 5.
        Assert.Equal(5, saved.LastCompletedStep);
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

    /// <summary>
    /// #659: an install that never opens the "Download health" settings
    /// screen gets exactly the behaviour the rule originally shipped with -
    /// no progress flags a download after 30 minutes, the other two rules are
    /// off.
    /// </summary>
    [Fact]
    public async Task Download_health_rules_default_to_the_original_hard_coded_behaviour()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.True(loaded.DownloadHealthNoProgressEnabled);
        Assert.Equal(30, loaded.DownloadHealthNoProgressMinutes);
        Assert.False(loaded.DownloadHealthMinSpeedEnabled);
        Assert.Equal(50, loaded.DownloadHealthMinSpeedKbps);
        Assert.Equal(30, loaded.DownloadHealthMinSpeedMinutes);
        Assert.False(loaded.DownloadHealthMinPeersEnabled);
        Assert.Equal(1, loaded.DownloadHealthMinPeers);
        Assert.Equal(30, loaded.DownloadHealthMinPeersMinutes);
    }

    [Fact]
    public async Task Download_health_rules_round_trip_through_a_patch_and_survive_being_reloaded()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(
                DownloadHealthNoProgressEnabled: true,
                DownloadHealthNoProgressMinutes: 15,
                DownloadHealthMinSpeedEnabled: true,
                DownloadHealthMinSpeedKbps: 200,
                DownloadHealthMinSpeedMinutes: 45,
                DownloadHealthMinPeersEnabled: true,
                DownloadHealthMinPeers: 3,
                DownloadHealthMinPeersMinutes: 20));
        await repository.SaveAsync(merged, CancellationToken.None);

        var reloaded = await SettingsAsync(storage); // a fresh instance: nothing survives but the database
        var loaded = await reloaded.GetAsync(CancellationToken.None);

        Assert.True(loaded.DownloadHealthNoProgressEnabled);
        Assert.Equal(15, loaded.DownloadHealthNoProgressMinutes);
        Assert.True(loaded.DownloadHealthMinSpeedEnabled);
        Assert.Equal(200, loaded.DownloadHealthMinSpeedKbps);
        Assert.Equal(45, loaded.DownloadHealthMinSpeedMinutes);
        Assert.True(loaded.DownloadHealthMinPeersEnabled);
        Assert.Equal(3, loaded.DownloadHealthMinPeers);
        Assert.Equal(20, loaded.DownloadHealthMinPeersMinutes);
    }

    [Fact]
    public async Task Each_download_health_rule_can_be_switched_off_independently()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(DownloadHealthNoProgressEnabled: false));
        await repository.SaveAsync(merged, CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.False(loaded.DownloadHealthNoProgressEnabled);
        // Untouched by this patch - still off, not flipped as a side effect.
        Assert.False(loaded.DownloadHealthMinSpeedEnabled);
        Assert.False(loaded.DownloadHealthMinPeersEnabled);
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
