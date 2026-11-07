using Deluno.Contracts;
using System.Diagnostics;
using System.Net.Sockets;
using System.ServiceProcess;

namespace Deluno.Tray;

/// <summary>Runs a program elevated, for the service commands (the tray's way of doing what an administrator would do in a console).</summary>
public interface IServiceCommandRunner
{
    void Run(string fileName, string arguments, string? workingDirectory = null);
}

public sealed class ElevatedCommandRunner : IServiceCommandRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public void Run(string fileName, string arguments, string? workingDirectory = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = true, WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Deluno.Infrastructure.Processes.ProcessWorkingDirectory.Neutral : workingDirectory, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(Limit);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Declined or not there: the caller's next look at the service says what happened.
        }
    }
}

/// <summary>What the SABnzbd service plan looks at and does besides running commands (so a test can see each step and its order).</summary>
public interface ISabnzbdServiceEnvironment
{
    bool ServiceExists();

    /// <summary>The service's start type as "Auto", "Manual" or "Disabled"; null when it is not there.</summary>
    string? StartMode();

    bool FileExists(string path);

    /// <summary>True when this process is in Windows session 0 (a service, PowerShell Direct or WinRM): SABnzbd's service install only works from a signed-in session.</summary>
    bool InSessionZero();

    /// <summary>Deluno's record, in its data folder, that Deluno registered the service. Without it the service is the person's own.</summary>
    bool MarkerExists();

    void WriteMarker();
    void DeleteMarker();
    bool SabnzbdRunning();

    /// <summary>Asks the running SABnzbd to shut down through its API with the person's API key. False when it could not be asked.</summary>
    bool AskToShutDown();

    /// <summary>Waits a bounded time until SABnzbd's process has exited and its port is closed. False when the time ran out.</summary>
    bool WaitUntilStopped();

    void ForceStop();
    void Log(string message);
}

/// <summary>
/// SABnzbd run as its own Windows service while Deluno is a Windows service (#1452). SABnzbd stops at once in session 0 when started as a plain
/// process (its own Windows-service check, whatever the arguments), so a restart with nobody signed in could not bring it back. Its own service
/// mode does: <c>SABnzbd-console.exe -f &lt;ini&gt; install</c> (run from a signed-in session), then start type automatic. It reads the person's
/// settings file named at install, so the API key, folders and categories the install set up are the ones it uses.
/// Deluno records that it registered the service and removes only that one; a SABnzbd service the person already had is left as it is (and kept
/// starting automatically). A running SABnzbd is asked to shut down and waited for before anything forces it, since it may be mid-download.
/// </summary>
public sealed class SabnzbdService(IServiceCommandRunner runner, ISabnzbdServiceEnvironment environment, string installDir, string iniPath)
{
    public const string Name = "SABnzbd";

    public const string NotSetUpMessage =
        "SABnzbd could not be set up as a Windows service, so it will not come back after a restart with nobody signed in. "
        + "Set this up again from a signed-in Windows session (choose the service option in the Deluno tray menu there).";

    /// <summary>Plain words saying why the last <see cref="Register"/> did not set SABnzbd up as a service; null when it did or had nothing to do.</summary>
    public string? Problem { get; private set; }

    public bool IsRegistered() => environment.ServiceExists();

    /// <summary>True when the SABnzbd service is there and Deluno registered it; the person's own service is not ours.</summary>
    public bool MadeByDeluno() => environment.ServiceExists() && environment.MarkerExists();

    /// <summary>True when a SABnzbd service will bring SABnzbd back (Deluno's or the person's own); false, and nothing run, when SABnzbd is not installed.</summary>
    public bool Register()
    {
        Problem = null;
        var console = installDir.TrimEnd('\\') + @"\SABnzbd-console.exe";
        if (!environment.FileExists(console))
        {
            return false;
        }

        if (environment.ServiceExists())
        {
            if (!environment.MarkerExists())
            {
                KeepPersonsOwnService();
                return true;
            }

            Remove();
        }

        if (environment.InSessionZero())
        {
            // SABnzbd-console takes its run-as-a-service path in session 0 whatever the arguments, so the install cannot work from here.
            return NotSetUp();
        }

        StopRunningSabnzbd();
        runner.Run(console, $"-f \"{iniPath}\" install", installDir.TrimEnd('\\'));
        if (!environment.ServiceExists())
        {
            return NotSetUp();
        }

        environment.WriteMarker();
        runner.Run("sc.exe", $"config {Name} start= auto");
        runner.Run("sc.exe", $"start {Name}");
        return true;
    }

    private bool NotSetUp()
    {
        Problem = NotSetUpMessage;
        environment.Log(NotSetUpMessage);
        return false;
    }

    /// <summary>Stops and deletes the service Deluno made. False (and nothing run) when it is not there or is the person's own.</summary>
    public bool Remove()
    {
        if (!environment.ServiceExists())
        {
            if (environment.MarkerExists())
            {
                environment.DeleteMarker();
            }

            return false;
        }

        if (!environment.MarkerExists())
        {
            return false;
        }

        runner.Run("sc.exe", $"stop {Name}");
        runner.Run("sc.exe", $"delete {Name}");
        environment.DeleteMarker();
        return true;
    }

    private void KeepPersonsOwnService()
    {
        if (string.Equals(environment.StartMode(), "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        runner.Run("sc.exe", $"config {Name} start= auto");
        environment.Log("SABnzbd already runs as your own SABnzbd service, so Deluno left it as it is and set it to start automatically, so it comes back after a restart.");
    }

    private void StopRunningSabnzbd()
    {
        if (!environment.SabnzbdRunning())
        {
            return;
        }

        environment.AskToShutDown();
        if (!environment.WaitUntilStopped())
        {
            environment.ForceStop();
        }
    }

    public static string IniPathFor(string dataRoot)
    {
        var local = ServiceProcessStarter.ProfileFoldersFor(dataRoot)?.LocalAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local + @"\sabnzbd\sabnzbd.ini";
    }

    public static SabnzbdService ForThisComputer(string? dataRoot = null)
    {
        var root = dataRoot ?? AppSettings.Load().DataRoot;
        var ini = IniPathFor(root);
        var runner = new ElevatedCommandRunner();
        return new SabnzbdService(runner, new RealSabnzbdEnvironment(root, ini, runner), ComponentPaths.Expand(@"%ProgramFiles%\SABnzbd"), ini);
    }
}

/// <summary>Asking a running SABnzbd to shut down, and waiting for it, on this computer.</summary>
public static class SabnzbdProcessControl
{
    /// <summary>
    /// The address that shuts SABnzbd down: its host and port and the person's API key, read from their settings file text. Null without an API key.
    /// </summary>
    public static string? ShutdownUrl(string iniText)
    {
        string? host = null, port = null, key = null;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)) host ??= value;
            else if (name.Equals("port", StringComparison.OrdinalIgnoreCase)) port ??= value;
            else if (name.Equals("api_key", StringComparison.OrdinalIgnoreCase)) key ??= value;
        }

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (string.IsNullOrEmpty(host) || host is "0.0.0.0" or "::" or "*")
        {
            host = "127.0.0.1";
        }

        return $"http://{host}:{(string.IsNullOrEmpty(port) ? "8080" : port)}/api?mode=shutdown&apikey={key}";
    }
}

internal sealed class RealSabnzbdEnvironment(string dataRoot, string iniPath, IServiceCommandRunner runner) : ISabnzbdServiceEnvironment
{
    // How long a running SABnzbd is given to finish what it is doing and exit before it is stopped by force.
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(90);

    private string MarkerPath => Path.Combine(dataRoot, "config", "sabnzbd-service.registered-by-deluno");

    public bool ServiceExists()
    {
        try
        {
            using var sc = new ServiceController(SabnzbdService.Name);
            _ = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? StartMode()
    {
        try
        {
            using var sc = new ServiceController(SabnzbdService.Name);
            return sc.StartType switch
            {
                ServiceStartMode.Automatic => "Auto",
                ServiceStartMode.Manual => "Manual",
                ServiceStartMode.Disabled => "Disabled",
                _ => sc.StartType.ToString()
            };
        }
        catch
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool InSessionZero()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId == 0;
    }

    public bool MarkerExists() => File.Exists(MarkerPath);

    public void WriteMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, $"Deluno registered the SABnzbd Windows service on {DateTimeOffset.Now:O}.{Environment.NewLine}");
    }

    public void DeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (IOException)
        {
            // A marker left behind only makes the next removal look for a service that is not there.
        }
    }

    public bool SabnzbdRunning() => ProcessesRunning() || PortOpen();

    public bool AskToShutDown()
    {
        try
        {
            var url = SabnzbdProcessControl.ShutdownUrl(File.ReadAllText(iniPath));
            if (url is null)
            {
                return false;
            }

            using var http = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
            return http.GetAsync(url).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public bool WaitUntilStopped()
    {
        var deadline = DateTime.UtcNow + ShutdownWait;
        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessesRunning() && !PortOpen())
            {
                return true;
            }

            Thread.Sleep(500); // reason: polling a process and a port; Windows raises no event for a port closing
        }

        return false;
    }

    public void ForceStop() => runner.Run("taskkill.exe", "/F /IM SABnzbd.exe /IM SABnzbd-console.exe");

    public void Log(string message) => HookLog.Write(message);

    private static bool ProcessesRunning()
    {
        foreach (var name in new[] { "SABnzbd", "SABnzbd-console" })
        {
            var found = Process.GetProcessesByName(name);
            var any = found.Length > 0;
            foreach (var p in found)
            {
                p.Dispose();
            }

            if (any)
            {
                return true;
            }
        }

        return false;
    }

    private static bool PortOpen()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 8080).Observed().Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
