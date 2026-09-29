using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// #1002: when "Pause automatic searching while many downloads are waiting" is
/// on, says so on the Health screen while a pause is holding a media type, or
/// while the queue cannot be read (in which case searching carries on and
/// nothing is paused). Silent when the switch is off. The fix is on the same
/// Search cycles screen that holds the switch.
/// </summary>
public sealed class SearchQueuePauseHealthCheck(ISearchQueuePauseGate gate) : IHealthCheck
{
    public string Category => HealthCheckCategories.System;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();
        foreach (var mediaType in new[] { "movies", "tv" })
        {
            var decision = await gate.EvaluateAsync(mediaType, cancellationToken);
            if (!decision.Enabled)
            {
                return [];
            }

            var id = $"search:queue-pause:{mediaType}";
            var kind = SearchQueuePauseGate.Kind(mediaType);
            var name = $"Automatic {kind} searching";
            if (decision.Unreadable)
            {
                outcomes.Add(HealthCheckOutcome.Problem(
                    id, Category, name,
                    $"Deluno could not read the download queue ({decision.UnreadableReason}), so it cannot tell whether to pause.",
                    cause: "Automatic searching carries on without the pause until the download client answers again.",
                    fix: "Check the download client under Services, or turn the pause off on the Search cycles screen."));
            }
            else if (decision.Paused)
            {
                outcomes.Add(HealthCheckOutcome.Problem(
                    id, Category, name,
                    $"Automatic {kind} searching is paused: {decision.Waiting} downloads are waiting and the pause limit is {decision.Threshold}.",
                    cause: "You asked Deluno to stop searching while the download queue is long. Searches you start yourself still run.",
                    fix: "It restarts by itself when the queue shrinks. To search anyway, raise the limit or turn the pause off on the Search cycles screen."));
            }
            else
            {
                outcomes.Add(HealthCheckOutcome.Ok(id, Category, name,
                    $"Automatic {kind} searching is running: {decision.Waiting} downloads waiting, the pause limit is {decision.Threshold}."));
            }
        }

        return outcomes;
    }
}
