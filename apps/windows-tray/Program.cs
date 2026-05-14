using Deluno.Tray;
using Velopack;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        try
        {
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

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
