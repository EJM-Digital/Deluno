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

    /// <summary>
    /// Test seam only: replaces the elevation check so a test can force the
    /// non-elevated, fast-return path without depending on how the test
    /// runner itself happens to be running. Defaults to the real check.
    /// </summary>
    internal static Func<bool> IsElevatedCheck { get; set; } = () => new WindowsFirewall().IsElevated;

    /// <summary>
    /// Test seam only: lets a test detect (or forbid) a call into
    /// <see cref="Firewall"/> -- which touches <c>DelunoServer</c>'s statics
    /// and <c>AppSettings.Load()</c> -- without it actually running. Defaults
    /// to the real factory.
    /// </summary>
    internal static Func<WindowsFirewallLanAccess> FirewallFactory { get; set; } = Firewall;

    /// <summary>
    /// After install or update: adds the rule only when this process may
    /// already do so. Velopack kills this callback after 30 seconds, so the
    /// very first thing it does is log, and the common case -- not elevated,
    /// which is every ordinary install -- returns before touching
    /// <see cref="Firewall"/> (and so <c>DelunoServer</c>'s statics and
    /// <c>AppSettings.Load()</c>) at all. A cold, non-elevated run once took
    /// 23+ seconds to reach its first log line for exactly that reason and
    /// blew the budget (#968).
    /// </summary>
    public static void OnAfterInstall()
    {
        try
        {
            LogHook("after install: starting.");

            if (!IsElevatedCheck())
            {
                LogHook("not elevated here; the first run asks Windows for permission.");
                return;
            }

            var firewall = FirewallFactory();
            Run(() => firewall.AllowAsync(CancellationToken.None), "after install");
        }
        catch (Exception ex)
        {
            // An install must never fail over this hook; the tray and System
            // still offer the firewall step on first run.
            LogHook($"after install: failed: {ex.Message}");
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
