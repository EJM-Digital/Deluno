using System.Reflection;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #828: <see cref="HistoryRetentionWorker"/> prunes Deluno's own Activity and
/// download-health history to the person's configured
/// <see cref="PlatformSettingsSnapshot.HistoryRetentionDays"/>, once a day.
/// Nothing referenced this worker at all - not even that it reads the
/// configured window rather than some fixed number, and passes the same
/// cutoff to both stores.
/// </summary>
public sealed class HistoryRetentionWorkerPruneTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(9000)] // #828: clamped to the 1..3650 day range, not passed through as-is.
    public async Task Pruning_uses_the_configured_retention_window_for_both_stores(int configuredDays)
    {
        var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
        var timeProvider = new FixedTimeProvider(now);

        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSettings(configuredDays));

        DateTimeOffset? activityCutoff = null;
        DateTimeOffset? healthCutoff = null;

        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((cutoff, _) => activityCutoff = cutoff)
            .ReturnsAsync(3);

        var health = new Mock<IDownloadHealthRepository>();
        health.Setup(repository => repository.PruneDownloadHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((cutoff, _) => healthCutoff = cutoff)
            .ReturnsAsync(2);

        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(activity.Object);
        services.AddSingleton(health.Object);
        await using var provider = services.BuildServiceProvider();

        var worker = new HistoryRetentionWorker(
            NullLogger<HistoryRetentionWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider);

        // PruneAsync is the private loop body: the public surface is a
        // BackgroundService whose ExecuteAsync only fires on a timer, which is
        // not what the acceptance line is about — the pruning logic itself is.
        var pruneMethod = typeof(HistoryRetentionWorker).GetMethod("PruneAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(pruneMethod);
        await (Task)pruneMethod!.Invoke(worker, [CancellationToken.None])!;

        var clampedDays = Math.Clamp(configuredDays, 1, 3650);
        var expectedCutoff = now - TimeSpan.FromDays(clampedDays);

        Assert.NotNull(activityCutoff);
        Assert.NotNull(healthCutoff);
        Assert.Equal(expectedCutoff, activityCutoff!.Value);
        Assert.Equal(expectedCutoff, healthCutoff!.Value);

        activity.Verify(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        health.Verify(repository => repository.PruneDownloadHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_prune_that_throws_is_logged_and_the_next_day_prunes_again_and_shutdown_during_the_wait_ends_the_loop()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
        var calls = 0;
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>())).Returns(() =>
            ++calls == 1
                ? Task.FromException<PlatformSettingsSnapshot>(new IOException("The database is busy."))
                : Task.FromResult(DefaultSettings(30)));
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var health = new Mock<IDownloadHealthRepository>();
        health.Setup(repository => repository.PruneDownloadHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(activity.Object);
        services.AddSingleton(health.Object);
        await using var provider = services.BuildServiceProvider();
        var worker = new HistoryRetentionWorker(NullLogger<HistoryRetentionWorker>.Instance, provider.GetRequiredService<IServiceScopeFactory>(), clock);

        await worker.StartAsync(CancellationToken.None);
        // The clock moves a minute at a time until the worker's own wait has run out, however soon it began waiting.
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMinutes(1)); return calls == 1; }, "the first prune to be tried and to fail");
        activity.Verify(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);

        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromHours(1)); return health.Invocations.Count > 0; }, "the next day's prune");
        await worker.StopAsync(CancellationToken.None);

        activity.Verify(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(worker.ExecuteTask is { IsCompleted: true });
    }

    private static PlatformSettingsSnapshot DefaultSettings(int historyRetentionDays) => new(
        MovieRootPath: null,
        SeriesRootPath: null,
        DownloadsPath: null,
        IncompleteDownloadsPath: null,
        AutoStartJobs: true,
        EnableNotifications: true,
        RenameOnImport: true,
        UseHardlinks: true,
        CleanupEmptyFolders: true,
        MovieFolderFormat: "{Movie}",
        SeriesFolderFormat: "{Series}",
        MovieFileFormat: "{Movie}",
        EpisodeFileFormat: "{Episode}",
        HostBindAddress: "127.0.0.1",
        HostPort: 7979,
        UrlBase: string.Empty,
        RequireAuthentication: false,
        UiTheme: "dark",
        DefaultMovieView: "poster",
        DefaultShowView: "poster",
        MetadataCertificationCountry: "US",
        MetadataLanguage: "en",
        MetadataProviderMode: "auto",
        MetadataBrokerUrl: string.Empty,
        MetadataBrokerConfigured: false,
        MetadataTmdbApiKeyConfigured: false,
        MetadataOmdbApiKeyConfigured: false,
        ReleaseNeverGrabPatterns: string.Empty,
        SearchScoringMode: "balanced",
        ImportRecoveryRetentionDays: 30,
        UpdatedUtc: DateTimeOffset.UnixEpoch,
        HistoryRetentionDays: historyRetentionDays);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
