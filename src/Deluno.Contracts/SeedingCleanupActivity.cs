namespace Deluno.Contracts;

/// <summary>
/// The Activity row written when retention removes a finished torrent from a download client once it had
/// shared enough (#1098). The sentence is the one place it is said, in plain words; the Live screen counts
/// these rows for "cleaned up today".
/// </summary>
public static class SeedingCleanupActivity
{
    public const string Category = "download.sharing.reclaimed";

    public static string Message(string title, string clientName)
        => $"Removed {title} from {clientName} once it had shared enough · your copy stays in the library";
}
