using System.Diagnostics;

namespace Deluno.Infrastructure.Processes;

/// <param name="Started">False when the program could not be started at all (not found, not allowed).</param>
/// <param name="ExitCode">Null when the program did not start or did not finish within its time.</param>
public sealed record ChildProcessResult(bool Started, int? ExitCode, string Output, string Error, bool TimedOut);

/// <summary>
/// Runs a program and gives back what it printed, without ever leaving one of its pipes unread and never waiting
/// without a limit (#1258). A program whose output pipe is full and unread stops until somebody reads it, so every
/// redirected stream is drained as it arrives; a program that does not finish in time is stopped with everything it started.
/// </summary>
public static class ChildProcess
{
    /// <summary>Most characters of each stream that are kept; the rest is read and thrown away so the pipe never fills.</summary>
    public const int MaxKeptCharacters = 1_000_000;

    public static Task<ChildProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public static ChildProcessResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        => throw new NotImplementedException();
}
