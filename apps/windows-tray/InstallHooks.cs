using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>
/// Velopack install/uninstall lifecycle callbacks. These run from inside
/// <c>Setup.exe</c> (or <c>Update.exe</c>) — both of which are elevated by
/// the installer's UAC prompt — so they're the right place to make
/// machine-wide changes that the unprivileged tray cannot.
///
/// Right now there's one such change: a Windows Firewall inbound allow
/// rule for the default port, so LAN devices can reach Deluno's UI
/// immediately after install without the user having to run any commands.
/// </summary>
internal static class InstallHooks
{
    /// <summary>
    /// Called by Velopack after a fresh install or after an update is
    /// applied. Adds an idempotent firewall allow rule for the default port.
    /// If the user has changed the port in <c>deluno.json</c>, the running
    /// tray will also try to add a rule for the configured port (see
    /// <see cref="DelunoServer"/>) — this hook only guarantees the default
    /// port works out of the box.
    /// </summary>
    public static void OnAfterInstall()
    {
        try
        {
            EnsureFirewallRule(new AppSettings().Port); // default port from class initializer
        }
        catch
        {
            // The installer should never crash because of a firewall rule
            // failure. Worst case the user runs the netsh command manually.
        }
    }

    /// <summary>
    /// Called by Velopack just before uninstall removes the install
    /// directory. Cleans up the firewall rule we added so we don't leave
    /// orphan rules behind.
    /// </summary>
    public static void OnBeforeUninstall()
    {
        try
        {
            RemoveFirewallRule(new AppSettings().Port);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void EnsureFirewallRule(int port)
    {
        var ruleName = $"Deluno (port {port})";

        // Idempotent: if a rule with this name already exists, skip.
        var probe = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        if (probe.ExitCode == 0)
        {
            LogHook($"firewall rule '{ruleName}' already exists; skipping add.");
            return;
        }

        var add = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");
        if (add.ExitCode == 0)
            LogHook($"firewall rule '{ruleName}' added for port {port}.");
        else
            LogHook($"firewall rule add failed (exit {add.ExitCode}): {add.Output}");
    }

    private static void RemoveFirewallRule(int port)
    {
        var ruleName = $"Deluno (port {port})";
        var del = RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        LogHook($"firewall rule '{ruleName}' delete exit={del.ExitCode}.");
    }

    private static (int ExitCode, string Output) RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "Failed to launch netsh.");
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Install hooks run before the tray's normal logging is set up.
    /// Append directly to the install-hook log so a packaging issue is
    /// at least traceable after the fact.
    /// </summary>
    private static void LogHook(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "install-hook.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // No telemetry channel from the installer; swallow.
        }
    }
}
