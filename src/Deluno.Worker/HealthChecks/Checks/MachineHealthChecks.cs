using Deluno.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Machine;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// The Machine area of Health (#1098): things about the computer Deluno runs on that quietly stop it working.
/// Each one is plain words and reports nothing at all (not "unknown") when the computer cannot say, so a
/// machine that does not report a temperature never shows a made-up one.
/// </summary>
public static class MachineHealthIds
{
    public const string Sleep = "machine:sleep";
    public const string DrivePrefix = "machine:drive:";
    public const string Reboot = "machine:reboot";
    public const string Temperature = "machine:temperature";
}

/// <summary>
/// The computer goes to sleep or hibernates on mains power, which pauses every search, download and import
/// until somebody wakes it. Windows only: a Linux server or Docker host is not asked. When Deluno is already
/// keeping the computer awake (the Keep awake button) the check is fine and says so.
/// </summary>
public sealed class ComputerSleepHealthCheck(IMachinePowerReader power, IPlatformSettingsRepository settings) : IHealthCheck
{
    private const string Name = "Computer sleep";

    public string Category => HealthCheckCategories.Machine;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var reading = power.Read();
        if (reading is null)
        {
            return [];
        }

        var sleep = reading.SleepAfterSecondsOnAc is > 0 ? reading.SleepAfterSecondsOnAc : null;
        var hibernate = reading.HibernateAfterSecondsOnAc is > 0 ? reading.HibernateAfterSecondsOnAc : null;
        if (sleep is null && hibernate is null)
        {
            return [HealthCheckOutcome.Ok(MachineHealthIds.Sleep, Category, Name, "This computer never goes to sleep on mains power.")];
        }

        var words = sleep is { } s && hibernate is { } h
            ? $"goes to sleep after {Minutes(s)} and hibernates after {Minutes(h)}"
            : sleep is { } onlySleep ? $"goes to sleep after {Minutes(onlySleep)}" : $"hibernates after {Minutes(hibernate!.Value)}";

        if ((await settings.GetAsync(cancellationToken)).KeepComputerAwake)
        {
            return [HealthCheckOutcome.Ok(MachineHealthIds.Sleep, Category, Name, $"This computer {words} on mains power, and Deluno is keeping it awake while it runs.")];
        }

        return
        [
            HealthCheckOutcome.Problem(
                MachineHealthIds.Sleep,
                Category,
                Name,
                $"This computer {words} on mains power.",
                cause: "While it sleeps nothing is searched, downloaded or imported, and the page cannot be opened until it wakes.",
                fix: "Press Keep awake and Deluno stops Windows sleeping while it runs, or set Sleep to Never in Windows power settings.")
        ];
    }

    internal static string Minutes(int seconds)
        => seconds < 90 ? $"{seconds} seconds" : seconds % 3600 == 0 ? (seconds == 3600 ? "1 hour" : $"{seconds / 3600} hours") : $"{(int)Math.Round(seconds / 60d)} minutes";
}

/// <summary>A drive reporting that it is not healthy: the warning a person would otherwise find out about when it fails.</summary>
public sealed class DriveHealthHealthCheck(IMachineDriveHealthReader drives) : IHealthCheck
{
    public string Category => HealthCheckCategories.Machine;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var reading = await drives.ReadAsync(cancellationToken);
        if (reading is null || reading.Count == 0)
        {
            return [];
        }

        var outcomes = new List<HealthCheckOutcome>(reading.Count);
        foreach (var drive in reading)
        {
            var id = $"{MachineHealthIds.DrivePrefix}{drive.Name}";
            var name = $"Drive health: {drive.Name}";
            outcomes.Add(drive.IsWarning
                ? HealthCheckOutcome.Problem(
                    id,
                    Category,
                    name,
                    drive.IsFailing
                        ? $"{drive.Name} says it is failing."
                        : $"{drive.Name} is reporting a health warning.",
                    cause: "The drive's own self-check says it may stop working. Anything only stored on it could be lost.",
                    fix: "Back up now, then replace the drive if the warning stays.")
                : HealthCheckOutcome.Ok(id, Category, name, $"{drive.Name} reports it is healthy."));
        }

        return outcomes;
    }
}

/// <summary>The computer is waiting for a restart to finish installing updates.</summary>
public sealed class PendingRebootHealthCheck(IMachineRebootReader reboot) : IHealthCheck
{
    private const string Name = "Computer restart";

    public string Category => HealthCheckCategories.Machine;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthCheckOutcome> result = reboot.RebootPending() switch
        {
            null => [],
            true =>
            [
                HealthCheckOutcome.Problem(
                    MachineHealthIds.Reboot,
                    Category,
                    Name,
                    "This computer is waiting for a restart to finish installing updates.",
                    cause: "Until it restarts the updates are half installed, and the computer may restart by itself later, pausing downloads while it does.",
                    fix: "Restart the computer at a quiet moment, when nothing is downloading.")
            ],
            false => [HealthCheckOutcome.Ok(MachineHealthIds.Reboot, Category, Name, "This computer is not waiting for a restart.")]
        };
        return Task.FromResult(result);
    }
}

/// <summary>The processor running hot. Only when the computer reports a temperature at all.</summary>
public sealed class CpuTemperatureHealthCheck(IMachineTemperatureReader temperature) : IHealthCheck
{
    /// <summary>Hot enough that a computer slows itself down to cool off.</summary>
    public const double HighCelsius = 90d;

    private const string Name = "Processor temperature";

    public string Category => HealthCheckCategories.Machine;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var celsius = await temperature.CpuCelsiusAsync(cancellationToken);
        if (celsius is null)
        {
            return [];
        }

        var rounded = Math.Round(celsius.Value);
        return celsius >= HighCelsius
            ?
            [
                HealthCheckOutcome.Problem(
                    MachineHealthIds.Temperature,
                    Category,
                    Name,
                    $"The processor is at {rounded}°C.",
                    cause: "A processor this hot slows itself down, so everything Deluno does gets slower, and the heat shortens the computer's life.",
                    fix: "Check the fans and clear the dust from the vents; make sure the computer has room to breathe.")
            ]
            : [HealthCheckOutcome.Ok(MachineHealthIds.Temperature, Category, Name, $"The processor is at {rounded}°C.")];
    }
}
