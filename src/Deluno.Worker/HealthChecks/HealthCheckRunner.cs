using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.HealthChecks;

/// <summary>
/// Runs every registered <see cref="IHealthCheck"/>, or only the categories a
/// setting just changed, and writes the results to the one store every
/// health surface reads (#909).
///
/// <para>A check starting or clearing is the only thing worth telling anyone
/// about -- an unchanged result every fifteen minutes would be a notification
/// a person learns to ignore, which is worse than no notification. Started
/// and cleared are each said once, on the same activity feed the bell already
/// reads (#907) and through the same outbound notification path everything
/// else uses, so nothing about health gets a channel of its own to
/// maintain.</para>
/// </summary>
public sealed class HealthCheckRunner(
    IEnumerable<IHealthCheck> checks,
    IHealthCheckRepository repository,
    IActivityFeedRepository activityFeedRepository,
    IOutboundNotificationService notificationService,
    IRealtimeEventPublisher realtimeEventPublisher,
    ILogger<HealthCheckRunner> logger)
{
    /// <summary>
    /// Categories that already announce their own transitions somewhere more
    /// immediate than this fifteen-minute pass, so this runner is not
    /// allowed to say the same thing again. Library pause/resume is
    /// announced the moment <see cref="Deluno.Filesystem.LibraryAvailabilityService"/>
    /// detects it, which is the mechanism search planning and import
    /// automation already gate on; a second, slower announcement from here
    /// would tell a person their library paused twice, in two different
    /// sentences, seconds apart.
    /// </summary>
    private static readonly HashSet<string> SelfAnnouncingCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        HealthCheckCategories.Library
    };

    public Task RunAllAsync(CancellationToken cancellationToken) => RunAsync(checks, cancellationToken);

    public Task RunCategoriesAsync(IReadOnlyCollection<string> categories, CancellationToken cancellationToken)
    {
        if (categories.Count == 0)
        {
            return Task.CompletedTask;
        }

        var wanted = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        return RunAsync(checks.Where(check => wanted.Contains(check.Category)), cancellationToken);
    }

    private async Task RunAsync(IEnumerable<IHealthCheck> toRun, CancellationToken cancellationToken)
    {
        var checksThisPass = toRun.ToArray();
        var outcomes = new List<HealthCheckOutcome>();
        foreach (var check in checksThisPass)
        {
            try
            {
                outcomes.AddRange(await check.RunAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One check failing to run is itself worth knowing, but it must
                // never stop the rest of the pass -- a bug in the processor
                // check must not hide a real problem with a download client.
                logger.LogError(exception, "Health check category {Category} failed to run.", check.Category);
                outcomes.Add(HealthCheckOutcome.Unknown(
                    $"check-error:{check.Category}",
                    check.Category,
                    $"{check.Category} check",
                    $"Deluno could not run this check: {exception.Message}"));
            }
        }

        // A check that reports one row per item it currently finds -- a held
        // processor hand-off, an unexpected file in an output folder -- stops
        // mentioning an id the moment that item is gone. Nothing else ever told
        // the repository the row was gone too, so it says a lie the check
        // itself no longer believes: resolved and dismissed, but still on
        // Health for ever (#914, #921, #930). Pruned here, once, for every
        // category this pass actually ran -- never for a category nothing here
        // was asked about.
        var pruned = await PruneStaleRowsAsync(checksThisPass, outcomes, cancellationToken);

        if (outcomes.Count == 0)
        {
            await PublishPrunedAsync(pruned, cancellationToken);
            return;
        }

        var transitions = await repository.UpsertResultsAsync(outcomes, cancellationToken);
        var changed = transitions
            .Where(item => item.Transition != HealthCheckTransition.Unchanged)
            .Where(item => !SelfAnnouncingCategories.Contains(item.Result.Category))
            .ToArray();
        if (changed.Length == 0)
        {
            // No problem started or cleared, but a row may still have moved (an unknown one turning fine): say so (#1229).
            var moved = transitions.Count(item => item.ScreenChanged);
            if (moved > 0 && pruned == 0)
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    "system.health",
                    "healthy",
                    $"{moved} check(s) changed.",
                    cancellationToken);
                return;
            }

            await PublishPrunedAsync(pruned, cancellationToken);
            return;
        }

        foreach (var transition in changed)
        {
            await AnnounceAsync(transition, cancellationToken);
        }

        // One nudge for the whole batch, not one per row -- the Dashboard tile
        // and the bell only need to know to re-fetch, not what changed.
        await realtimeEventPublisher.PublishHealthChangedAsync(
            "system.health",
            changed.Any(item => item.Transition == HealthCheckTransition.Started) ? "degraded" : "healthy",
            $"{changed.Count(item => item.Transition == HealthCheckTransition.Started)} problem(s) started, {changed.Count(item => item.Transition == HealthCheckTransition.Cleared) + pruned} cleared.",
            cancellationToken);
    }

    /// <summary>
    /// A row the check stopped reporting is removed without a transition, and the open System page only follows a push (#1229):
    /// say so, so it drops the row within a second or two and not at the next unrelated change.
    /// </summary>
    private Task PublishPrunedAsync(int pruned, CancellationToken cancellationToken) =>
        pruned == 0
            ? Task.CompletedTask
            : realtimeEventPublisher.PublishHealthChangedAsync(
                "system.health",
                "healthy",
                $"0 problem(s) started, {pruned} cleared.",
                cancellationToken);

    private async Task<int> PruneStaleRowsAsync(
        IReadOnlyCollection<IHealthCheck> checksThisPass,
        IReadOnlyList<HealthCheckOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var categoriesThisPass = checksThisPass.Select(check => check.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (categoriesThisPass.Count == 0)
        {
            return 0;
        }

        var pruned = 0;
        var reportedIds = outcomes.Select(outcome => outcome.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await repository.ListAsync(cancellationToken) ?? [];
        foreach (var row in existing)
        {
            if (categoriesThisPass.Contains(row.Category) && !reportedIds.Contains(row.Id))
            {
                await repository.RemoveAsync(row.Id, cancellationToken);
                pruned++;
            }
        }

        return pruned;
    }

    private async Task AnnounceAsync(HealthCheckTransitionResult transition, CancellationToken cancellationToken)
    {
        var result = transition.Result;
        var started = transition.Transition == HealthCheckTransition.Started;
        var title = started ? result.Name : $"{result.Name} is fine again";
        var message = started
            ? BuildProblemMessage(result)
            : $"{result.Name}: {result.Message}";

        await activityFeedRepository.RecordActivityAsync(
            started ? "health.problem" : "health.resolved",
            message,
            result.DetailsJson,
            null,
            "health-check",
            result.Id,
            cancellationToken);

        // Advisory-tier checks travel with an ".advisory" suffix so a connection
        // that has not asked for health warnings skips them (#1016). The
        // "health" filter is a prefix, so every connection still matches.
        var outboundCategory = (started ? "health.problem" : "health.resolved")
            + (HealthCheckCategories.IsWarningTier(result.Category) ? HealthCheckCategories.OutboundWarningSuffix : string.Empty);

        await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
            outboundCategory,
            title,
            message,
            result.DetailsJson,
            cancellationToken), "notice");
    }

    private static string BuildProblemMessage(HealthCheckResultItem result)
    {
        var parts = new List<string> { result.Message };
        if (!string.IsNullOrWhiteSpace(result.Cause))
        {
            parts.Add(result.Cause);
        }

        if (!string.IsNullOrWhiteSpace(result.Fix))
        {
            parts.Add(result.Fix);
        }

        return string.Join(" ", parts);
    }
}
