using Deluno.Worker.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Worker.Tests.SystemReadings;

/// <summary>
/// A slow reading (WMI, a name lookup, a folder size) is served from its last answer and refreshed in the background once
/// it is older than its life; only the very first read waits, and only up to its limit. Each test holds the load on a
/// gate it opens itself, so nothing depends on how fast the machine is.
/// </summary>
public sealed class SlowValueTests
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task The_first_read_waits_for_the_reading_and_says_it_has_a_value()
    {
        var time = new FakeTimeProvider();
        var value = new SlowValue<int>(Life, _ => Task.FromResult(42), time, -1);

        Assert.False(value.HasValue);
        Assert.Equal(42, await value.GetAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.True(value.HasValue);
    }

    [Fact]
    public async Task A_first_read_that_takes_longer_than_its_limit_answers_unknown_and_the_reading_still_lands()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = new SlowValue<int>(Life, _ => gate.Task, time, -1);

        Assert.Equal(-1, await value.GetAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None));

        gate.SetResult(7);
        await Wait.UntilAsync(() => value.HasValue);
        Assert.Equal(7, value.Current);
    }

    [Fact]
    public async Task Asks_while_a_reading_is_running_share_it_instead_of_starting_another()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var value = new SlowValue<int>(Life, _ => { Interlocked.Increment(ref loads); return gate.Task; }, time, -1);

        Assert.Equal(-1, value.Current);
        Assert.Equal(-1, value.Current);
        var waiting = value.GetAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        gate.SetResult(3);

        Assert.Equal(3, await waiting);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task A_fresh_reading_is_served_without_loading_again_and_a_stale_one_is_served_while_it_refreshes()
    {
        var time = new FakeTimeProvider();
        var next = 1;
        var loads = 0;
        var value = new SlowValue<int>(Life, _ => { Interlocked.Increment(ref loads); return Task.FromResult(next); }, time, -1);
        Assert.Equal(1, await value.GetAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        await Wait.UntilAsync(() => Volatile.Read(ref loads) == 1 && value.Current == 1);

        time.Advance(Life - TimeSpan.FromSeconds(1));
        Assert.Equal(1, value.Current);
        Assert.Equal(1, loads);

        next = 2;
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, value.Current);
        await Wait.UntilAsync(() => value.Current == 2);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task A_failed_reading_keeps_the_last_good_one_and_is_tried_again_once_its_life_is_up()
    {
        var time = new FakeTimeProvider();
        var fail = false;
        var loads = 0;
        var value = new SlowValue<string>(Life, _ =>
        {
            Interlocked.Increment(ref loads);
            return fail ? Task.FromException<string>(new IOException("the share went away")) : Task.FromResult("good");
        }, time, "unknown");
        Assert.Equal("good", await value.GetAsync(TimeSpan.FromSeconds(30), CancellationToken.None));

        fail = true;
        time.Advance(Life + TimeSpan.FromSeconds(1));
        Assert.Equal("good", value.Current);
        await Wait.UntilAsync(() => Volatile.Read(ref loads) == 2);
        Assert.Equal("good", value.Current);
        Assert.True(value.HasValue);
    }

    private static class Wait
    {
        public static async Task UntilAsync(Func<bool> condition)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!condition())
            {
                await Task.Yield();
                limit.Token.ThrowIfCancellationRequested();
                await Task.Delay(1, limit.Token); // reason: a bounded poll for the thread-pool refresh, ended by the condition
            }
        }
    }
}
