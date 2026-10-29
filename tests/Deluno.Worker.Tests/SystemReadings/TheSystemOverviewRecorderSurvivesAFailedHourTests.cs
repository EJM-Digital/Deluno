using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.SystemReadings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.SystemReadings;

/// <summary>
/// The System screen's drive-full forecast needs a free-space reading kept every day (coverage register, engines): one hour in which
/// the drives cannot be read, or the record cannot be written, is a gap in the history and the recorder reads again an hour later,
/// instead of ending for good and leaving the forecast to stop without a word. Refs #1296.
/// </summary>
public sealed class TheSystemOverviewRecorderSurvivesAFailedHourTests
{
    [Fact]
    public async Task A_failed_hour_is_skipped_and_the_next_hour_records_the_drive_and_prunes_the_old_history()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new Mock<ISystemOverviewStore>();
        var facts = new Mock<ISystemOverviewFacts>();
        var readings = 0;
        facts.Setup(item => item.ReadDrivesAsync(It.IsAny<CancellationToken>())).Returns(() =>
            ++readings == 1
                ? Task.FromException<IReadOnlyList<DriveFacts>>(new IOException("The share did not answer."))
                : Task.FromResult<IReadOnlyList<DriveFacts>>([new DriveFacts("C:", "System", @"C:\", 1000, 400, 10, null)]));
        var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var recorder = new SystemOverviewRecorder(
            services.GetRequiredService<IServiceScopeFactory>(), facts.Object, clock, NullLogger<SystemOverviewRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        // The clock moves in small steps until each wait of the recorder has run out, however soon it began waiting.
        await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromSeconds(10)); return readings == 1; }, "the first hour's reading to be tried and to fail");
        await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromMinutes(10)); return store.Invocations.Any(call => call.Method.Name == nameof(ISystemOverviewStore.PruneAsync)); }, "the second hour to record the drive and prune");
        await recorder.StopAsync(CancellationToken.None);

        Assert.Equal(2, readings);
        store.Verify(item => item.RecordDriveSpaceAsync("C:", It.IsAny<DateOnly>(), 400, 1000, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.PruneAsync(It.Is<DateTimeOffset>(cutoff => cutoff <= clock.GetUtcNow() - TimeSpan.FromDays(14) && cutoff > clock.GetUtcNow() - TimeSpan.FromDays(14) - TimeSpan.FromHours(2)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Each_start_of_Deluno_is_recorded_at_once_and_each_drive_is_recorded_under_the_day_it_was_read()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new Mock<ISystemOverviewStore>();
        var facts = new Mock<ISystemOverviewFacts>();
        facts.Setup(item => item.ReadDrivesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DriveFacts("C:", "System", @"C:\", 1000, 400, 10, null), new DriveFacts("D:", "Media", @"D:\", 5000, 2500, 10, null)]);
        var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var recorder = new SystemOverviewRecorder(
            services.GetRequiredService<IServiceScopeFactory>(), facts.Object, clock, NullLogger<SystemOverviewRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => store.Invocations.Any(call => call.Method.Name == nameof(ISystemOverviewStore.RecordStartAsync)), "the start to be recorded");
        // Before the first drive reading (30 seconds in) the start is already written, with the moment Deluno started.
        store.Verify(item => item.RecordStartAsync(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.RecordDriveSpaceAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

        await Wait.UntilAsync(() => { clock.Advance(TimeSpan.FromSeconds(10)); return store.Invocations.Any(call => call.Method.Name == nameof(ISystemOverviewStore.PruneAsync)); }, "the first drive reading");
        await recorder.StopAsync(CancellationToken.None);

        var today = DateOnly.FromDateTime(clock.GetLocalNow().Date);
        store.Verify(item => item.RecordDriveSpaceAsync("C:", It.Is<DateOnly>(day => day == today || day == today.AddDays(-1)), 400, 1000, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.RecordDriveSpaceAsync("D:", It.Is<DateOnly>(day => day == today || day == today.AddDays(-1)), 2500, 5000, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.RecordStartAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
