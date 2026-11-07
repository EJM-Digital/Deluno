using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>
/// Before an update is applied, stops the download clients Deluno started (#1532). Velopack's Setup renames the install
/// folder, and a client that was started by an older build stands in that folder and makes the rename fail with "Access is
/// denied". Builds with the working-directory fix never do, but the build that applies an update is the OLD one, so this
/// covers updates from a build without it. The tray's normal start brings the clients back from the same list.
/// </summary>
public static class ClientsStoppedForUpdate
{
    /// <summary>Stops them. Replaceable so a test never touches a real process.</summary>
    public static Action Stop { get; set; } = () => StopStarted(new FileStartedComponentsRegistry(), BundledComponents.Load(), new RealProcessControl(), message => HookLog.Write(message, TrayStartLog.FileName));

    /// <summary>The names (without .exe) of the programs of every client in <paramref name="registry"/>, and stops each one running.</summary>
    public static IReadOnlyList<string> StopStarted(IStartedComponentsRegistry registry, IEnumerable<BundledComponentSpec> specs, IProcessControl control, Action<string>? log = null)
    {
        var stopped = new List<string>();
        try
        {
            var remembered = registry.Read();
            foreach (var spec in specs)
            {
                if (!spec.RestartsAtSignIn || string.IsNullOrWhiteSpace(spec.PostInstallCommand)
                    || !remembered.Contains(spec.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(ComponentPaths.Expand(spec.PostInstallCommand!));
                if (control.StopAll(name) > 0)
                {
                    stopped.Add(spec.Kind);
                    log?.Invoke($"{spec.DisplayName} was stopped so the update can replace Deluno's folder; Deluno starts it again afterwards.");
                }
            }
        }
        catch (Exception exception)
        {
            // The update still applies; a client left running is what it did before.
            log?.Invoke($"Stopping the download clients before the update failed: {exception.Message}");
        }

        return stopped;
    }
}

public interface IProcessControl
{
    /// <summary>Stops every running program with this name (no .exe); the number stopped.</summary>
    int StopAll(string name);
}

public sealed class RealProcessControl : IProcessControl
{
    public int StopAll(string name)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    count++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours to stop.
                }
            }
        }

        return count;
    }
}
