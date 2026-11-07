using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>What the uninstall's service step looks at and does, so a test sees each decision without touching the computer's services.</summary>
internal interface IServiceUninstallEnvironment
{
    bool DelunoServiceExists();

    /// <summary>True when a SABnzbd Windows service exists and Deluno registered it (the person's own is not ours to remove).</summary>
    bool SabnzbdServiceMadeByDeluno();

    /// <summary>Runs <c>Deluno.exe --uninstall-service</c> elevated: one Windows prompt. False when it was declined or could not start.</summary>
    bool RunElevatedUninstallService();

    void Log(string message);
}

/// <summary>
/// Uninstalling Deluno while it runs as a Windows service used to leave the service behind, stopped and pointing at the deleted program (#1462).
/// Removing a service needs administrator rights and an uninstall from Apps and features is not always elevated, so when Deluno's service (or a
/// SABnzbd service Deluno made) is there, the uninstall runs the existing <c>--uninstall-service</c> path elevated, once. A tray-mode uninstall has
/// nothing to remove and shows no prompt. A declined prompt does not stop the uninstall; the log says in plain words what is left and how to remove it.
/// </summary>
internal static class ServiceUninstall
{
    internal static void Run(IServiceUninstallEnvironment env)
    {
        try
        {
            var deluno = env.DelunoServiceExists();
            var sabnzbd = env.SabnzbdServiceMadeByDeluno();
            if (!deluno && !sabnzbd)
            {
                return;
            }

            try
            {
                env.RunElevatedUninstallService();
            }
            catch (Exception ex)
            {
                env.Log($"The Windows service could not be removed: {ex.Message}");
            }

            var left = false;
            if (deluno && env.DelunoServiceExists())
            {
                left = true;
                env.Log(Leftover("Deluno", "Deluno"));
            }

            if (sabnzbd && env.SabnzbdServiceMadeByDeluno())
            {
                left = true;
                env.Log(Leftover("SABnzbd", "SABnzbd"));
            }

            if (!left)
            {
                env.Log("Deluno's Windows service" + (sabnzbd ? " and the SABnzbd service Deluno made were removed." : " was removed."));
            }
        }
        catch (Exception ex)
        {
            env.Log($"The Windows service step failed: {ex.Message}");
        }
    }

    private static string Leftover(string name, string commandName)
        => $"The Windows service '{name}' is still installed because it was not removed with Deluno. To remove it, open a Command Prompt as administrator and run: sc.exe delete {commandName}";
}

internal sealed class RealServiceUninstallEnvironment : IServiceUninstallEnvironment
{
    // The uninstall hook is given 15 seconds; the prompt and the removal must fit in what is left.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(12);

    private readonly Lazy<SabnzbdService> _sabnzbd = new(() => SabnzbdService.ForThisComputer());

    public bool DelunoServiceExists() => ServiceManager.IsInstalled();

    public bool SabnzbdServiceMadeByDeluno()
    {
        try
        {
            return _sabnzbd.Value.MadeByDeluno();
        }
        catch
        {
            return false;
        }
    }

    public bool RunElevatedUninstallService()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--uninstall-service", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Deluno.Infrastructure.Processes.ProcessWorkingDirectory.Neutral });
            return p is not null && p.WaitForExit(Limit);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the prompt was declined
        }
    }

    public void Log(string message) => HookLog.Write(message);
}
