using System.Collections.Concurrent;

namespace Deluno.Platform.Contracts;

/// <summary>How a send the person asked for (Try again) ended (#1253).</summary>
public enum ProcessorHandoffSendKind
{
    /// <summary>The processor took it. The hand-off now reads submitted.</summary>
    Sent,

    /// <summary>The send did not go: Deluno's own checks or the processor refused it. The hand-off says why.</summary>
    Refused,

    /// <summary>Another send of the same hand-off is under way or already done, so this one did nothing.</summary>
    AlreadySending,

    /// <summary>Nothing was wrong, but it is not sent yet (the processor is full). The planner sends it when there is room.</summary>
    Waiting
}

public sealed record ProcessorHandoffSendResult(ProcessorHandoffSendKind Kind, string Message);

/// <summary>
/// Sends one hand-off to its processor now, with the same rules and checks as the planner's send (#1253). The planner
/// and this share <see cref="ProcessorHandoffSends"/>, so a hand-off is never sent by both.
/// </summary>
public interface IProcessorHandoffSender
{
    Task<ProcessorHandoffSendResult> SendNowAsync(string handoffId, CancellationToken cancellationToken);
}

/// <summary>
/// Which hand-offs are being sent right now. Whoever sends a hand-off (the planner's pass, or Try again) holds its lease
/// for the whole send and, once it has it, reads the hand-off again: a hand-off that has already moved on is not sent.
/// </summary>
public static class ProcessorHandoffSends
{
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The lease on sending this hand-off, or null when another send holds it.</summary>
    public static IDisposable? TryBegin(string handoffId)
        => Active.TryAdd(handoffId, 0) ? new Lease(handoffId) : null;

    public static bool IsSending(string handoffId) => Active.ContainsKey(handoffId);

    private sealed class Lease(string handoffId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Active.TryRemove(handoffId, out _);
            }
        }
    }
}
