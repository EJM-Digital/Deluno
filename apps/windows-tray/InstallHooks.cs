using Deluno.Api.Network;

namespace Deluno.Tray;

/// <summary>
/// Velopack install lifecycle callbacks, and the one machine-wide change
/// Deluno makes: a Windows Firewall rule so other devices on the network can
/// reach it (#623).
///
/// <para>These used to add the rule from the after-install callback on the
/// belief that Setup.exe runs elevated. It does not: Velopack installs per
/// user, without administrator rights, so netsh refused every time, and the
/// only record was a line in install-hook.log. Other devices simply could not
/// connect.</para>
///
/// <para>Velopack's install, update and uninstall callbacks must finish within
/// 30 seconds (15 for uninstall) and are not meant to show anything, so they
/// only act when they already have the rights. The Windows permission prompt
/// comes on the first run after install, which Velopack runs once as the app
/// itself. If that prompt is declined, the tray menu and System keep offering
/// the same step.</para>
/// </summary>
internal static class InstallHooks
{
    /// <summary>Uninstall's limit is 15 seconds; leave room to exit cleanly.</summary>
    private static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(12);

    /// <summary>After install or update: adds the rule only when this process may already do so.</summary>
    public static void OnAfterInstall()
    {
        var firewall = Firewall();
        if (!new WindowsFirewall().IsElevated)
        {
            LogHook($"'{firewall.RuleName}': not elevated here; the first run asks Windows for permission.");
            return;
        }

        Run(() => firewall.AllowAsync(CancellationToken.None), "after install");
    }

    /// <summary>First run after install: asks Windows, once, to let other devices reach Deluno.</summary>
    public static void OnFirstRun() => Run(() => Firewall().AllowAsync(CancellationToken.None), "first run");

    /// <summary>Before uninstall: removes the rule Deluno added, so nothing is left behind.</summary>
    public static void OnBeforeUninstall()
    {
        var firewall = Firewall();
        try
        {
            using var budget = new CancellationTokenSource(UninstallBudget);
            // Only Deluno's own rule: another rule for the same port is not ours to remove.
            if (!firewall.HasOwnRule())
            {
                return;
            }

            var removed = firewall.RemoveAsync(budget.Token).GetAwaiter().GetResult();
            LogHook($"'{firewall.RuleName}' delete exit={removed.ExitCode}{(removed.Declined ? " (permission declined)" : string.Empty)}.");
        }
        catch (Exception ex)
        {
            LogHook($"'{firewall.RuleName}' delete failed: {ex.Message}");
        }
    }

    private static WindowsFirewallLanAccess Firewall()
        => new(DelunoServer.PortOverride ?? AppSettings.Load().Port);

    private static void Run(Func<Task<LanAccessStatus>> step, string when)
    {
        try
        {
            var status = step().GetAwaiter().GetResult();
            LogHook($"{when}: {status.State}: {status.Message}");
        }
        catch (Exception ex)
        {
            // An install must never fail over a firewall rule; the tray and
            // System still offer the step.
            LogHook($"{when}: firewall rule failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Install hooks run before the tray's normal logging is set up. Append
    /// directly to the install-hook log so a packaging issue is at least
    /// traceable after the fact.
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
