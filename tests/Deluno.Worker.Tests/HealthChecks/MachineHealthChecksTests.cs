using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.HealthChecks.Machine;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks;

/// <summary>#1098: the Machine area of Health: sleep, drive health, a pending restart and processor heat.</summary>
public sealed class MachineHealthChecksTests
{
    private static IPlatformSettingsRepository Settings(bool keepAwake)
    {
        var repository = new Mock<IPlatformSettingsRepository>();
        repository.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformSettingsSnapshot)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)) with { KeepComputerAwake = keepAwake });
        return repository.Object;
    }

    private static IMachinePowerReader Power(MachinePowerReading? reading)
    {
        var reader = new Mock<IMachinePowerReader>();
        reader.Setup(item => item.Read()).Returns(reading);
        return reader.Object;
    }

    [Fact]
    public async Task A_computer_that_sleeps_on_mains_power_is_a_machine_problem_in_plain_words_with_the_keep_awake_fix()
    {
        var outcomes = await new ComputerSleepHealthCheck(Power(new MachinePowerReading(1800, 0)), Settings(false)).RunAsync(CancellationToken.None);

        var problem = Assert.Single(outcomes);
        Assert.Equal(HealthCheckCategories.Machine, problem.Category);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.Equal("machine:sleep", problem.Id);
        Assert.Equal("This computer goes to sleep after 30 minutes on mains power.", problem.Message);
        Assert.Contains("Keep awake", problem.Fix);
    }

    [Fact]
    public async Task Hibernating_is_named_and_both_are_named_together()
    {
        var hibernate = Assert.Single(await new ComputerSleepHealthCheck(Power(new MachinePowerReading(0, 3600)), Settings(false)).RunAsync(CancellationToken.None));
        var both = Assert.Single(await new ComputerSleepHealthCheck(Power(new MachinePowerReading(600, 7200)), Settings(false)).RunAsync(CancellationToken.None));

        Assert.Equal("This computer hibernates after 1 hour on mains power.", hibernate.Message);
        Assert.Equal("This computer goes to sleep after 10 minutes and hibernates after 2 hours on mains power.", both.Message);
    }

    [Fact]
    public async Task A_computer_that_never_sleeps_or_that_Deluno_keeps_awake_is_fine()
    {
        var never = Assert.Single(await new ComputerSleepHealthCheck(Power(new MachinePowerReading(0, 0)), Settings(false)).RunAsync(CancellationToken.None));
        var kept = Assert.Single(await new ComputerSleepHealthCheck(Power(new MachinePowerReading(1800, 0)), Settings(true)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, never.Status);
        Assert.Equal(HealthCheckStatuses.Ok, kept.Status);
        Assert.Contains("keeping it awake", kept.Message);
    }

    [Fact]
    public async Task A_computer_that_cannot_say_how_it_sleeps_reports_nothing_at_all()
    {
        Assert.Empty(await new ComputerSleepHealthCheck(Power(null), Settings(false)).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Every_drive_is_reported_and_only_one_that_is_not_healthy_is_a_problem()
    {
        var reader = new Mock<IMachineDriveHealthReader>();
        reader.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PhysicalDriveHealth("Samsung SSD", "Healthy", "OK", "SSD"),
            new PhysicalDriveHealth("WD Red", "Warning", "OK", "HDD"),
            new PhysicalDriveHealth("Old Disk", "Unhealthy", "Predictive Failure", "HDD")
        ]);

        var outcomes = await new DriveHealthHealthCheck(reader.Object).RunAsync(CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, item => Assert.Equal(HealthCheckCategories.Machine, item.Category));
        Assert.Equal(HealthCheckStatuses.Ok, outcomes.Single(item => item.Id == "machine:drive:Samsung SSD").Status);
        Assert.Equal("WD Red is reporting a health warning.", outcomes.Single(item => item.Id == "machine:drive:WD Red").Message);
        var failing = outcomes.Single(item => item.Id == "machine:drive:Old Disk");
        Assert.Equal("Old Disk says it is failing.", failing.Message);
        Assert.Contains("Back up now", failing.Fix);
    }

    [Fact]
    public async Task A_computer_that_cannot_read_drive_health_reports_nothing()
    {
        var reader = new Mock<IMachineDriveHealthReader>();
        reader.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PhysicalDriveHealth>?)null);

        Assert.Empty(await new DriveHealthHealthCheck(reader.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public void Drive_health_reads_one_drive_or_many_and_the_numbers_Windows_prints_for_a_name_it_cannot_say()
    {
        var one = WindowsMachineDriveHealthReader.Parse("""{"FriendlyName":"Samsung SSD","HealthStatus":"Healthy","OperationalStatus":"OK","MediaType":"SSD"}""");
        var many = WindowsMachineDriveHealthReader.Parse("""[{"FriendlyName":"A","HealthStatus":1,"OperationalStatus":"OK","MediaType":4},{"FriendlyName":"B","HealthStatus":0}]""");

        Assert.Equal("Samsung SSD", Assert.Single(one!).Name);
        Assert.Equal(["Warning", "Healthy"], many!.Select(item => item.Health).ToArray());
        Assert.Null(WindowsMachineDriveHealthReader.Parse(""));
        Assert.Null(WindowsMachineDriveHealthReader.Parse("not json"));
    }

    [Theory]
    [InlineData(true, HealthCheckStatuses.Problem)]
    [InlineData(false, HealthCheckStatuses.Ok)]
    public async Task A_pending_restart_is_a_problem_and_no_pending_restart_is_fine(bool pending, string status)
    {
        var reboot = new Mock<IMachineRebootReader>();
        reboot.Setup(item => item.RebootPending()).Returns(pending);

        var outcome = Assert.Single(await new PendingRebootHealthCheck(reboot.Object).RunAsync(CancellationToken.None));

        Assert.Equal(status, outcome.Status);
        Assert.Equal("machine:reboot", outcome.Id);
    }

    [Fact]
    public async Task A_computer_that_cannot_say_whether_it_needs_a_restart_reports_nothing()
    {
        var reboot = new Mock<IMachineRebootReader>();
        reboot.Setup(item => item.RebootPending()).Returns((bool?)null);

        Assert.Empty(await new PendingRebootHealthCheck(reboot.Object).RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(72.0, HealthCheckStatuses.Ok)]
    [InlineData(89.9, HealthCheckStatuses.Ok)]
    [InlineData(90.0, HealthCheckStatuses.Problem)]
    [InlineData(97.4, HealthCheckStatuses.Problem)]
    public async Task Processor_heat_is_a_problem_only_when_it_is_high(double celsius, string status)
    {
        var temperature = new Mock<IMachineTemperatureReader>();
        temperature.Setup(item => item.CpuCelsiusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(celsius);

        var outcome = Assert.Single(await new CpuTemperatureHealthCheck(temperature.Object).RunAsync(CancellationToken.None));

        Assert.Equal(status, outcome.Status);
        Assert.Contains($"{Math.Round(celsius)}°C", outcome.Message);
    }

    [Fact]
    public async Task A_computer_that_does_not_report_a_temperature_shows_none()
    {
        var temperature = new Mock<IMachineTemperatureReader>();
        temperature.Setup(item => item.CpuCelsiusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);

        Assert.Empty(await new CpuTemperatureHealthCheck(temperature.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public void A_Linux_thermal_zone_is_read_for_the_processor_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-thermal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "thermal_zone0"));
            File.WriteAllText(Path.Combine(root, "thermal_zone0", "type"), "x86_pkg_temp");
            File.WriteAllText(Path.Combine(root, "thermal_zone0", "temp"), "61000");
            Directory.CreateDirectory(Path.Combine(root, "thermal_zone1"));
            File.WriteAllText(Path.Combine(root, "thermal_zone1", "type"), "iwlwifi");
            File.WriteAllText(Path.Combine(root, "thermal_zone1", "temp"), "99000");

            Assert.Equal(61d, MachineTemperatureReader.ReadLinux(root));
            Assert.Null(MachineTemperatureReader.ReadLinux(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>The real Windows readers run on the computer the tests run on and never throw; where they cannot say, they say nothing.</summary>
public sealed class MachineReadersOnThisComputerTests
{
    [Fact]
    public async Task The_real_readers_answer_or_say_nothing_and_never_throw()
    {
        var power = new WindowsMachinePowerReader().Read();
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(power);
            Console.WriteLine($"sleep AC {power!.SleepAfterSecondsOnAc}, hibernate AC {power.HibernateAfterSecondsOnAc}");
        }
        else
        {
            Assert.Null(power);
        }

        _ = new MachineRebootReader().RebootPending();
        _ = await new WindowsMachineDriveHealthReader().ReadAsync(CancellationToken.None);
        _ = await new MachineTemperatureReader().CpuCelsiusAsync(CancellationToken.None);
    }
}
