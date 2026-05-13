using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
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

        var repository = new SqlitePlatformSettingsRepository(
            storage.Factory,
            timeProvider,
            TestSecretProtection.Create(storage));

        var movieLibrary = await repository.CreateLibraryAsync(
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

        var created = await repository.CreateIntakeSourceAsync(
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
                SyncIntervalHours: 12,
                SearchOnAdd: true,
                IsEnabled: true),
            CancellationToken.None);

        Assert.Equal("Action, Sci-Fi", created.RequiredGenres);
        Assert.Equal(7.5, created.MinimumRating);
        Assert.Equal(2018, created.MinimumYear);
        Assert.Equal(3650, created.MaximumAgeDays);
        Assert.Equal("PG-13, R", created.AllowedCertifications);
        Assert.Equal("any", created.Audience);
        Assert.Equal(12, created.SyncIntervalHours);
        Assert.Null(created.LastSyncUtc);
        Assert.Equal("never", created.LastSyncStatus);

        var updated = await repository.UpdateIntakeSourceAsync(
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
                SyncIntervalHours: 6,
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
        Assert.Equal(6, updated.SyncIntervalHours);

        var synced = await repository.RecordIntakeSourceSyncResultAsync(
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
}
