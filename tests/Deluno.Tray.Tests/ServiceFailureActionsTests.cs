using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1080: an installed Windows service restarts itself after it stops on a failure.</summary>
public sealed class ServiceFailureActionsTests
{
    [Fact]
    public void Failure_actions_restart_the_service_after_a_minute_and_count_a_non_zero_exit_as_a_failure()
    {
        var commands = ServiceManager.FailureActionArguments("Deluno");

        Assert.Equal(
            [
                "failure Deluno reset= 86400 actions= restart/60000/restart/60000/restart/60000",
                "failureflag Deluno 1"
            ],
            commands);
    }
}
