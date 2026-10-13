using Deluno.Contracts;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1227: with no hand-off in flight, nothing but the processor health check hears a processor stop. #1261 moved it
/// into its own loop (ProcessorHealthWatcherTests drive it on a fake clock); these keep the cadences honest against
/// the one-minute target, worst case included: a full healthy cadence, a tick, and the longest a hanging processor
/// is given.
/// </summary>
public sealed class AQuietProcessorOutageIsNoticedWithinAMinuteTests
{
    [Fact]
    public void The_processor_health_task_is_declared_at_the_healthy_cadence()
        => Assert.Equal(ProcessorHealthWatcher.HealthyCadence, SystemTasks.IntervalFor(SystemTasks.ProcessorHealth));

    [Fact]
    public void An_outage_is_seen_and_a_return_is_seen_within_a_minute_even_in_the_worst_case()
    {
        var worstOutage = ProcessorHealthWatcher.HealthyCadence + ProcessorHealthWatcher.TickInterval + ProcessorHealthWatcher.ProbeTimeout;
        var worstReturn = ProcessorHealthWatcher.DownCadence + ProcessorHealthWatcher.TickInterval + ProcessorHealthWatcher.ProbeTimeout;
        Assert.True(worstOutage < TimeSpan.FromSeconds(50), $"outage worst case {worstOutage}");
        Assert.True(worstReturn < TimeSpan.FromSeconds(35), $"return worst case {worstReturn}");
    }
}
