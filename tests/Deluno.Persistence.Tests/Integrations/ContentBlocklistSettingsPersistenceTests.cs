using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #816's content-blocklist settings, #879's dangerous-file legacy migration,
/// and #822's qBittorrent sync-state bookkeeping - all off/empty until saved
/// once (#936).
/// </summary>
public sealed class ContentBlocklistSettingsPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    private static async Task<(SqliteDownloadHealthRepository Repository, TestStorage Storage)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (new SqliteDownloadHealthRepository(storage.Factory, time), storage);
    }

    [Fact]
    public async Task GetContentBlocklistSettingsAsync_WithNothingSaved_ReturnsTheOffDefault()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var settings = await repository.GetContentBlocklistSettingsAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
        Assert.Equal(ContentBlocklistModes.Blacklist, settings.Mode);
        Assert.True(settings.UseBuiltInPatterns);
        Assert.Empty(settings.CustomPatterns);
    }

    [Fact]
    public async Task SaveContentBlocklistSettingsAsync_PersistsAndRoundTrips()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var saved = await repository.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with
            {
                Enabled = true,
                Mode = ContentBlocklistModes.Whitelist,
                CustomPatterns = ["*.mkv"],
                RemoteUrl = "https://example.test/blocklist.txt"
            },
            CancellationToken.None);

        Assert.True(saved.Enabled);
        var reloaded = await repository.GetContentBlocklistSettingsAsync(CancellationToken.None);
        Assert.True(reloaded.Enabled);
        Assert.Equal(ContentBlocklistModes.Whitelist, reloaded.Mode);
        Assert.Equal(["*.mkv"], reloaded.CustomPatterns);
        Assert.Equal("https://example.test/blocklist.txt", reloaded.RemoteUrl);
    }

    [Fact]
    public async Task SaveContentBlocklistSettingsAsync_NeverOverwritesTheLastRemoteRefresh()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await repository.RecordContentBlocklistRemoteRefreshAsync(["*.remote"], CancellationToken.None);
        var saved = await repository.SaveContentBlocklistSettingsAsync(
            ContentBlocklistSettings.Default with { Enabled = true, CustomPatterns = ["*.custom"] },
            CancellationToken.None);

        Assert.Equal(["*.remote"], saved.RemotePatterns);
        Assert.NotNull(saved.LastRefreshedUtc);
    }

    [Fact]
    public async Task RecordContentBlocklistRemoteRefreshAsync_ReplacesRemotePatternsAndStampsTheTime()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var updated = await repository.RecordContentBlocklistRemoteRefreshAsync(["*.exe", "*.scr"], CancellationToken.None);

        Assert.Equal(["*.exe", "*.scr"], updated.RemotePatterns);
        Assert.Equal(Now, updated.LastRefreshedUtc);
    }

    [Fact]
    public async Task GetDownloadHealthRulesAsync_MigratesTheLegacyDangerousFileToggle()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var legacyOn = new PlatformSettingsSnapshot(
            AppInstanceName: "test", MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null,
            IncompleteDownloadsPath: null, AutoStartJobs: false, EnableNotifications: false, RenameOnImport: false,
            UseHardlinks: false, CleanupEmptyFolders: false, MovieFolderFormat: "",
            SeriesFolderFormat: "", MovieFileFormat: "", EpisodeFileFormat: "", HostBindAddress: "", HostPort: 0,
            UrlBase: "", RequireAuthentication: false, UiTheme: "", DefaultMovieView: "", DefaultShowView: "",
            MetadataCertificationCountry: "",
            MetadataLanguage: "", MetadataProviderMode: "", MetadataBrokerUrl: "", MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false, ReleaseNeverGrabPatterns: "",
            SearchScoringMode: "", ImportRecoveryRetentionDays: 30, UpdatedUtc: Now,
            DownloadHealthDangerousFileEnabled: true);

        var rules = await repository.GetDownloadHealthRulesAsync(legacyOn, CancellationToken.None);

        var dangerousFileRule = Assert.Single(rules, rule => rule.Kind == DownloadHealthRuleKinds.DangerousFile);
        Assert.True(dangerousFileRule.Enabled);
        Assert.Equal(1, dangerousFileRule.StrikeLimit);
    }

    [Fact]
    public async Task GetDownloadHealthRulesAsync_LegacyToggleOff_MigratesAsDisabled()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        var legacyOff = new PlatformSettingsSnapshot(
            AppInstanceName: "test", MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null,
            IncompleteDownloadsPath: null, AutoStartJobs: false, EnableNotifications: false, RenameOnImport: false,
            UseHardlinks: false, CleanupEmptyFolders: false, MovieFolderFormat: "",
            SeriesFolderFormat: "", MovieFileFormat: "", EpisodeFileFormat: "", HostBindAddress: "", HostPort: 0,
            UrlBase: "", RequireAuthentication: false, UiTheme: "", DefaultMovieView: "", DefaultShowView: "",
            MetadataCertificationCountry: "",
            MetadataLanguage: "", MetadataProviderMode: "", MetadataBrokerUrl: "", MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false, ReleaseNeverGrabPatterns: "",
            SearchScoringMode: "", ImportRecoveryRetentionDays: 30, UpdatedUtc: Now);

        var rules = await repository.GetDownloadHealthRulesAsync(legacyOff, CancellationToken.None);

        var dangerousFileRule = Assert.Single(rules, rule => rule.Kind == DownloadHealthRuleKinds.DangerousFile);
        Assert.False(dangerousFileRule.Enabled);
    }

    [Fact]
    public async Task QbittorrentBlocklistSyncState_RecordsAndListsPerClient()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await repository.RecordQbittorrentBlocklistSyncAsync("client-1", "hash-a", true, null, CancellationToken.None);
        await repository.RecordQbittorrentBlocklistSyncAsync("client-2", "hash-a", false, "qBittorrent returned 403.", CancellationToken.None);

        var states = await repository.ListQbittorrentBlocklistSyncStatesAsync(CancellationToken.None);

        Assert.Equal(2, states.Count);
        Assert.True(states["client-1"].Succeeded);
        Assert.False(states["client-2"].Succeeded);
        Assert.Equal("qBittorrent returned 403.", states["client-2"].Message);
    }

    [Fact]
    public async Task QbittorrentBlocklistSyncState_RecordingAgainReplacesThePriorEntryForThatClient()
    {
        var (repository, storage) = await CreateAsync();
        using var _ = storage;

        await repository.RecordQbittorrentBlocklistSyncAsync("client-1", "hash-a", true, null, CancellationToken.None);
        await repository.RecordQbittorrentBlocklistSyncAsync("client-1", "hash-b", true, null, CancellationToken.None);

        var states = await repository.ListQbittorrentBlocklistSyncStatesAsync(CancellationToken.None);

        Assert.Single(states);
        Assert.Equal("hash-b", states["client-1"].BlocklistHash);
    }
}
