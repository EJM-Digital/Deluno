using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
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
                ReleaseNeverGrabPatterns: "cam\nhardcoded subs"),
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
        Assert.Equal("cam\nhardcoded subs", loaded.ReleaseNeverGrabPatterns);
    }
}
