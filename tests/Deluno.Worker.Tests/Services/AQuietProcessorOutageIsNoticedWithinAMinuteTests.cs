using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1227: with no hand-off in flight, nothing but the processor health pass hears a processor stop. It ran every five
/// minutes, so the Dashboard, System and the tray stayed green for two to four. It runs every minute, and a pass that
/// finds the processor silent stores unreachable at once.
/// </summary>
public sealed class AQuietProcessorOutageIsNoticedWithinAMinuteTests
{
    [Fact]
    public void The_processor_health_pass_runs_about_every_minute()
        => Assert.True(SystemTasks.IntervalFor(SystemTasks.ProcessorHealth) <= TimeSpan.FromMinutes(1));

    [Fact]
    public async Task A_pass_with_no_hand_off_anywhere_stores_a_silent_processor_as_unreachable_on_the_minute_cadence()
    {
        TimeSpan? claimedEvery = null;
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(SystemTasks.ProcessorHealth, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan, CancellationToken>((_, interval, _) => claimedEvery = interval)
            .ReturnsAsync(true);
        var weir = new ProcessorConnectionItem("p1", "Weir", "generic", "http://processor.invalid/intake", "Authorization", null, true, "healthy", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repository => repository.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([weir]);
        var connections = new Mock<IProcessorConnectionService>();
        connections.Setup(service => service.GetStatusSupportAsync(weir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, "Weir did not answer."));

        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, jobs.Object, new ConfigurationBuilder().Build(), TimeProvider.System);
        await planner.RunProcessorHealthPassAsync(processors.Object, connections.Object, CancellationToken.None);

        processors.Verify(repository => repository.RecordProcessorConnectionHealthAsync("p1", "unreachable", "Weir did not answer.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(claimedEvery <= TimeSpan.FromMinutes(1));
    }
}
