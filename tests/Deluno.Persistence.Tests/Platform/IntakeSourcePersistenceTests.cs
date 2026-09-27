using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

public sealed class IntakeSourcePersistenceTests
{
    [Fact]
    public async Task IntakeSource_round_trips_filters_and_sync_diagnostics()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:02:03Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var librariesRepository = new SqliteLibrariesRepository(
            storage.Factory,
            timeProvider);

        var intakeRepository = new SqliteIntakeRepository(
            storage.Factory,
            timeProvider);

        var movieLibrary = await librariesRepository.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Test Movies",
                MediaType: "movies",
                Purpose: "General",
                RootPath: @"D:\Media\Movies",
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: 360,
                ProcessorFailureMode: "block",
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        var created = await intakeRepository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                Name: "IMDb Watchlist",
                Provider: "imdb",
                FeedUrl: "https://www.imdb.com/list/ls123456789/",
                MediaType: "movies",
                LibraryId: movieLibrary.Id,
                QualityProfileId: null,
                RequiredGenres: "Action, Sci-Fi",
                MinimumRating: 7.5,
                MinimumYear: 2018,
                MaximumAgeDays: 3650,
                AllowedCertifications: "PG-13, R",
                Audience: "any",
                SyncIntervalHours: 720,
                SearchOnAdd: true,
                IsEnabled: true),
            CancellationToken.None);

        Assert.Equal("Action, Sci-Fi", created.RequiredGenres);
        Assert.Equal(7.5, created.MinimumRating);
        Assert.Equal(2018, created.MinimumYear);
        Assert.Equal(3650, created.MaximumAgeDays);
        Assert.Equal("PG-13, R", created.AllowedCertifications);
        Assert.Equal("any", created.Audience);
        Assert.Equal(720, created.SyncIntervalHours);
        Assert.Null(created.LastSyncUtc);
        Assert.Equal("never", created.LastSyncStatus);

        var updated = await intakeRepository.UpdateIntakeSourceAsync(
            created.Id,
            new UpdateIntakeSourceRequest(
                Name: created.Name,
                Provider: created.Provider,
                FeedUrl: created.FeedUrl,
                MediaType: created.MediaType,
                LibraryId: created.LibraryId,
                QualityProfileId: created.QualityProfileId,
                RequiredGenres: "Action, Thriller",
                MinimumRating: 8.1,
                MinimumYear: 2020,
                MaximumAgeDays: 720,
                AllowedCertifications: "R",
                Audience: "adult",
                SyncIntervalHours: 720,
                SearchOnAdd: created.SearchOnAdd,
                IsEnabled: created.IsEnabled),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Action, Thriller", updated!.RequiredGenres);
        Assert.Equal(8.1, updated.MinimumRating);
        Assert.Equal(2020, updated.MinimumYear);
        Assert.Equal(720, updated.MaximumAgeDays);
        Assert.Equal("R", updated.AllowedCertifications);
        Assert.Equal("adult", updated.Audience);
        Assert.Equal(720, updated.SyncIntervalHours);

        var reloaded = await intakeRepository.GetIntakeSourceAsync(created.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(720, reloaded!.SyncIntervalHours);

        var synced = await intakeRepository.RecordIntakeSourceSyncResultAsync(
            created.Id,
            DateTimeOffset.Parse("2026-05-14T05:00:00Z"),
            "success",
            "Fetched 10, added 3, skipped 7.",
            CancellationToken.None);

        Assert.NotNull(synced);
        Assert.Equal("success", synced!.LastSyncStatus);
        Assert.Equal("Fetched 10, added 3, skipped 7.", synced.LastSyncSummary);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T05:00:00Z"), synced.LastSyncUtc);
    }

    [Fact]
    public async Task IntakeSource_round_trips_monitor_mode_series_type_and_season_folder()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var intakeRepository = new SqliteIntakeRepository(storage.Factory, timeProvider);

        // #848/#850: an unset source (as every source created before these
        // fields existed would be) keeps today's fully-monitored, no-override
        // behaviour.
        var plainMovie = await intakeRepository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                Name: "Plain movie list",
                Provider: "url-list",
                FeedUrl: "https://lists.deluno.test/movies.txt",
                MediaType: "movies",
                LibraryId: null,
                QualityProfileId: null,
                RequiredGenres: null,
                MinimumRating: null,
                MinimumYear: null,
                MaximumAgeDays: null,
                AllowedCertifications: null,
                Audience: null,
                SyncIntervalHours: 24,
                SearchOnAdd: true,
                IsEnabled: true),
            CancellationToken.None);
        Assert.Equal("movie", plainMovie.MonitorMode);
        Assert.Null(plainMovie.SeriesType);
        Assert.Null(plainMovie.SeasonFolder);

        var unmonitoredMovie = await intakeRepository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                Name: "Unmonitored movie list",
                Provider: "url-list",
                FeedUrl: "https://lists.deluno.test/movies2.txt",
                MediaType: "movies",
                LibraryId: null,
                QualityProfileId: null,
                RequiredGenres: null,
                MinimumRating: null,
                MinimumYear: null,
                MaximumAgeDays: null,
                AllowedCertifications: null,
                Audience: null,
                SyncIntervalHours: 24,
                SearchOnAdd: true,
                IsEnabled: true,
                MonitorMode: "none"),
            CancellationToken.None);
        Assert.Equal("none", unmonitoredMovie.MonitorMode);

        var tvSource = await intakeRepository.CreateIntakeSourceAsync(
            new CreateIntakeSourceRequest(
                Name: "Anime watchlist",
                Provider: "url-list",
                FeedUrl: "https://lists.deluno.test/anime.txt",
                MediaType: "tv",
                LibraryId: null,
                QualityProfileId: null,
                RequiredGenres: null,
                MinimumRating: null,
                MinimumYear: null,
                MaximumAgeDays: null,
                AllowedCertifications: null,
                Audience: null,
                SyncIntervalHours: 24,
                SearchOnAdd: true,
                IsEnabled: true,
                MonitorMode: "future",
                SeriesType: "anime",
                SeasonFolder: true),
            CancellationToken.None);
        Assert.Equal("future", tvSource.MonitorMode);
        Assert.Equal("anime", tvSource.SeriesType);
        Assert.True(tvSource.SeasonFolder);

        var reloadedTv = await intakeRepository.GetIntakeSourceAsync(tvSource.Id, CancellationToken.None);
        Assert.NotNull(reloadedTv);
        Assert.Equal("future", reloadedTv!.MonitorMode);
        Assert.Equal("anime", reloadedTv.SeriesType);
        Assert.True(reloadedTv.SeasonFolder);

        var updatedTv = await intakeRepository.UpdateIntakeSourceAsync(
            tvSource.Id,
            new UpdateIntakeSourceRequest(
                Name: tvSource.Name,
                Provider: tvSource.Provider,
                FeedUrl: tvSource.FeedUrl,
                MediaType: tvSource.MediaType,
                LibraryId: tvSource.LibraryId,
                QualityProfileId: tvSource.QualityProfileId,
                RequiredGenres: tvSource.RequiredGenres,
                MinimumRating: tvSource.MinimumRating,
                MinimumYear: tvSource.MinimumYear,
                MaximumAgeDays: tvSource.MaximumAgeDays,
                AllowedCertifications: tvSource.AllowedCertifications,
                Audience: tvSource.Audience,
                SyncIntervalHours: tvSource.SyncIntervalHours,
                SearchOnAdd: tvSource.SearchOnAdd,
                IsEnabled: tvSource.IsEnabled,
                MonitorMode: "recent",
                SeriesType: "daily",
                SeasonFolder: false),
            CancellationToken.None);
        Assert.NotNull(updatedTv);
        Assert.Equal("recent", updatedTv!.MonitorMode);
        Assert.Equal("daily", updatedTv.SeriesType);
        Assert.False(updatedTv.SeasonFolder);
    }
}
