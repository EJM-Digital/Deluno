using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
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
                MovieRootPath: @"D:\Media\Movies",
                SeriesRootPath: @"D:\Media\TV",
                DownloadsPath: @"D:\Downloads\Complete",
                IncompleteDownloadsPath: @"D:\Downloads\Incomplete",
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: true,
                CleanupEmptyFolders: true,
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
    public async Task Check_connections_regularly_is_on_for_a_fresh_or_upgraded_install_and_stays_off_once_turned_off()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-01T01:02:03Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));

        // Nothing stored: reads on (it only reads, so existing installs get it too).
        Assert.True((await repository.GetAsync(CancellationToken.None)).CheckConnectionsRegularly);

        var current = await repository.GetAsync(CancellationToken.None);
        var merged = PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(CheckConnectionsRegularly: false));
        await repository.SaveAsync(merged, CancellationToken.None);
        Assert.False((await repository.GetAsync(CancellationToken.None)).CheckConnectionsRegularly);

        // An unrelated patch leaves it off.
        var again = PlatformSettingsPatchMerger.Apply(await repository.GetAsync(CancellationToken.None), new PatchPlatformSettingsRequest(UiTheme: "dark"));
        await repository.SaveAsync(again, CancellationToken.None);
        Assert.False((await repository.GetAsync(CancellationToken.None)).CheckConnectionsRegularly);

        // Keep awake starts off for everyone, is on only after the button, and an unrelated save leaves it as it was.
        Assert.False((await repository.GetAsync(CancellationToken.None)).KeepComputerAwake);
        await repository.SaveAsync(PlatformSettingsPatchMerger.Apply(await repository.GetAsync(CancellationToken.None), new PatchPlatformSettingsRequest(KeepComputerAwake: true)), CancellationToken.None);
        Assert.True((await repository.GetAsync(CancellationToken.None)).KeepComputerAwake);
        await repository.SaveAsync(PlatformSettingsPatchMerger.Apply(await repository.GetAsync(CancellationToken.None), new PatchPlatformSettingsRequest(UiTheme: "light")), CancellationToken.None);
        Assert.True((await repository.GetAsync(CancellationToken.None)).KeepComputerAwake);
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

    /// <summary>
    /// #860: the zone episode air times convert into for display. Empty (no
    /// zone chosen) is the default, so an existing install falls back to the
    /// browser/OS zone rather than being silently switched to a stored one.
    /// A garbage value clears back to empty rather than being stored as a
    /// zone that will make every future format call throw.
    /// </summary>
    [Fact]
    public async Task Timezone_persists_and_a_value_the_host_cannot_recognise_clears_to_empty()
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
        Assert.Equal(string.Empty, beforeSave.Timezone);

        var current = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(Timezone: "Australia/Sydney")),
            CancellationToken.None);
        Assert.Equal("Australia/Sydney", (await repository.GetAsync(CancellationToken.None)).Timezone);

        current = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(Timezone: "Not/A_Real_Zone")),
            CancellationToken.None);
        Assert.Equal(string.Empty, (await repository.GetAsync(CancellationToken.None)).Timezone);
    }

    [Fact]
    public async Task ProperRepackAcquisitionMode_persists_and_defaults_to_prefer_and_upgrade()
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
        Assert.Equal("prefer-and-upgrade", beforeSave.ProperRepackAcquisitionMode);

        var current = await repository.GetAsync(CancellationToken.None);
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                current,
                new PatchPlatformSettingsRequest(ProperRepackAcquisitionMode: "do-not-upgrade-automatically")),
            CancellationToken.None);

        Assert.Equal("do-not-upgrade-automatically", (await repository.GetAsync(CancellationToken.None)).ProperRepackAcquisitionMode);

        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                await repository.GetAsync(CancellationToken.None),
                new PatchPlatformSettingsRequest(ProperRepackAcquisitionMode: "not-a-real-mode")),
            CancellationToken.None);

        Assert.Equal("prefer-and-upgrade", (await repository.GetAsync(CancellationToken.None)).ProperRepackAcquisitionMode);
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

    /// <summary>#828: Deluno's own event/strike history window, same pattern as ImportRecoveryRetentionDays.</summary>
    [Fact]
    public async Task HistoryRetentionDays_defaults_to_365_and_persists_within_its_bounds()
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

        Assert.Equal(365, (await repository.GetAsync(CancellationToken.None)).HistoryRetentionDays);

        await repository.SaveAsync(MinimalRequest(90), CancellationToken.None);
        Assert.Equal(90, (await repository.GetAsync(CancellationToken.None)).HistoryRetentionDays);

        // Out of range in either direction clamps rather than fails.
        await repository.SaveAsync(MinimalRequest(0), CancellationToken.None);
        Assert.Equal(1, (await repository.GetAsync(CancellationToken.None)).HistoryRetentionDays);

        await repository.SaveAsync(MinimalRequest(999_999), CancellationToken.None);
        Assert.Equal(3650, (await repository.GetAsync(CancellationToken.None)).HistoryRetentionDays);
    }

    private static UpdatePlatformSettingsRequest MinimalRequest(int historyRetentionDays)
        => new(
            MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null,
            IncompleteDownloadsPath: null, AutoStartJobs: false, EnableNotifications: false, RenameOnImport: false,
            UseHardlinks: false, CleanupEmptyFolders: false,
            MovieFolderFormat: null, SeriesFolderFormat: null, MovieFileFormat: null, EpisodeFileFormat: null,
            HostBindAddress: null, HostPort: 0, UrlBase: null, RequireAuthentication: false, UiTheme: null,
            DefaultMovieView: null, DefaultShowView: null,
            MetadataCertificationCountry: null, MetadataLanguage: null, MetadataProviderMode: null,
            MetadataBrokerUrl: null, MetadataTmdbApiKey: null, MetadataOmdbApiKey: null, ReleaseNeverGrabPatterns: null,
            HistoryRetentionDays: historyRetentionDays);

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

    /// <summary>#1002: off for a new install and for an existing one (a missing key reads as off); the limit defaults to 20 and is clamped.</summary>
    [Fact]
    public async Task PauseSearchWhenQueueLong_is_off_by_default_and_saves_its_limit()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var fresh = await repository.GetAsync(CancellationToken.None);
        Assert.False(fresh.PauseSearchWhenQueueLongEnabled);
        Assert.Equal(20, fresh.PauseSearchWhenQueueLongThreshold);

        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(fresh, new PatchPlatformSettingsRequest(PauseSearchWhenQueueLongEnabled: true, PauseSearchWhenQueueLongThreshold: 7)),
            CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.True(loaded.PauseSearchWhenQueueLongEnabled);
        Assert.Equal(7, loaded.PauseSearchWhenQueueLongThreshold);
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

    /// <summary>
    /// #932/#936: the decision sheet's own row for this said "Default on",
    /// but #936 is James's later, standing rule - every automatic behaviour,
    /// this one included, starts off for a new install and is asked in the
    /// setup guide instead. The age threshold still defaults to 24 hours
    /// (it only ever matters once the switch is on), and both save through
    /// the same PATCH path as every other setting.
    /// </summary>
    [Fact]
    public async Task LeftoverDownloadCleanup_defaults_to_off_at_24_hours_and_both_are_configurable()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.False(defaults.LeftoverDownloadCleanupEnabled);
        Assert.Equal(24, defaults.LeftoverDownloadCleanupAgeHours);

        var merged = PlatformSettingsPatchMerger.Apply(
            defaults,
            new PatchPlatformSettingsRequest(LeftoverDownloadCleanupEnabled: true, LeftoverDownloadCleanupAgeHours: 72));
        await repository.SaveAsync(merged, CancellationToken.None);

        var updated = await repository.GetAsync(CancellationToken.None);
        Assert.True(updated.LeftoverDownloadCleanupEnabled);
        Assert.Equal(72, updated.LeftoverDownloadCleanupAgeHours);
    }

    /// <summary>#1001: an untouched install has no skip list and the 2-hour minimum age leftovers always had.</summary>
    [Fact]
    public async Task LeftoverExcludeList_and_min_age_default_to_what_leftovers_always_did_and_are_configurable()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(string.Empty, defaults.LeftoverExcludePatterns);
        Assert.Equal(2, defaults.LeftoverMinAgeHours);

        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(defaults, new PatchPlatformSettingsRequest(LeftoverExcludePatterns: "*.nfo, Thumbs.db", LeftoverMinAgeHours: 12)),
            CancellationToken.None);

        var updated = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("*.nfo, Thumbs.db", updated.LeftoverExcludePatterns);
        Assert.Equal(12, updated.LeftoverMinAgeHours);
        // Nothing else was switched on by saving them.
        Assert.False(updated.LeftoverDownloadCleanupEnabled);
    }

    /// <summary>#1013: Force Import is off on a fresh (or upgraded) install, with 3 tries, and both are configurable.</summary>
    [Fact]
    public async Task ForceImport_defaults_to_off_with_three_tries_and_is_configurable()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.False(defaults.ForceImportEnabled);
        Assert.Equal(3, defaults.ForceImportMaxTries);
        Assert.Equal(100, defaults.ForceImportMinimumMegabytes);

        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(defaults, new PatchPlatformSettingsRequest(ForceImportEnabled: true, ForceImportMaxTries: 7, ForceImportMinimumMegabytes: 20)),
            CancellationToken.None);

        var updated = await repository.GetAsync(CancellationToken.None);
        Assert.True(updated.ForceImportEnabled);
        Assert.Equal(7, updated.ForceImportMaxTries);
        Assert.Equal(20, updated.ForceImportMinimumMegabytes);
        // An emptied box means the 100 MB default, never 1 MB and never the old value.
        // An emptied box never saves 1 MB: nothing sent (0) leaves the size as it was.
        Assert.Equal(100, PlatformSettingsPatchMerger.Apply(updated, new PatchPlatformSettingsRequest(ForceImportMinimumMegabytes: 0)).ForceImportMinimumMegabytes);

        // A patch that mentions neither leaves both as they were.
        await repository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(updated, new PatchPlatformSettingsRequest(LeftoverMinAgeHours: 5)),
            CancellationToken.None);
        var untouched = await repository.GetAsync(CancellationToken.None);
        Assert.True(untouched.ForceImportEnabled);
        Assert.Equal(7, untouched.ForceImportMaxTries);
        Assert.Equal(20, untouched.ForceImportMinimumMegabytes);
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

    /// <summary>#762: blank for both by default (today's one flat series folder), and each round-trips independently through a patch.</summary>
    [Fact]
    public async Task SeasonAndSpecialsFolderFormat_default_to_blank_and_round_trip_independently()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(string.Empty, defaults.SeasonFolderFormat);
        Assert.Equal(string.Empty, defaults.SpecialsFolderFormat);

        var merged = PlatformSettingsPatchMerger.Apply(
            defaults,
            new PatchPlatformSettingsRequest(SeasonFolderFormat: "Season {Season:00}"));
        await repository.SaveAsync(merged, CancellationToken.None);

        var afterSeason = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("Season {Season:00}", afterSeason.SeasonFolderFormat);
        Assert.Equal(string.Empty, afterSeason.SpecialsFolderFormat);

        var mergedSpecials = PlatformSettingsPatchMerger.Apply(
            afterSeason,
            new PatchPlatformSettingsRequest(SpecialsFolderFormat: "Specials"));
        await repository.SaveAsync(mergedSpecials, CancellationToken.None);

        var reloaded = await SettingsAsync(storage); // a fresh instance: nothing survives but the database
        var loaded = await reloaded.GetAsync(CancellationToken.None);
        Assert.Equal("Season {Season:00}", loaded.SeasonFolderFormat);
        Assert.Equal("Specials", loaded.SpecialsFolderFormat);
    }

    /// <summary>#764: independent of RenameOnImport, off by default for a new install, and stays on once explicitly turned on.</summary>
    [Fact]
    public async Task RenameFoldersOnImport_defaults_to_off_and_is_independent_of_RenameOnImport()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var defaults = await repository.GetAsync(CancellationToken.None);
        Assert.False(defaults.RenameFoldersOnImport);
        Assert.False(defaults.RenameOnImport);

        var mergedFoldersOnly = PlatformSettingsPatchMerger.Apply(
            defaults,
            new PatchPlatformSettingsRequest(RenameFoldersOnImport: true));
        await repository.SaveAsync(mergedFoldersOnly, CancellationToken.None);

        var afterFolders = await repository.GetAsync(CancellationToken.None);
        Assert.True(afterFolders.RenameFoldersOnImport);
        // Untouched by that patch.
        Assert.False(afterFolders.RenameOnImport);

        var mergedFilesToo = PlatformSettingsPatchMerger.Apply(
            afterFolders,
            new PatchPlatformSettingsRequest(RenameOnImport: true));
        await repository.SaveAsync(mergedFilesToo, CancellationToken.None);

        var reloaded = await SettingsAsync(storage); // a fresh instance: nothing survives but the database
        var loaded = await reloaded.GetAsync(CancellationToken.None);
        Assert.True(loaded.RenameFoldersOnImport);
        Assert.True(loaded.RenameOnImport);
    }

    /// <summary>
    /// #764: before this, the destination folder was always computed from
    /// the naming template -- there was no way to keep a release's own
    /// folder name. V0084RenameFoldersOnImportDefaultsExisting must freeze
    /// that behaviour for any install that already had settings saved, the
    /// same way V0073AutomationDefaultsOff froze RenameOnImport (#936). A
    /// brand-new install (nothing here yet when V0084 runs) reads the new
    /// switch's missing key as off instead.
    /// </summary>
    [Fact]
    public async Task An_upgraded_install_keeps_todays_folder_naming_after_the_rename_folders_migration_runs()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T01:02:03Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);

        // The install as it stood before #764 shipped: every migration up to
        // V0082, plus at least one setting saved -- an install with zero rows
        // here has never actually been configured, and is treated as new even
        // if it happens to run this migration late. Written as a direct row,
        // not through SqlitePlatformSettingsRepository.SaveAsync: that method
        // already knows about media.renameFoldersOnImport and would write its
        // own "false" for it immediately, which is not what a database still
        // running the pre-#764 code could ever have done.
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            PlatformDatabaseMigrations.All.Where(migration => migration.Version < 84).ToArray(),
            CancellationToken.None);
        await using (var preUpgradeConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None))
        await using (var insert = preUpgradeConnection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO system_settings (setting_key, setting_value, updated_utc) VALUES ('app.instanceName', 'My Deluno', '2026-01-01T00:00:00Z');";
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // The upgrade: the rest of the migrations, including V0084, run
        // against that already-configured database.
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, PlatformDatabaseMigrations.All, CancellationToken.None);

        var upgraded = await new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage))
            .GetAsync(CancellationToken.None);
        Assert.True(upgraded.RenameFoldersOnImport);
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

        // #1034 split the guide into two stages, so it now has 13 steps
        // (indices 0-12); a later step moved the clamp from 11 to 12.
        Assert.Equal(12, saved.LastCompletedStep);
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
            MovieDownloadsPath: "D:\\Downloads\\Completed\\Movies",
            QualityPreset: "balanced1080p",
            FormatGoal: "balanced",
            IndexerUrl: "https://indexer.example/api",
            ClientHost: "download-client.local",
            FirstTitle: "Example Movie",
            SkipSignInLocalNetwork: false,
            UpdateAutomatically: true,
            Automation: new Dictionary<string, bool> { ["rssGrabs"] = true, ["forceImport"] = false }), CancellationToken.None);

        var loaded = await repository.GetSetupDraftAsync(CancellationToken.None);
        Assert.Equal(saved.UpdatedUtc, loaded.UpdatedUtc);
        Assert.Equal("D:\\Downloads\\Completed\\Movies", loaded.MovieDownloadsPath);
        Assert.Equal(false, loaded.SkipSignInLocalNetwork);
        Assert.Equal(true, loaded.UpdateAutomatically);
        Assert.True(loaded.Automation!["rssGrabs"]);
        Assert.False(loaded.Automation["forceImport"]);
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
    public async Task Download_health_rules_default_to_off_with_the_original_numbers()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var loaded = await repository.GetAsync(CancellationToken.None);

        // #1194: nothing is on for a fresh install.
        Assert.False(loaded.DownloadHealthNoProgressEnabled);
        Assert.Equal(30, loaded.DownloadHealthNoProgressMinutes);
        Assert.False(loaded.DownloadHealthMinSpeedEnabled);
        Assert.Equal(50, loaded.DownloadHealthMinSpeedKbps);
        Assert.Equal(30, loaded.DownloadHealthMinSpeedMinutes);
        Assert.False(loaded.DownloadHealthMinPeersEnabled);
        Assert.Equal(1, loaded.DownloadHealthMinPeers);
        Assert.Equal(30, loaded.DownloadHealthMinPeersMinutes);
        // #818: off by default (#936), with Cleanuparr's own hour as the starting grace period.
        Assert.False(loaded.DownloadHealthDeadTorrentEnabled);
        Assert.Equal(60, loaded.DownloadHealthDeadTorrentGraceMinutes);
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
                DownloadHealthMinPeersMinutes: 20,
                DownloadHealthDeadTorrentEnabled: true,
                DownloadHealthDeadTorrentGraceMinutes: 90));
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
        Assert.True(loaded.DownloadHealthDeadTorrentEnabled);
        Assert.Equal(90, loaded.DownloadHealthDeadTorrentGraceMinutes);
    }

    /// <summary>
    /// #818: the Dead torrent rule's switch and grace period are ordinary
    /// settings, the same way the original three rules (#659) are, and
    /// <see cref="SqliteDownloadHealthRepository.GetDownloadHealthRulesAsync"/>
    /// synthesizes the equivalent rule from them the first time an
    /// installation reads its rules - so an existing install stays exactly
    /// off (#936), and a new one becomes configurable from Settings ->
    /// Download health without a separate rule-editor screen.
    /// </summary>
    [Fact]
    public async Task Off_by_default_dead_torrent_setting_synthesizes_a_disabled_rule_with_the_shipped_grace_period()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);
        var health = new SqliteDownloadHealthRepository(storage.Factory, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T01:02:03Z")));

        var rules = await health.GetDownloadHealthRulesAsync(await repository.GetAsync(CancellationToken.None), CancellationToken.None);

        var rule = Assert.Single(rules, rule => rule.Kind == DownloadHealthRuleKinds.DeadTorrent);
        Assert.False(rule.Enabled);
        Assert.Equal(60, rule.Threshold);
    }

    [Fact]
    public async Task Turning_the_dead_torrent_setting_on_with_a_custom_grace_period_produces_an_enabled_rule()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);
        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(DownloadHealthDeadTorrentEnabled: true, DownloadHealthDeadTorrentGraceMinutes: 15));
        await repository.SaveAsync(merged, CancellationToken.None);
        var health = new SqliteDownloadHealthRepository(storage.Factory, new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T01:02:03Z")));

        var rules = await health.GetDownloadHealthRulesAsync(await repository.GetAsync(CancellationToken.None), CancellationToken.None);

        var rule = Assert.Single(rules, rule => rule.Kind == DownloadHealthRuleKinds.DeadTorrent);
        Assert.True(rule.Enabled);
        Assert.Equal(15, rule.Threshold);
    }

    /// <summary>
    /// #829/#936: an install that never opens this setting keeps the
    /// instance-wide hourly search cap off.
    /// </summary>
    [Fact]
    public async Task Hourly_search_cap_defaults_to_off()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.False(loaded.MaxSearchesPerHourEnabled);
        Assert.Equal(200, loaded.MaxSearchesPerHour);
    }

    [Fact]
    public async Task Hourly_search_cap_round_trips_through_a_patch_and_survives_being_reloaded()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(
                MaxSearchesPerHourEnabled: true,
                MaxSearchesPerHour: 75));
        await repository.SaveAsync(merged, CancellationToken.None);

        var reloaded = await SettingsAsync(storage);
        var loaded = await reloaded.GetAsync(CancellationToken.None);

        Assert.True(loaded.MaxSearchesPerHourEnabled);
        Assert.Equal(75, loaded.MaxSearchesPerHour);
    }

    /// <summary>
    /// #888: "hold" (today's only behaviour) is every install's default until
    /// somebody chooses otherwise, existing or new.
    /// </summary>
    [Fact]
    public async Task UnmatchedImportPolicy_defaults_to_hold_for_seven_days()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var loaded = await repository.GetAsync(CancellationToken.None);

        Assert.Equal(UnmatchedImportPolicies.Hold, loaded.UnmatchedImportPolicy);
        Assert.Equal(7, loaded.UnmatchedImportGiveUpDays);
    }

    [Fact]
    public async Task UnmatchedImportPolicy_round_trips_through_a_patch_and_survives_being_reloaded()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(
                UnmatchedImportPolicy: UnmatchedImportPolicies.GiveUpAfterDays,
                UnmatchedImportGiveUpDays: 3));
        await repository.SaveAsync(merged, CancellationToken.None);

        var reloaded = await SettingsAsync(storage);
        var loaded = await reloaded.GetAsync(CancellationToken.None);

        Assert.Equal(UnmatchedImportPolicies.GiveUpAfterDays, loaded.UnmatchedImportPolicy);
        Assert.Equal(3, loaded.UnmatchedImportGiveUpDays);
    }

    [Fact]
    public async Task UnmatchedImportPolicy_ignores_an_unknown_value_and_falls_back_to_hold()
    {
        using var storage = TestStorage.Create();
        var repository = await SettingsAsync(storage);

        var merged = PlatformSettingsPatchMerger.Apply(
            await repository.GetAsync(CancellationToken.None),
            new PatchPlatformSettingsRequest(UnmatchedImportPolicy: "not-a-real-policy"));
        await repository.SaveAsync(merged, CancellationToken.None);

        var loaded = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(UnmatchedImportPolicies.Hold, loaded.UnmatchedImportPolicy);
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
                new PatchPlatformSettingsRequest(MetadataLanguage: "fr")),
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
        Assert.Equal("fr", loaded.MetadataLanguage);
        Assert.Equal("dark", loaded.UiTheme);
    }
}
