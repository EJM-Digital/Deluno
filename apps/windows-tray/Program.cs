using System.Diagnostics;
using Deluno.Tray;
using Velopack;

internal static class Program
{
    /// <summary>
    /// The real entry point, and deliberately synchronous.
    ///
    /// <para>Velopack's install, update and uninstall hooks have to run at the
    /// very start of the process, before anything else touches the app
    /// directory. An <c>async Task Main</c> looks like it does that in source
    /// and does not in fact: the compiler rewrites it into a state machine, so
    /// the hooks end up inside <c>MoveNext</c> behind the runtime's async
    /// setup. <c>vpk</c> warns about exactly this — <i>"VelopackApp.Run() was
    /// found in method MoveNext, which does not look like your application's
    /// entry point"</i> — and the failure it is warning about is an update that
    /// half-applies, which is the one path a packaged app cannot afford to get
    /// wrong.</para>
    ///
    /// <para>So the hooks run here, synchronously, and the rest of startup is
    /// handed to <see cref="RunAsync"/> afterwards.</para>
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostic only (#968): how long the OS took to create the process
        // and the CLR took to JIT and reach this line, before Velopack's hook
        // budget (30s install/update, 15s uninstall) even starts ticking on
        // its side. A clean-VM run once showed velopack.log logging "found
        // fast exit hook" and then nothing at all from Deluno.exe within 30s
        // -- this line, and the one line it writes, is what tells us whether
        // that gap was cold-start-before-Main or something inside the hook
        // body itself. Process.GetCurrentProcess().StartTime and a single
        // AppendAllText are the only cost paid here; neither touches
        // WinForms, ASP.NET, EF or any Deluno.* assembly.
        LogColdStart();

        // Setup.exe and Update.exe run these per user, without administrator
        // rights, and kill them after 30 seconds (15 on uninstall). So the
        // firewall rule (#623) is only added here when the rights are already
        // there. The Windows permission prompt comes on the first run instead.
        // The install hooks exit the process when fired, so nothing after
        // this runs on install.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => InstallHooks.OnAfterInstall())
            .OnAfterUpdateFastCallback(_ => InstallHooks.OnAfterInstall())
            .OnBeforeUninstallFastCallback(_ => InstallHooks.OnBeforeUninstall())
            .OnFirstRun(_ => InstallHooks.OnFirstRun())
            .Run();

        try
        {
            RunAsync(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            TryLogStartupFailure(ex);
            throw;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        {
            // --port N override (diagnostic). Takes precedence over AppSettings.Port
            // for THIS run only — does not persist to deluno.json. Useful when the
            // configured port is blocked by another listener.
            DelunoServer.PortOverride = TryParsePortOverride(args);

            // Service mode
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            {
                await ServiceHost.RunAsync(args);
                return;
            }

            // Service management
            if (args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceManager.Install(args);
                return;
            }

            if (args.Contains("--uninstall-service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceManager.Uninstall();
                return;
            }

            // Tray app mode with single-instance guard
            using var mutex = new Mutex(true, @"Global\DelunoTrayApplication", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                NativeMethods.PostMessage(
                    NativeMethods.FindWindow(null, "Deluno"),
                    NativeMethods.WM_DELUNO_SHOW, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            EnsureWinFormsBootstrap();
            Application.Run(new TrayApplication());
        }
    }

    private static bool _winFormsBootstrapped;

    /// <summary>
    /// EnableVisualStyles/SetCompatibleTextRenderingDefault/SetHighDpiMode
    /// must run before the first Win32Window (Form) is created anywhere in
    /// the process -- and must NOT run unconditionally at the top of Main:
    /// that was tried and reverted. Main also runs for Velopack's other
    /// lifecycle invocations (OnAfterInstall, OnBeforeUninstall, and an
    /// internal "--veloapp-install" fast-exit helper process, confirmed live
    /// via velopack.log: "Found fast exit hook: --veloapp-install" was the
    /// last line logged, then that process just sat there, never installing
    /// anything, for minutes past Velopack's own 30-second budget for this
    /// callback). None of those paths show a Form or need WinForms
    /// initialized at all, and initializing it unconditionally broke at
    /// least one of them.
    ///
    /// <para>So this runs lazily, guarded so it only executes once, from the
    /// two places that actually need it: <see cref="InstallHooks.OnFirstRun"/>,
    /// right before it can show the component picker (#640) -- a Form -- and
    /// <see cref="RunAsync"/>, right before showing the tray icon itself.
    /// Either can run first in a given process (a fresh install's first
    /// launch hits OnFirstRun first, in the same process, before RunAsync
    /// gets here); the guard makes calling it from both safe.</para>
    /// </summary>
    internal static void EnsureWinFormsBootstrap()
    {
        if (_winFormsBootstrapped)
        {
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        _winFormsBootstrapped = true;
    }

    private static int? TryParsePortOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var p)
                && p > 0 && p < 65536)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes one line to the same install-hook.log the hooks use, so a
    /// cold-start gap and a hook-body gap show up on the same timeline
    /// (#968). Best-effort: a failure here must never stop startup, and
    /// nothing before this point may depend on it running.
    /// </summary>
    private static void LogColdStart()
    {
        try
        {
            var elapsed = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno",
                "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "install-hook.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} Main entered {elapsed.TotalMilliseconds:F0}ms after process start.{Environment.NewLine}");
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static void TryLogStartupFailure(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno",
                "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "tray-startup.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} startup failure{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort startup diagnostics only.
        }
    }
}
