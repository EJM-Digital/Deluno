using System.Diagnostics;
using System.ServiceProcess;

namespace Deluno.Tray;

/// <summary>Runs a program elevated, for the service commands (the tray's way of doing what an administrator would do in a console).</summary>
public interface IServiceCommandRunner
{
    void Run(string fileName, string arguments);
}

public sealed class ElevatedCommandRunner : IServiceCommandRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public void Run(string fileName, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(Limit);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Declined or not there: the caller's next look at the service says what happened.
        }
    }
}

/// <summary>
/// SABnzbd run as its own Windows service while Deluno is a Windows service (#1452). SABnzbd stops at once in session 0 when started as a plain
/// process (its own Windows-service check, whatever the arguments), so a restart with nobody signed in could not bring it back. Its own service
/// mode does: <c>SABnzbd-console.exe -f &lt;ini&gt; install</c> (run from a signed-in session), then start type automatic. It reads the person's
/// settings file named at install, so the API key, folders and categories the install set up are the ones it uses.
/// </summary>
public sealed class SabnzbdService(IServiceCommandRunner runner, Func<bool> isRegistered, Func<string, bool> fileExists, string installDir, string iniPath)
{
    public const string Name = "SABnzbd";

    public bool IsRegistered() => isRegistered();

    /// <summary>Registers and starts the service. False (and nothing run) when SABnzbd is not installed.</summary>
    public bool Register()
    {
        var console = installDir.TrimEnd('\\') + @"\SABnzbd-console.exe";
        if (!fileExists(console))
        {
            return false;
        }

        if (isRegistered())
        {
            Remove();
        }

        // A copy the tray started holds port 8080; the service takes it over.
        runner.Run("taskkill.exe", "/F /IM SABnzbd.exe /IM SABnzbd-console.exe");
        runner.Run(console, $"-f \"{iniPath}\" install");
        runner.Run("sc.exe", $"config {Name} start= auto");
        runner.Run("sc.exe", $"start {Name}");
        return true;
    }

    /// <summary>Stops and deletes the service. False (and nothing run) when it is not there.</summary>
    public bool Remove()
    {
        if (!isRegistered())
        {
            return false;
        }

        runner.Run("sc.exe", $"stop {Name}");
        runner.Run("sc.exe", $"delete {Name}");
        return true;
    }

    public static string IniPathFor(string dataRoot)
    {
        var local = ServiceProcessStarter.ProfileFoldersFor(dataRoot)?.LocalAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local + @"\sabnzbd\sabnzbd.ini";
    }

    public static SabnzbdService ForThisComputer(string? dataRoot = null)
        => new(
            new ElevatedCommandRunner(),
            ServiceExists,
            File.Exists,
            ComponentPaths.Expand(@"%ProgramFiles%\SABnzbd"),
            IniPathFor(dataRoot ?? AppSettings.Load().DataRoot));

    private static bool ServiceExists()
    {
        try
        {
            using var sc = new ServiceController(Name);
            _ = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
