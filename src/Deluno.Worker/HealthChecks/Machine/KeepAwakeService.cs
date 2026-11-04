using System.Runtime.InteropServices;
using Deluno.Platform.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.HealthChecks.Machine;

/// <summary>
/// The Keep awake button (#1098): while the setting is on, Deluno tells Windows every half minute that the
/// computer is in use, so it does not go to sleep on its own. Windows' own power settings are never changed,
/// and the moment the setting is off, or Deluno stops, Windows goes back to its own timers. Does nothing off
/// Windows. The setting starts off and is switched on only by a person pressing the button.
/// </summary>
public sealed class KeepAwakeService(
    IPlatformSettingsRepository settings,
    ILogger<KeepAwakeService> logger,
    TimeProvider? time = null,
    Func<bool>? isWindows = null,
    Func<uint, uint>? setExecutionState = null) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private const uint EsSystemRequired = 0x00000001;

    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;
    private readonly Func<uint, uint> _setExecutionState = setExecutionState ?? SetThreadExecutionState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_isWindows())
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time ?? TimeProvider.System);
        while (true)
        {
            try
            {
                if ((await settings.GetAsync(stoppingToken)).KeepComputerAwake)
                {
                    _setExecutionState(EsSystemRequired);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Could not ask Windows to stay awake.");
            }

            // Only a stop ends the wait (it throws); the timer is never disposed while the loop runs.
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
