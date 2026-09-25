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
        // Must run before the first Win32Window (Form) is created anywhere in
        // the process, not just before TrayApplication's own. On first run,
        // OnFirstRun below shows ComponentPickerDialog (#640) -- a Form --
        // before RunAsync ever gets to its own Application setup. These calls
        // used to live in RunAsync, after that dialog could already exist,
        // and SetCompatibleTextRenderingDefault throws
        // InvalidOperationException the moment that happens: "must be called
        // before the first IWin32Window object is created in the
        // application." That crashed Deluno.exe on every fresh install that
        // showed the picker -- which is every fresh install, by design --
        // right after first run finished, before the app ever served a
        // request.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

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

            // EnableVisualStyles/SetCompatibleTextRenderingDefault/SetHighDpiMode
            // already ran at the top of Main, before VelopackApp could show
            // the first-run component picker.
            Application.Run(new TrayApplication());
        }
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
