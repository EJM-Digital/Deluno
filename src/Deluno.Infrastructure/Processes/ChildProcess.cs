using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Deluno.Infrastructure.Processes;

/// <param name="Started">False when the program could not be started at all (not found, not allowed).</param>
/// <param name="ExitCode">Null when the program did not start or did not finish within its time.</param>
public sealed record ChildProcessResult(bool Started, int? ExitCode, string Output, string Error, bool TimedOut);

/// <summary>
/// Runs a program and gives back what it printed, without ever leaving one of its pipes unread and never waiting
/// without a limit (#1258). A program whose output pipe is full and unread stops until somebody reads it, so every
/// stream is read as it arrives, to the end, whatever is kept; standard input is closed so a program that reads it sees
/// the end at once; and a program that does not finish in time is stopped with everything it started.
/// </summary>
public static class ChildProcess
{
    /// <summary>Most characters of each stream that are kept; the rest is read and thrown away so the pipe never fills.</summary>
    public const int MaxKeptCharacters = 1_000_000;

    /// <summary>After the program ends, how long to wait for the last of its output (a program it started can hold the pipe open).</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the program with no window and its output captured. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled (the program is stopped first); a program that outlasts
    /// <paramref name="timeout"/> is stopped and reported as timed out.
    /// </summary>
    public static async Task<ChildProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ChildProcessResult(false, null, string.Empty, exception.Message, false);
        }

        if (started is null)
        {
            return new ChildProcessResult(false, null, string.Empty, "The program did not start.", false);
        }

        using var process = started;
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // It already ended; its output is still read below.
        }

        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await Settle(output, error).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ChildProcessResult(true, null, await Kept(output), await Kept(error), true);
        }

        await Settle(output, error).ConfigureAwait(false);
        return new ChildProcessResult(true, process.ExitCode, await Kept(output), await Kept(error), false);
    }

    /// <summary>The same, for code that cannot be asynchronous. Runs on the thread pool so no caller's context is waited on.</summary>
    public static ChildProcessResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        => Task.Run(() => RunAsync(startInfo, timeout, CancellationToken.None)).GetAwaiter().GetResult();

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                var room = MaxKeptCharacters - kept.Length;
                if (room > 0)
                {
                    kept.Append(buffer, 0, Math.Min(read, room));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed under the read.
        }

        return kept.ToString();
    }

    private static async Task Settle(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(DrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A program it started still holds the pipe open; what was read so far is what there is.
        }
    }

    private static async Task<string> Kept(Task<string> drain)
        => drain.IsCompletedSuccessfully ? await drain.ConfigureAwait(false) : string.Empty;

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to stop.
        }
    }
}
