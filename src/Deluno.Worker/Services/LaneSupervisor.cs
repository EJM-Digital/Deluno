using Deluno.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Keeps one job-worker lane alive and honest (#1080).
///
/// <para>A lane task that ends while Deluno is running (an exception out of
/// the lane loop, or a quiet return) used to leave that lane's job types
/// unleased while everything looked fine, because the other lanes kept the
/// worker's <c>Task.WhenAll</c> pending. Here the end is reported to Health
/// and the lane is started again, pausing longer each time. A lane whose
/// passes keep failing (a database that stays busy) is reported to Health
/// after <see cref="FailureStreakThreshold"/> failures in a row, with the last
/// error, and the row goes away once a pass succeeds.</para>
/// </summary>
public sealed class LaneSupervisor(
    ILogger logger,
    IBackgroundProblemReporter? reporter,
    TimeSpan? restartBackoff = null,
    TimeSpan? maxRestartBackoff = null)
{
    /// <summary>Failed passes in a row before the lane is shown on Health.</summary>
    public const int FailureStreakThreshold = 3;

    private readonly TimeSpan _restartBackoff = restartBackoff ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _maxRestartBackoff = maxRestartBackoff ?? TimeSpan.FromMinutes(1);

    public static string ProblemKey(string laneName) => $"lane:{laneName}";

    /// <summary>Called by the lane with its consecutive failed-pass count (0 when a pass works).</summary>
    public void OnFailureStreak(string laneName, int consecutiveFailures, Exception? lastError)
    {
        if (reporter is null)
        {
            return;
        }

        if (consecutiveFailures >= FailureStreakThreshold && lastError is not null)
        {
            reporter.Report(
                ProblemKey(laneName),
                $"Background jobs: {laneName} lane keeps failing",
                $"Deluno's {laneName} background jobs have failed {consecutiveFailures} times in a row and are waiting to try again. Work on this lane is delayed until it succeeds.",
                lastError.Message);
        }
        else if (consecutiveFailures == 0)
        {
            reporter.Clear(ProblemKey(laneName));
        }
    }

    /// <summary>Runs <paramref name="runLane"/>, starting it again if it ends before <paramref name="stoppingToken"/> does.</summary>
    public async Task SuperviseAsync(string laneName, Func<CancellationToken, Task> runLane, CancellationToken stoppingToken)
    {
        var restarts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            Exception? failure = null;
            try
            {
                await runLane(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            failure ??= new InvalidOperationException($"The {laneName} lane ended without being asked to stop.");
            restarts++;
            var pause = TimeSpan.FromTicks(Math.Min(
                _maxRestartBackoff.Ticks,
                _restartBackoff.Ticks * (1L << Math.Min(restarts - 1, 10))));
            logger.LogError(failure, "Job lane {LaneName} stopped unexpectedly; restarting it in {Pause}.", laneName, pause);
            reporter?.Report(
                ProblemKey(laneName),
                $"Background jobs: {laneName} lane stopped",
                $"Deluno's {laneName} background jobs stopped after an error and are being restarted (restart {restarts}). Work on this lane is delayed until it is back.",
                failure.Message);

            try
            {
                await Task.Delay(pause, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
