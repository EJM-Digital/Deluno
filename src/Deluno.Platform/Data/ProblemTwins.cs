using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// The twins of a counted problem (#1228): an import issue (a recovery case), the processor hand-off it is about, and the
/// System health row that mirrors the issue. A problem is dismissed once, and every twin goes with it, so no screen is
/// left counting what another screen has already cleared.
/// </summary>
public static class ProblemTwins
{
    /// <summary>The id prefix of the System health row that mirrors an open processor-output import issue.</summary>
    public const string OutputIssueRowPrefix = "processor-output-issue:";

    public static string OutputIssueRowId(string caseId) => OutputIssueRowPrefix + caseId;

    /// <summary>The hand-off an import issue is about, from the details the processor watcher wrote with it.</summary>
    public static string? HandoffIdIn(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "handoffId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// An import issue was dismissed: the hand-off it is about is dismissed with it (when it still needs a person), and
    /// the issue's row on System goes now, not at the next fifteen-minute pass.
    /// </summary>
    public static async Task AfterIssueDismissedAsync(
        string caseId,
        string? detailsJson,
        IProcessorRepository? processors,
        IActivityFeedRepository? activityFeed,
        IHealthCheckRepository? healthChecks,
        IRealtimeEventPublisher? realtime,
        CancellationToken cancellationToken)
    {
        await ClearHealthRowAsync(caseId, healthChecks, realtime, cancellationToken);
        if (processors is null || HandoffIdIn(detailsJson) is not { } handoffId)
        {
            return;
        }

        var handoff = await processors.GetProcessorHandoffAsync(handoffId, cancellationToken);
        if (handoff is null || !ProcessorHandoffStatuses.CanDismiss(handoff.Status, handoff.FailureMessage))
        {
            return;
        }

        // The listener this triggers closes any other issue about the same hand-off.
        await processors.UpdateProcessorHandoffAsync(handoff.Id, ProcessorHandoffStatuses.Dismissed, null, null, handoff.FailureMessage, cancellationToken);
        if (activityFeed is not null)
        {
            await activityFeed.RecordActivityAsync(
                "processing.dismissed",
                $"{handoff.ReleaseName}'s hand-off to {handoff.ProcessorName ?? "the processor"} was dismissed with its import issue.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, PreviousStatus = handoff.Status }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    /// <summary>Removes the System row of a closed import issue and tells every open screen to read health again.</summary>
    public static async Task ClearHealthRowAsync(
        string caseId,
        IHealthCheckRepository? healthChecks,
        IRealtimeEventPublisher? realtime,
        CancellationToken cancellationToken)
    {
        if (healthChecks is null)
        {
            return;
        }

        try
        {
            if (await healthChecks.GetAsync(OutputIssueRowId(caseId), cancellationToken) is null)
            {
                return;
            }

            await healthChecks.RemoveAsync(OutputIssueRowId(caseId), cancellationToken);
            if (realtime is not null)
            {
                await realtime.PublishHealthChangedAsync("system.health", "healthy", "0 problem(s) started, 1 cleared.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The row is a mirror; the next health pass drops it if this could not.
        }
    }
}
