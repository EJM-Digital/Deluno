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

    /// <summary>Whether this record's download was moved to keep seeding and is no longer Deluno's.</summary>
    public static bool IsReleased(DownloadDispatchItem dispatch)
        => string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase)
           && dispatch.ImportFailureMessage?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true;
}
