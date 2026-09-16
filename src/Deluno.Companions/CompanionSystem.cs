using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Deluno.Companions;

/// <summary>
/// Everything a companion install does to the computer, in one place, so the
/// steps can be tested without installing anything.
/// </summary>
public interface ICompanionSystem
{
    string ProgramFiles { get; }

    string RoamingAppData { get; }

    string LocalAppData { get; }

    bool FileExists(string path);

    string? ReadText(string path);

    void WriteText(string path, string contents);

    bool IsProcessRunning(string processName);

    bool IsPortInUse(int port);

    /// <summary>
    /// Runs a vendor installer that needs administrator rights. Windows asks
    /// the person at this computer; a refusal comes back as
    /// <see cref="CompanionRunResult.Declined"/>, not as a failure to explain.
    /// </summary>
    Task<CompanionRunResult> RunElevatedAsync(string path, string arguments, CancellationToken cancellationToken);

    Task<CompanionRunResult> RunAsync(string path, string arguments, CancellationToken cancellationToken);

    void Start(string path, string arguments);

    /// <summary>Starts the tool when this Windows account signs in, as its own installer's option would.</summary>
    void StartAtSignIn(string name, string command);
}

public sealed record CompanionRunResult(int ExitCode, bool Declined);

public sealed class WindowsCompanionSystem : ICompanionSystem
{
    private const int ErrorCancelled = 1223;

    public string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public bool IsProcessRunning(string processName) => Process.GetProcessesByName(processName).Length > 0;

    public bool IsPortInUse(int port)
        => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    public async Task<CompanionRunResult> RunElevatedAsync(string path, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true, Verb = "runas" })
                ?? throw new InvalidOperationException($"{Path.GetFileName(path)} did not start.");
            await process.WaitForExitAsync(cancellationToken);
            return new CompanionRunResult(process.ExitCode, false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new CompanionRunResult(ErrorCancelled, true);
        }
    }

    public async Task<CompanionRunResult> RunAsync(string path, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException($"{Path.GetFileName(path)} did not start.");
        await process.WaitForExitAsync(cancellationToken);
        return new CompanionRunResult(process.ExitCode, false);
    }

    public void Start(string path, string arguments)
        => Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose();

    public void StartAtSignIn(string name, string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.SetValue(name, command);
    }
}
