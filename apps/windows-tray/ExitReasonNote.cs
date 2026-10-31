namespace Deluno.Tray;

/// <summary>How the person ended Deluno when an update was handed to the installer (#1451).</summary>
internal enum ExitReason { Quit, Restart }

/// <summary>
/// The explicit record of how Deluno ended, left just before it exits for an update. The install hook reads it to decide
/// whether to start Deluno: after "restart" it does, after "quit" it never does (Deluno starts only when asked, #553). A
/// crash writes nothing, and a start clears any record left over, so an old "quit" never decides a later install.
/// Before this the hook guessed from how recent the jobs database's heartbeat was, which is always recent after a Quit.
/// </summary>
internal static class ExitReasonNote
{
    internal static string PathOnThisMachine()
        => Path.Combine(Path.GetDirectoryName(AppSettings.UpdateAppliedNotePath())!, "update-exit-reason.txt");

    internal static void Write(ExitReason reason, string? path = null)
    {
        try
        {
            path ??= PathOnThisMachine();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, reason == ExitReason.Quit ? "quit" : "restart");
        }
        catch
        {
            // Never get in the way of an update; without a record the hook falls back to what it knew before.
        }
    }

    /// <summary>The recorded reason, or null when there is none (a crash, or a build from before the record).</summary>
    internal static ExitReason? Read(string? path = null)
    {
        try
        {
            path ??= PathOnThisMachine();
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim().ToLowerInvariant() switch
            {
                "quit" => ExitReason.Quit,
                "restart" => ExitReason.Restart,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    internal static void Clear(string? path = null)
    {
        try { File.Delete(path ?? PathOnThisMachine()); } catch { /* a leftover record is cleared at the next start */ }
    }
}
