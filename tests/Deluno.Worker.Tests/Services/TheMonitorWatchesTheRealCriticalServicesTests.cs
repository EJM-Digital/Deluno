using Deluno.Worker.HealthChecks;
using Deluno.Worker.Services;
using Microsoft.Extensions.Hosting;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The failure monitor finds its critical services by type name. If a real class is renamed the monitor would quietly
/// stop protecting it, so the names it holds are checked against the real classes, not stand-ins named alike.
/// </summary>
public sealed class TheMonitorWatchesTheRealCriticalServicesTests
{
    [Fact]
    public void The_default_critical_names_are_the_real_job_worker_and_health_check_service()
    {
        var critical = new BackgroundServiceSafetyOptions().CriticalServiceTypeNames;

        Assert.Equal(2, critical.Count);
        Assert.Contains(typeof(DelunoHeartbeatWorker).Name, critical);
        Assert.Contains(typeof(HealthCheckHostedService).Name, critical);
    }

    [Theory]
    [InlineData(typeof(DelunoHeartbeatWorker))]
    [InlineData(typeof(HealthCheckHostedService))]
    public void Each_real_critical_service_is_a_background_service_the_monitor_can_watch_and_restart(Type type)
    {
        Assert.True(typeof(BackgroundService).IsAssignableFrom(type));
    }
}
