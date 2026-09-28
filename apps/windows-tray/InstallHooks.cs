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
/// <para>Velopack's install and update callbacks must finish within 30
/// seconds and are not meant to show anything. They used to run the firewall
/// step when already elevated, but on a clean VM checked into an elevated
/// Administrator session that step itself -- the <c>netsh</c> call, run
/// elevated -- pushed a cold start (18s before the first log line) past the
/// 30 second budget and Velopack killed the hook (#968). The fast callbacks
/// now never touch the firewall, elevated or not: they log one line and
/// return. The rule still gets added -- from <see cref="OnFirstRun"/> after a
/// fresh install, and from the tray's own "Allow other devices" item (offered
/// whenever <c>LanAccess.CanAllow</c> is true) on every normal start,
/// including the first start after an update, which has no first run of its
/// own.</para>
/// </summary>
internal static class InstallHooks
{
    /// <summary>Uninstall's limit is 15 seconds; leave room to exit cleanly.</summary>
    private static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Test seam only. <see cref="OnAfterInstall"/> no longer reads this --
    /// the fast callback never runs the firewall step, elevated or not (see
    /// class remarks, #968) -- but a test still sets it to prove that holds
    /// in both elevation states without depending on how the test runner
    /// itself happens to be running. Defaults to the real check.
    /// </summary>
    internal static Func<bool> IsElevatedCheck { get; set; } = () => new WindowsFirewall().IsElevated;

    /// <summary>
    /// Test seam only. <see cref="OnAfterInstall"/> no longer calls this --
    /// see class remarks (#968) -- so a test uses it only to detect (and
    /// forbid) a call into <see cref="Firewall"/> -- which touches
    /// <c>DelunoServer</c>'s statics and <c>AppSettings.Load()</c> -- without
    /// it actually running. <see cref="OnFirstRun"/> still adds the rule, but
    /// through <see cref="Firewall"/> directly, not through this seam.
    /// Defaults to the real factory.
    /// </summary>
    internal static Func<WindowsFirewallLanAccess> FirewallFactory { get; set; } = Firewall;

    /// <summary>
    /// After install or after update: Velopack kills this callback after 30
    /// seconds, and the firewall step -- <see cref="FirewallFactory"/>, which
    /// touches <c>DelunoServer</c>'s statics and <c>AppSettings.Load()</c>,
    /// then runs <c>netsh</c> -- has blown that budget even when elevated: a
    /// clean-VM run under an elevated Administrator took 18 seconds just to
    /// reach its first log line, and the elevated <c>netsh</c> call pushed it
    /// past 30s and Velopack killed the hook (#968). So this fast callback
    /// never runs the firewall step, elevated or not; it only logs. A fresh
    /// install still gets the rule from <see cref="OnFirstRun"/>, and an
    /// update still ends up with it because the tray offers (and, once
    /// allowed, no longer needs to offer) the same step on every normal
    /// start.
    /// </summary>
    public static void OnAfterInstall()
    {
        try
        {
            LogHook("after install/update: starting; the firewall step runs from first run or the tray, not from this hook.");
        }
        catch (Exception ex)
        {
            // An install must never fail over this hook; the tray and System
            // still offer the firewall step on first run or normal start.
            LogHook($"after install/update: failed: {ex.Message}");
        }
    }

    /// <summary>
    /// First run after install: asks Windows, once, to let other devices reach
    /// Deluno (#623), then offers the component picker (#640). Both need an
    /// interactive prompt, and first run is Velopack's one moment for that --
    /// its other callbacks must return in seconds and show nothing.
    /// </summary>
    public static void OnFirstRun()
    {
        // Before ComponentPickerDialog (#640) -- a Form -- can be created.
        // See Program.EnsureWinFormsBootstrap for why this cannot just run
        // unconditionally at the top of Main instead.
        Program.EnsureWinFormsBootstrap();
        Run(() => Firewall().AllowAsync(CancellationToken.None), "first run");
        RunComponentPicker();
    }

    private static void RunComponentPicker()
    {
        try
        {
            var results = ComponentPickerDialog.ShowOnce();
            foreach (var result in results)
            {
                LogHook($"component '{result.Kind}': {result.Outcome}: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            // Never let an installer-picker failure stop first run from finishing.
            LogHook($"component picker failed: {ex.Message}");
        }
    }

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
