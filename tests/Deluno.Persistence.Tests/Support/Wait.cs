namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// The waits tests use instead of fixed sleeps. Every wait names what it is waiting for, has a limit, and fails
/// with that name when the limit passes.
/// </summary>
internal static class Wait
{
    /// <summary>The limit for a wait on work that runs in-process: far longer than it ever takes, short enough that a hang fails the run.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        await UntilAsync(() => Task.FromResult(condition()), what, limit);
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? limit = null)
    {
        var deadline = DateTimeOffset.UtcNow + (limit ?? DefaultLimit);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Gave up waiting for: {what}.");
            }

            await Task.Delay(2); // reason: poll interval inside a bounded wait; the limit is on the line above
        }
    }

    /// <summary>
    /// Returns once the real clock has moved on past every timestamp taken so far, so the next one sorts strictly later.
    /// The code under test stamps with <see cref="DateTimeOffset.UtcNow"/>, so ordered timestamps need the real clock to
    /// advance. It waits on the clock itself rather than sleeping a guessed time; 16 ms is a little over the coarsest tick
    /// a Windows clock has (15.6 ms), and the 5 s limit is only there so a stopped clock fails the test.
    /// </summary>
    public static async Task ClockMovesOnAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var spin = new SpinWait();
        while (DateTimeOffset.UtcNow - start < TimeSpan.FromMilliseconds(16))
        {
            if (DateTimeOffset.UtcNow - start > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The clock did not move on.");
            }

            spin.SpinOnce(sleep1Threshold: -1);
            await Task.Yield();
        }
    }
}
