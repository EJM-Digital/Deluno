using Deluno.Api.Updates;

namespace Deluno.Tray;

public sealed class TrayUpdateBackgroundService(
    IUpdateOrchestrator updates,
    ILogger<TrayUpdateBackgroundService> logger) : BackgroundService
{
    /// <summary>The time between checks: six hours.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// A test hook, read from the environment so a test machine can see the repeat without waiting six hours: a whole number of
    /// seconds from 5 to 21600 in DELUNO_TEST_UPDATE_CHECK_INTERVAL_SECONDS. Anything else (unset, not a number, out of range)
    /// is the six hours; a person's install never sets it.
    /// </summary>
    internal static TimeSpan IntervalFrom(string? seconds)
        => int.TryParse(seconds, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 5 and <= 21600
            ? TimeSpan.FromSeconds(value)
            : DefaultInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = IntervalFrom(Environment.GetEnvironmentVariable("DELUNO_TEST_UPDATE_CHECK_INTERVAL_SECONDS"));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = AppSettings.Load();
                if (settings.AutoCheckUpdates)
                {
                    await updates.CheckForUpdatesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Background update check failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
