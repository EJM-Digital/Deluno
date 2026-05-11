using System.Diagnostics;
using System.ServiceProcess;

namespace Deluno.Tray;

public enum ServiceMode { Tray, ServiceLocalSystem, ServiceRunAsUser }

public static class ServiceManager
{
    private const string ServiceName = "Deluno";
    private static readonly string ExePath =
        Path.Combine(AppContext.BaseDirectory, "Deluno.exe");

    public static void Install(string[] args)
    {
        // args: --install-service [--username domain\user --password pass]
        string? username = null, password = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--username", StringComparison.OrdinalIgnoreCase)) username = args[i + 1];
            if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase)) password = args[i + 1];
        }

        Apply(
            string.IsNullOrEmpty(username) ? ServiceMode.ServiceLocalSystem : ServiceMode.ServiceRunAsUser,
            username, password);
    }

    public static void Uninstall()
    {
        RemoveService();
        RemoveTrayStartup();
    }

    public static void Apply(ServiceMode mode, string? username, string? password)
    {
        RemoveService();
        RemoveTrayStartup();

        switch (mode)
        {
            case ServiceMode.Tray:
                SetTrayStartup();
                break;

            case ServiceMode.ServiceLocalSystem:
                CreateService(null, null);
                break;

            case ServiceMode.ServiceRunAsUser:
                if (string.IsNullOrWhiteSpace(username))
                    throw new ArgumentException("Username required for run-as-user service mode.");
                CreateService(username, password);
                break;
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    public static ServiceControllerStatus? CurrentStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch { return null; }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static void CreateService(string? username, string? password)
    {
        var binPath = $"\"{ExePath}\" --service";

        // sc.exe is the simplest cross-version way to create a service with credentials
        var scArgs = $"create {ServiceName} " +
                     $"binPath= \"{binPath}\" " +
                     $"start= auto " +
                     $"DisplayName= \"Deluno Media Manager\"";

        RunSc(scArgs);
        RunSc($"description {ServiceName} \"Deluno media acquisition and management server\"");

        if (!string.IsNullOrEmpty(username))
        {
            // Set the service logon account — requires a valid Windows account
            RunSc($"config {ServiceName} obj= \"{username}\" password= \"{password ?? ""}\"");
        }

        // Start immediately
        RunSc($"start {ServiceName}");
    }

    private static void RemoveService()
    {
        if (!IsInstalled()) return;
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
    }

    private static void SetTrayStartup()
    {
        var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
        key.SetValue("Deluno", $"\"{ExePath}\"");
    }

    private static void RemoveTrayStartup()
    {
        var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("Deluno", throwOnMissingValue: false);
    }

    private static void RunSc(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",     // UAC elevation
            CreateNoWindow = true
        });
        p?.WaitForExit();
    }
}
