using Deluno.Tray;
using Velopack;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        try
        {
            // Hooks below run from elevated Setup.exe / Update.exe contexts;
            // they're the right place to make machine-wide changes (firewall
            // rule, etc.) the unprivileged tray cannot. The hooks exit the
            // process when fired, so the rest of Main is skipped on install.
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnAfterInstallFastCallback(_ => InstallHooks.OnAfterInstall())
                .OnAfterUpdateFastCallback(_ => InstallHooks.OnAfterInstall())
                .OnBeforeUninstallFastCallback(_ => InstallHooks.OnBeforeUninstall())
                .Run();

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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new TrayApplication());
        }
        catch (Exception ex)
        {
            TryLogStartupFailure(ex);
            throw;
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
