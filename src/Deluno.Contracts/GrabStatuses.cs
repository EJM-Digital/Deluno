namespace Deluno.Contracts;

/// <summary>
/// The one spelling of every grab status (#1412). A grab answers with a result status
/// (<c>notFound</c>), the jobs store keeps it as the stored grab status (<c>not_found</c>), and a
/// dispatch whose client later lost the download keeps <see cref="StoredNotFound"/> as its failure
/// code. Code never spells any of them itself; it uses these and <see cref="ToStored"/>.
/// </summary>
public static class GrabStatuses
{
    // What a grab answers with.
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Planned = "planned";
    public const string Paused = "paused";
    public const string CircuitOpen = "circuitOpen";
    public const string NotFound = "notFound";
    public const string Unready = "unready";
    public const string Unavailable = "unavailable";

    // What the jobs store keeps in grab_status / grab_failure_code.
    public const string StoredSucceeded = "succeeded";
    public const string StoredFailed = "failed";
    public const string StoredPlanned = "planned";
    public const string StoredPaused = "paused";
    public const string StoredCircuitOpen = "circuit_open";
    public const string StoredNotFound = "not_found";

    /// <summary>The stored spelling of a grab result status; an unknown status is kept as it is.</summary>
    public static string ToStored(string status) => status switch
    {
        Sent => StoredSucceeded,
        Failed => StoredFailed,
        Planned => StoredPlanned,
        CircuitOpen => StoredCircuitOpen,
        NotFound => StoredNotFound,
        Paused => StoredPaused,
        _ => status
    };

    /// <summary>True for a stored status or failure code that means the grab is waiting on something, so cleanup leaves it.</summary>
    public static bool IsStoredWaiting(string? stored) => stored is StoredNotFound or StoredPaused or StoredPlanned or StoredCircuitOpen;
}
