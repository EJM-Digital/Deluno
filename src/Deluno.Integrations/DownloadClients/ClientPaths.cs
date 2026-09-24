namespace Deluno.Integrations.DownloadClients;

/// <summary>Paths as a download client writes them, which may not be how this host writes them.</summary>
public static class ClientPaths
{
    /// <summary>
    /// A folder as the client will use it: <paramref name="folder"/> as it is
    /// when it is already a full path, otherwise inside <paramref name="root"/>.
    /// Joined the way the root is written (a Windows path for a Windows client,
    /// a POSIX one for Linux or Docker), because the client, not Deluno, reads
    /// it (#616). Null when there is nothing to go on.
    /// </summary>
    public static string? ClientFolder(string? root, string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && IsFullClientPath(folder.Trim()))
        {
            return folder.Trim().TrimEnd('/', '\\');
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var trimmedRoot = root.Trim().TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(folder))
        {
            return trimmedRoot;
        }

        var separator = trimmedRoot.Contains('/') && !trimmedRoot.Contains('\\') ? '/' : '\\';
        return trimmedRoot + separator + folder.Trim().Trim('/', '\\');
    }

    /// <summary>A drive path, a UNC share or a POSIX absolute path.</summary>
    public static bool IsFullClientPath(string path)
        => path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal)
           || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

}
