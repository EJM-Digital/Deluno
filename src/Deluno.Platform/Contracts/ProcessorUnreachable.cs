using Deluno.Jobs.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// A hand-off whose send did not reach its processor (#1385): the request timed out or found nothing listening, so the
/// processor never answered. Every send carries the same idempotency key, so sending it again is safe; a processor that
/// is restarting, updating or briefly busy answers a minute later. The hand-off waits and is sent again after a back-off,
/// and only after the last try does it become one problem a person can clear. A refusal the processor gave in its own
/// words is not this: that fails at once.
/// </summary>
public static class ProcessorUnreachable
{
    /// <summary>The Activity line for each try that will be sent again. The count of these is the count of tries.</summary>
    public const string TriedCategory = "processing.unreachable";

    /// <summary>How long Deluno waits before sending again after the first, second and third send that did not get through.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryAfter = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private const string WaitingEnding = " did not answer; Deluno will send it again shortly.";

    /// <summary>True for a send that got no answer at all: no status code and no back-off the processor asked for.</summary>
    public static bool IsUnreachable(ProcessorSubmissionResult submission)
        => !submission.IsAccepted && submission.StatusCode is null && submission.RetryAfter is null;

    /// <summary>The sentence kept on the hand-off while it waits to be sent again.</summary>
    public static string WaitingMessage(string processorName) => processorName + WaitingEnding;

    public static bool IsWaitingMessage(string? failureMessage)
        => failureMessage is not null && failureMessage.EndsWith(WaitingEnding, StringComparison.Ordinal);

    /// <summary>The one problem, after the last try: what happened and what to check.</summary>
    public static string GaveUpMessage(string processorName)
        => $"Deluno could not reach {processorName} after {RetryAfter.Count + 1} tries. Check that {processorName} is running and its connection in Deluno is right, then send it again.";

    /// <summary>How many sends in a row have not got through since the hand-off last reached its processor or became a problem.</summary>
    public static int TriesSoFar(IEnumerable<ActivityEventItem> activity) => SoFar(activity).Tries;

    /// <summary>
    /// The tries so far, and the revision the first of them carried in its idempotency key. Marking the hand-off waiting
    /// moves its revision, so without this each try would carry a new key, and a send that did reach the processor
    /// before the answer was lost would be done twice. Every try in a run is sent with the first one's key.
    /// </summary>
    public static (int Tries, DateTimeOffset? KeyRevision) SoFar(IEnumerable<ActivityEventItem> activity)
    {
        var tries = 0;
        DateTimeOffset? revision = null;
        foreach (var line in activity.OrderByDescending(item => item.CreatedUtc))
        {
            if (line.Category is "processing.submitted" or "processing.submission-failed")
            {
                break;
            }

            if (line.Category == TriedCategory)
            {
                tries++;
                revision = KeyRevisionOf(line.DetailsJson) ?? revision;
            }
        }

        return (tries, revision);
    }

    private static DateTimeOffset? KeyRevisionOf(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailsJson);
            return document.RootElement.TryGetProperty("KeyRevisionMs", out var ms) && ms.TryGetInt64(out var value)
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>True while a hand-off waiting to be sent again has not yet waited its back-off.</summary>
    public static bool RetryNotDue(ProcessorHandoffItem handoff, int tries, DateTimeOffset now)
        => tries > 0
           && string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
           && IsWaitingMessage(handoff.FailureMessage)
           && now < handoff.UpdatedUtc + RetryAfter[Math.Min(tries, RetryAfter.Count) - 1];
}
