using Deluno.Jobs.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// A processor that did not take a hand-off because another connection owns the file (#1250, Weir #956): two
/// connections share one download folder. Nothing is wrong with the release and the processor holds nothing for this
/// hand-off, so Deluno sends it again after a back-off and only after a few tries says, once, why it keeps happening.
/// Never a held case, never a refusal or blocklisting, and the download is never touched.
/// </summary>
public static class ProcessorNotTaken
{
    /// <summary>The Activity line for each try that will be sent again. The count of these is the count of tries.</summary>
    public const string TriedCategory = "processing.not-taken";

    /// <summary>The Activity line for the one problem raised after the last try. It starts a fresh count.</summary>
    public const string GaveUpCategory = "processing.not-taken.gave-up";

    /// <summary>How long Deluno waits before sending again after the first, second and third refusal.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryAfter = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    private const string BusyEnding = " is busy with this file for another connection; Deluno will send it again shortly.";

    /// <summary>The sentence kept on the hand-off while it waits to be sent again.</summary>
    public static string BusyMessage(string processorName) => processorName + BusyEnding;

    public static bool IsBusyMessage(string? failureMessage)
        => failureMessage is not null && failureMessage.EndsWith(BusyEnding, StringComparison.Ordinal);

    /// <summary>The one problem, after the last try: what happens, why, and what to do about it.</summary>
    public static string GaveUpMessage(string processorName, string releaseName)
        => $"Another connection keeps taking {releaseName} before {processorName} can, so Deluno stopped sending it. This happens when two connections share one download folder. Give each connection its own download folder, then send it again.";

    /// <summary>How many times the hand-off has been refused since it last became a problem.</summary>
    public static int TriesSoFar(IEnumerable<ActivityEventItem> activity)
    {
        var tries = 0;
        foreach (var line in activity.OrderByDescending(item => item.CreatedUtc))
        {
            if (line.Category == GaveUpCategory)
            {
                break;
            }

            if (line.Category == TriedCategory)
            {
                tries++;
            }
        }

        return tries;
    }

    /// <summary>True while a hand-off waiting to be sent again has not yet waited its back-off.</summary>
    public static bool RetryNotDue(ProcessorHandoffItem handoff, int tries, DateTimeOffset now)
        => tries > 0
           && string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
           && IsBusyMessage(handoff.FailureMessage)
           && now < handoff.UpdatedUtc + RetryAfter[Math.Min(tries, RetryAfter.Count) - 1];
}
