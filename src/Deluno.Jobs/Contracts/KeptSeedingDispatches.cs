namespace Deluno.Jobs.Contracts;

/// <summary>
/// A download Deluno moved to a keep-seeding category and stopped tracking (#1240). Its record is closed with the
/// code a download Deluno's own strike rules handled carries, and a message that starts with
/// <see cref="MessagePrefix"/>; from then on the torrent is not Deluno's to list, strike, act on or sweep, even
/// though it is still in the client and still seeding. The record is kept so the title's history still tells what
/// became of the release.
/// </summary>
public static class KeptSeedingDispatches
{
    /// <summary>Starts the closing message of a record whose download was moved to keep seeding.</summary>
    public const string MessagePrefix = "Kept seeding: ";

    public static string Message(string releaseName, string category, string clientName, string reason)
        => $"{MessagePrefix}Deluno moved {releaseName} to {category} on {clientName} and stopped tracking it. {reason}".TrimEnd();

    /// <summary>Starts the closing message of a record whose download a person told Deluno to stop tracking (#1356).</summary>
    public const string StoppedTrackingPrefix = "Stopped tracking: ";

    public static string StoppedTrackingMessage(string releaseName, string clientName)
        => $"{StoppedTrackingPrefix}You told Deluno to stop tracking {releaseName}; {clientName} still has it, untouched.";

    /// <summary>
    /// Whether this record's download was moved to keep seeding, or a person told Deluno to stop tracking it, and is no
    /// longer Deluno's. An archived record otherwise still proves a download is Deluno's (#1062).
    /// </summary>
    public static bool IsReleased(DownloadDispatchItem dispatch)
        => string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase)
           && (dispatch.ImportFailureMessage?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true
               || dispatch.ImportFailureMessage?.StartsWith(StoppedTrackingPrefix, StringComparison.Ordinal) == true);
}
