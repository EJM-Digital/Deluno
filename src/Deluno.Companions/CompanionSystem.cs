using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// Running in this Windows session: the copy the person at this desktop
    /// would have to close. A copy another account runs - a service, say - is
    /// not it, and asking them to close it could never succeed (seen on the rig).
    /// </summary>
    bool IsProcessRunning(string processName);

    /// <summary>Closes every copy of the program running in this Windows session (#1118: before its installer replaces it).</summary>
    void StopProcess(string processName);

    bool IsPortInUse(int port);

    /// <summary>
    /// The TCP ports listened on by processes running from under a folder: how
    /// a tool Deluno installed is told apart from another copy of it that is
    /// also running on this computer.
    /// </summary>
    IReadOnlyList<int> ListeningPortsOf(string folder);

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

    public bool IsProcessRunning(string processName)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(process => process.SessionId == current.SessionId);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void StopProcess(string processName)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.SessionId != current.SessionId)
                {
                    continue;
                }

                if (!process.CloseMainWindow() || !process.WaitForExit(5000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone, or not ours to close.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public bool IsPortInUse(int port)
        => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    public IReadOnlyList<int> ListeningPortsOf(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var ports = new SortedSet<int>();
        foreach (var (port, pid) in TcpListeners(AddressFamilyInet).Concat(TcpListeners(AddressFamilyInet6)))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    ports.Add(port);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Gone, or another account's process: not the one being looked for.
            }
        }

        return ports.ToArray();
    }

    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>Listening ports with the process that owns each (GetExtendedTcpTable).</summary>
    private static IEnumerable<(int Port, int Pid)> TcpListeners(int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
            {
                return [];
            }

            // IPv4 rows: state, local addr, local port, remote addr, remote port, pid (6 x 4 bytes).
            // IPv6 rows: local addr (16), scope (4), local port (4), remote addr (16), scope (4), remote port (4), state (4), pid (4).
            var rowSize = family == AddressFamilyInet ? 24 : 56;
            var portOffset = family == AddressFamilyInet ? 8 : 20;
            var pidOffset = family == AddressFamilyInet ? 20 : 52;
            var count = Marshal.ReadInt32(buffer);
            var rows = new List<(int, int)>(count);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * rowSize);
                var raw = Marshal.ReadInt32(row, portOffset);
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                rows.Add((port, Marshal.ReadInt32(row, pidOffset)));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, uint reserved);

    public async Task<CompanionRunResult> RunElevatedAsync(string path, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true, Verb = "runas" })
                ?? throw new InvalidOperationException($"{Path.GetFileName(path)} did not start.");
            return await WaitForInstallerAsync(process, cancellationToken);
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
        return await WaitForInstallerAsync(process, cancellationToken);
    }

    /// <summary>How long an installer is given. Long, since a vendor's installer can be slow, but never open-ended (#1258).</summary>
    private static readonly TimeSpan InstallerLimit = TimeSpan.FromMinutes(15);

    /// <summary>Waits for the installer; one that is still running after its limit is stopped and reported as failed (-1).</summary>
    private static async Task<CompanionRunResult> WaitForInstallerAsync(Process process, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(InstallerLimit);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return new CompanionRunResult(process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
            return new CompanionRunResult(-1, false);
        }
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
