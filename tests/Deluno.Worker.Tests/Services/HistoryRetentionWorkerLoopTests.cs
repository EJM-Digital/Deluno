using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#828: the daily prune says what it removed from either store, warns once when a pass fails, and stops with Deluno.</summary>
public sealed class HistoryRetentionWorkerLoopTests
{
    private static async Task<(RecordingLogger<HistoryRetentionWorker> Log, Mock<IPlatformSettingsRepository> Settings, HistoryRetentionWorker Worker, FakeTimeProvider Clock, ServiceProvider Provider)> BuildAsync(int activityRows, int healthRows)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Deluno.Persistence.Tests.Support.FakePlatformSettingsRepository.Default() with { HistoryRetentionDays = 30 });
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repository => repository.PruneActivityAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(activityRows);
        var health = new Mock<IDownloadHealthRepository>();
        health.Setup(repository => repository.PruneDownloadHealthHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(healthRows);
        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(activity.Object);
        services.AddSingleton(health.Object);
        var provider = services.BuildServiceProvider();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
        var log = new RecordingLogger<HistoryRetentionWorker>();
        var worker = new HistoryRetentionWorker(log, provider.GetRequiredService<IServiceScopeFactory>(), clock);
        await Task.CompletedTask;
        return (log, settings, worker, clock, provider);
    }

    [Theory]
    [InlineData(0, 3, "History retention pruned 0 Activity row(s) and 3 download-health history row(s) past 30 day(s).")]
    [InlineData(2, 0, "History retention pruned 2 Activity row(s) and 0 download-health history row(s) past 30 day(s).")]
    public async Task Rows_removed_from_only_one_store_are_still_reported(int activityRows, int healthRows, string expected)
    {
        var (log, settings, worker, clock, provider) = await BuildAsync(activityRows, healthRows);
        await using var lifetime = provider;

        await worker.StartAsync(CancellationToken.None);
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMinutes(1)); return log.Entries.Count > 0; }, "the pass to report");
        await worker.StopAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(expected, entry.Message);
    }

    [Fact]
    public async Task A_pass_that_fails_is_warned_about_with_its_error_in_plain_words()
    {
        var (log, settings, worker, clock, provider) = await BuildAsync(0, 0);
        await using var lifetime = provider;
        var broken = new IOException("The database is busy.");
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(broken);

        await worker.StartAsync(CancellationToken.None);
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMinutes(1)); return log.Entries.Count > 0; }, "the failed pass to be reported");
        await worker.StopAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("History retention pruning failed; it will try again on the next pass.", entry.Message);
        Assert.Same(broken, entry.Exception);
    }

    [Fact]
    public async Task Stopping_Deluno_during_the_wait_ends_the_loop_without_pruning()
    {
        var (log, settings, worker, clock, provider) = await BuildAsync(1, 1);
        await using var lifetime = provider;

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(worker.ExecuteTask is { IsCompleted: true });
        Assert.Empty(settings.Invocations);
        Assert.Empty(log.Entries);
    }
}
