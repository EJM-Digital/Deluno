namespace Deluno.Contracts;

/// <summary>
/// Asks the connected download clients whether any of them still holds a
/// file (#1171). A manual import has no download behind it, so nothing else
/// can tell it that Transmission is seeding the file it is about to copy:
/// deleting the source afterwards breaks the torrent and, on a private site,
/// costs the user their ratio.
/// </summary>
public interface IClientHeldFileProbe
{
    /// <summary>
    /// True when any connected client lists this file, or the folder it sits
    /// in, as its own. When the clients cannot be asked the answer is true:
    /// leaving a file behind wastes disk and says so; deleting one a client
    /// is sharing cannot be undone.
    /// </summary>
    Task<bool> IsHeldByAnyClientAsync(string filePath, CancellationToken cancellationToken);
}

public static class ClientHeldFiles
{
    /// <summary>
    /// Whether <paramref name="claimedPath"/> (what a client says it owns: a
    /// file or a folder) is this file or contains it. Compared as text so a
    /// Windows path means the same on the Linux build agents.
    /// </summary>
    public static bool Claims(string? claimedPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(claimedPath) || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimEnd('/');
        var claimed = Normalize(claimedPath);
        var file = Normalize(filePath);
        return claimed.Length > 0
            && (string.Equals(claimed, file, StringComparison.OrdinalIgnoreCase)
                || file.StartsWith(claimed + "/", StringComparison.OrdinalIgnoreCase));
    }
}
