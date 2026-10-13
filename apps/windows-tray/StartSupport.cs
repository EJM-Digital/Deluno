using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Deluno.Tray;

/// <summary>The real host the tray supervises: the in-process web server.</summary>
internal sealed class ServerHost(DelunoServer server) : IStartableHost
{
    public Task StartAsync(CancellationToken cancellationToken) => server.StartAsync(cancellationToken);

    public Task StopAsync() => server.StopAsync();

    public Task FreeAsync(CancellationToken cancellationToken) => server.FreeAsync(cancellationToken);
}

/// <summary>The supervisor's log: tray-startup.log, next to the other start-up logs.</summary>
internal sealed class TrayStartLog : ISupervisorLog
{
    public const string FileName = "tray-startup.log";

    public void Info(string message) => HookLog.Write("[info] " + message, FileName);

    public void Warning(string message) => HookLog.Write("[warning] " + message, FileName);

    public void Error(string message) => HookLog.Write("[error] " + message, FileName);
}

internal sealed class RealProcessLauncher : IProcessLauncher
{
    public void Launch(string exePath, string arguments)
    {
        // Not a shell start: no window, no browser, nothing on the person's screen but the tray icon.
        using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        });
    }
}

/// <summary>
/// What can be learned about a start that is stuck: the process's size and every thread's state and wait reason. The
/// managed stack of another thread cannot be read from inside the process, so this is the closest thing to a stack: a
/// thread waiting on a lock or a file shows as such, and one that is busy shows its processor time.
/// </summary>
public static class StartDiagnostics
{
    public static string Capture()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"process {process.Id} has run {(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds:0} s, used {process.TotalProcessorTime.TotalSeconds:0.#} s of processor time, holds {process.Threads.Count} threads and {process.WorkingSet64 / (1024 * 1024)} MB; ");
            text.Append(CultureInfo.InvariantCulture, $"thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} waiting jobs; ");
            var waiting = new List<string>();
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    var state = thread.ThreadState == System.Diagnostics.ThreadState.Wait ? $"waiting ({thread.WaitReason})" : thread.ThreadState.ToString();
                    waiting.Add($"{thread.Id}: {state}, {thread.TotalProcessorTime.TotalSeconds:0.#} s");
                }
                catch
                {
                    // A thread that ended while it was being looked at.
                }
            }

            text.Append("threads: ").Append(string.Join("; ", waiting.Take(40)));
            if (waiting.Count > 40)
            {
                text.Append(CultureInfo.InvariantCulture, $"; and {waiting.Count - 40} more");
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            return $"the process could not be looked at ({ex.Message})";
        }
    }
}
