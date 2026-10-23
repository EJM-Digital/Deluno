using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Api.Updates;

/// <summary>
/// Looks for a newly applied update as Deluno starts, so the history and the Activity line exist whether or not anyone
/// opens the Updates card (#1376). It waits for the databases, because the Activity line is written to one of them.
/// </summary>
public sealed class AppliedUpdateStartupCheck(
    AppliedUpdateRecorder recorder,
    IDelunoStartupGate startupGate,
    ILogger<AppliedUpdateStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startupGate.WaitUntilReadyAsync(logger, "Recording an applied update", stoppingToken))
            {
                return;
            }

            await recorder.DetectAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Recording an update is never a reason for Deluno not to run; the Updates card tries again.
            logger.LogWarning(ex, "Could not record an applied update at start.");
        }
    }
}
