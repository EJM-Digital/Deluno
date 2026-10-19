namespace Deluno.Infrastructure.Storage;

/// <summary>
/// Looks at the data folder before anything opens a database, so a folder Deluno cannot write ends the start with one plain
/// sentence naming the folder and the fix, not a stack trace and a crash code (#1361).
/// </summary>
public static class DataFolderCheck
{
    /// <summary>Null when the folder exists (or can be made) and accepts a file; otherwise the plain message to show.</summary>
    public static string? Problem(string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var probe = Path.Combine(dataRoot, $".deluno-start-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return $"Deluno's data folder {dataRoot} cannot be written to ({ex.Message}). "
                + "Deluno keeps everything it saves there, so it cannot start. "
                + "Fix: let the account Deluno runs as write to that folder. In Docker, mount the folder without ':ro' and give it to the container's user "
                + "(for example: chown -R 1654:1654 on the folder you mounted), then start Deluno again.";
        }
    }
}
