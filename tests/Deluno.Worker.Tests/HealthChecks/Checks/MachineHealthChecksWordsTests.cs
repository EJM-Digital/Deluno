using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.HealthChecks.Machine;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#1098: the words each computer health row says, in every state: sleep, drive health, restart and heat.</summary>
public sealed class MachineHealthChecksWordsTests
{
    private static ComputerSleepHealthCheck Sleep(int? sleep, int? hibernate, bool keepAwake = false)
    {
        var power = new Mock<IMachinePowerReader>();
        power.Setup(p => p.Read()).Returns(new MachinePowerReading(sleep, hibernate));
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(FakePlatformSettingsRepository.Default() with { KeepComputerAwake = keepAwake });
        return new ComputerSleepHealthCheck(power.Object, settings.Object);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(-5, null)]
    public async Task A_computer_that_never_sleeps_says_so(int? sleep, int? hibernate)
    {
        var outcome = Assert.Single(await Sleep(sleep, hibernate).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("Computer sleep", outcome.Name);
        Assert.Equal("This computer never goes to sleep on mains power.", outcome.Message);
        Assert.Equal(HealthCheckCategories.Machine, Sleep(null, null).Category);
    }

    [Theory]
    [InlineData(600, null, "goes to sleep after 10 minutes")]
    [InlineData(null, 7200, "hibernates after 2 hours")]
    [InlineData(1800, 7200, "goes to sleep after 30 minutes and hibernates after 2 hours")]
    [InlineData(0, 3600, "hibernates after 1 hour")]
    [InlineData(300, 0, "goes to sleep after 5 minutes")]
    public async Task A_computer_that_sleeps_says_how_soon_and_what_to_do(int? sleep, int? hibernate, string words)
    {
        var outcome = Assert.Single(await Sleep(sleep, hibernate).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal($"This computer {words} on mains power.", outcome.Message);
        Assert.Equal("While it sleeps nothing is searched, downloaded or imported, and the page cannot be opened until it wakes.", outcome.Cause);
        Assert.Equal("Press Keep awake and Deluno stops Windows sleeping while it runs, or set Sleep to Never in Windows power settings.", outcome.Fix);
    }

    [Fact]
    public async Task A_computer_that_sleeps_but_is_kept_awake_says_so()
    {
        var outcome = Assert.Single(await Sleep(600, null, keepAwake: true).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("This computer goes to sleep after 10 minutes on mains power, and Deluno is keeping it awake while it runs.", outcome.Message);
    }

    [Fact]
    public async Task Each_drive_has_a_row_named_for_it_in_its_own_words()
    {
        var reader = new Mock<IMachineDriveHealthReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new PhysicalDriveHealth("Disk A", "Healthy", null, null),
            new PhysicalDriveHealth("Disk B", "Warning", null, null),
            new PhysicalDriveHealth("Disk C", "Unhealthy", null, null)]);
        var check = new DriveHealthHealthCheck(reader.Object);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Equal(["Drive health: Disk A", "Drive health: Disk B", "Drive health: Disk C"], outcomes.Select(o => o.Name));
        Assert.Equal(MachineHealthIds.DrivePrefix + "Disk A", outcomes[0].Id);
        Assert.Equal("Disk A reports it is healthy.", outcomes[0].Message);
        Assert.Equal("Disk B is reporting a health warning.", outcomes[1].Message);
        Assert.Equal("Disk C says it is failing.", outcomes[2].Message);
        Assert.Equal("The drive's own self-check says it may stop working. Anything only stored on it could be lost.", outcomes[2].Cause);
        Assert.Equal("Back up now, then replace the drive if the warning stays.", outcomes[1].Fix);
        Assert.Equal(HealthCheckCategories.Machine, check.Category);
    }

    [Fact]
    public async Task A_pending_restart_says_why_it_matters_and_when_to_do_it()
    {
        var reader = new Mock<IMachineRebootReader>();
        reader.SetupSequence(r => r.RebootPending()).Returns(true).Returns(false);
        var check = new PendingRebootHealthCheck(reader.Object);

        var pending = Assert.Single(await check.RunAsync(CancellationToken.None));
        var fine = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("Computer restart", pending.Name);
        Assert.Equal("This computer is waiting for a restart to finish installing updates.", pending.Message);
        Assert.Equal("Until it restarts the updates are half installed, and the computer may restart by itself later, pausing downloads while it does.", pending.Cause);
        Assert.Equal("Restart the computer at a quiet moment, when nothing is downloading.", pending.Fix);
        Assert.Equal("This computer is not waiting for a restart.", fine.Message);
        Assert.Equal(HealthCheckCategories.Machine, check.Category);
    }

    [Theory]
    [InlineData(89.6, "The processor is at 90°C.", false)]
    [InlineData(90.0, "The processor is at 90°C.", true)]
    [InlineData(95.2, "The processor is at 95°C.", true)]
    [InlineData(55.4, "The processor is at 55°C.", false)]
    public async Task The_processor_heat_is_said_in_whole_degrees_and_a_hot_one_says_what_to_do(double celsius, string message, bool problem)
    {
        var reader = new Mock<IMachineTemperatureReader>();
        reader.Setup(r => r.CpuCelsiusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(celsius);
        var check = new CpuTemperatureHealthCheck(reader.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("Processor temperature", outcome.Name);
        Assert.Equal(message, outcome.Message);
        Assert.Equal(problem, outcome.Status == HealthCheckStatuses.Problem);
        Assert.Equal(problem ? "A processor this hot slows itself down, so everything Deluno does gets slower, and the heat shortens the computer's life." : null, outcome.Cause);
        Assert.Equal(problem ? "Check the fans and clear the dust from the vents; make sure the computer has room to breathe." : null, outcome.Fix);
        Assert.Equal(90d, CpuTemperatureHealthCheck.HighCelsius);
        Assert.Equal(HealthCheckCategories.Machine, check.Category);
    }
}
