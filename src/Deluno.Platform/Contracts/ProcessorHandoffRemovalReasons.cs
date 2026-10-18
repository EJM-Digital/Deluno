namespace Deluno.Platform.Contracts;

/// <summary>
/// Why a hand-off was closed because its download left the client (#1216, #1220), written once so the writer and the reader
/// of the words cannot drift apart. A processor that had already finished the file reports it afterwards; a hand-off closed
/// this way answers that report as already handled (#1242), because Deluno itself ended the wait and told nobody.
/// </summary>
public static class ProcessorHandoffRemovalReasons
{
    public static string MovedToKeepSeeding(string releaseName, string category, string clientName)
        => $"Deluno moved {releaseName} to {category} on {clientName} so it keeps seeding, so it will not import it.";

    public static string StoppedTracking(string releaseName)
        => $"You told Deluno to stop tracking {releaseName}, so Deluno will not import it.";

    public static string Removed(string releaseName, string clientName)
        => $"Deluno removed {releaseName} from {clientName}, so it will not import it.";

    /// <summary>Whether a closed hand-off's recorded reason is one of the above: its download was removed from, or moved out of, its client.</summary>
    public static bool IsOne(string? reason)
        => !string.IsNullOrWhiteSpace(reason)
           && ((reason.StartsWith("Deluno moved ", StringComparison.Ordinal) && reason.EndsWith(" so it keeps seeding, so it will not import it.", StringComparison.Ordinal))
               || (reason.StartsWith("You told Deluno to stop tracking ", StringComparison.Ordinal) && reason.EndsWith(", so Deluno will not import it.", StringComparison.Ordinal))
               || (reason.StartsWith("Deluno removed ", StringComparison.Ordinal) && reason.EndsWith(", so it will not import it.", StringComparison.Ordinal)));
}
