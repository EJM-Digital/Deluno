using Deluno.Api.Monitoring;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Monitoring;

/// <summary>
/// How a library's folder is checked off the request (#1081): a folder that is there or missing is told at once on a real
/// temp folder; a folder that never answers (a share whose NAS is off) is reachable for the first five seconds and
/// unreachable after, the first look waits only a quarter of a second, and the folder is asked again after thirty seconds.
/// </summary>
public sealed class LibraryRootReachabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-reach-" + Guid.NewGuid().ToString("N"));

    public LibraryRootReachabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_real_folder_that_is_there_is_reachable_and_one_that_is_missing_is_not_at_the_first_look()
    {
        var reach = new LibraryRootReachability(new FakeTimeProvider());
        var missing = Path.Combine(_root, "not-there");

        var unreachable = await reach.UnreachableAsync([_root, missing], CancellationToken.None);

        Assert.DoesNotContain(_root, unreachable);
        Assert.Contains(missing, unreachable);
    }

    [Fact]
    public async Task A_folder_that_never_answers_is_not_waited_for_past_a_quarter_of_a_second_and_is_unreachable_after_five_seconds()
    {
        var clock = new FakeTimeProvider();
        using var neverAnswers = new ManualResetEventSlim(false);
        var reach = new LibraryRootReachability(clock, _ => { neverAnswers.Wait(); return true; });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var first = await reach.UnreachableAsync(["//dead-nas/media"], CancellationToken.None);

        // The first look gave the check a quarter of a second and moved on: it did not hold the request for the share.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the first look took {watch.Elapsed}");
        Assert.Empty(first);

        clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Empty(await reach.UnreachableAsync(["//dead-nas/media"], CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(0.2));
        var later = await reach.UnreachableAsync(["//dead-nas/media"], CancellationToken.None);
        Assert.Contains("//dead-nas/media", later);

        neverAnswers.Set();
    }

    [Fact]
    public async Task A_folder_that_was_checked_is_checked_again_only_after_thirty_seconds()
    {
        var clock = new FakeTimeProvider();
        var asked = 0;
        var there = true;
        var reach = new LibraryRootReachability(clock, _ => { Interlocked.Increment(ref asked); return there; });

        Assert.Empty(await reach.UnreachableAsync(["/media"], CancellationToken.None));
        await WaitForAsync(() => Volatile.Read(ref asked) >= 1);

        // The folder goes away, but the answer is kept for thirty seconds.
        there = false;
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(await reach.UnreachableAsync(["/media"], CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref asked));

        // After thirty seconds it is asked again, in the background, and the next look reads that answer.
        clock.Advance(TimeSpan.FromSeconds(2));
        await reach.UnreachableAsync(["/media"], CancellationToken.None);
        await WaitForAsync(() => Volatile.Read(ref asked) >= 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("/media", await reach.UnreachableAsync(["/media"], CancellationToken.None));
    }

    // The check runs on a worker thread; the wait is bounded and names what it waits for.
    private static async Task WaitForAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10); // reason: bounds a wait for a background probe to start
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "the background folder check did not run");
            await Task.Delay(20); // reason: poll interval inside a loop bounded by its deadline
        }
    }
}
