using Deluno.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Pacing outbound requests before they are sent.
///
/// These run on a fake clock, so they assert the shape of the pacing rather
/// than waiting real seconds — which also means they assert the thing that
/// matters: that the second request to a host is not allowed until the interval
/// has actually elapsed.
/// </summary>
public sealed class OutboundRequestThrottleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-20T00:00:00Z");

    [Fact]
    public async Task The_first_request_to_a_host_is_not_delayed()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);

        var waited = await throttle.TryAcquireAsync(
            "indexer.example", OutboundRate.PerIndexerDefault, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, waited);
    }

    [Fact]
    public async Task A_second_request_to_the_same_host_waits_out_the_interval()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.PerIndexerDefault;

        await throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);

        var second = throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(second.IsCompleted, "The second request should be waiting, not allowed straight through.");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted, "One second is not two.");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), await second);
    }

    [Fact]
    public async Task Different_hosts_do_not_wait_for_each_other()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.PerIndexerDefault;

        await throttle.TryAcquireAsync("one.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Sixteen indexers in flight at once is fine. Sixteen requests to one
        // indexer is the thing that gets an account flagged.
        var other = await throttle.TryAcquireAsync("two.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, other);
    }

    [Fact]
    public async Task A_host_left_alone_does_not_bank_permits_it_can_spend_all_at_once()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.PerIndexerDefault;

        await throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);

        // An hour of silence must not buy 1,800 requests.
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.Zero, await throttle.TryAcquireAsync(
            "indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None));

        var immediatelyAfter = throttle.TryAcquireAsync(
            "indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(immediatelyAfter.IsCompleted, "The interval still applies after a quiet period.");

        time.Advance(TimeSpan.FromSeconds(2));
        await immediatelyAfter;
    }

    [Fact]
    public async Task A_budget_allows_a_burst_and_then_paces()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.MetadataProviderDefault;

        // Ten a second with a burst of ten: the first ten go straight through,
        // which is what keeps a metadata backfill fast without letting it
        // become the 20,000-request burst that collected 394 rate-limit
        // responses.
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(TimeSpan.Zero, await throttle.TryAcquireAsync(
                "api.themoviedb.org", rate, TimeSpan.FromSeconds(30), CancellationToken.None));
        }

        var eleventh = throttle.TryAcquireAsync(
            "api.themoviedb.org", rate, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(eleventh.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await eleventh;
    }

    [Fact]
    public async Task A_caller_that_cannot_wait_long_enough_is_refused_rather_than_blocked()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.FromInterval(TimeSpan.FromMinutes(5));

        await throttle.TryAcquireAsync("slow.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);

        // A search job holds a two-minute lease. Sitting on it for five minutes
        // would get the job leased again by another worker and the request sent
        // twice — the opposite of throttling.
        var refused = await throttle.TryAcquireAsync(
            "slow.example", rate, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Null(refused);
    }

    [Fact]
    public async Task A_refusal_gives_its_slot_back_rather_than_burning_it()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.FromInterval(TimeSpan.FromSeconds(30));

        await throttle.TryAcquireAsync("host.example", rate, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Null(await throttle.TryAcquireAsync("host.example", rate, TimeSpan.FromSeconds(5), CancellationToken.None));

        // The refused caller must not have pushed the next slot further out for
        // everybody else; a queue of impatient callers would otherwise starve a
        // patient one indefinitely.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.Zero, await throttle.TryAcquireAsync(
            "host.example", rate, TimeSpan.FromSeconds(60), CancellationToken.None));
    }

    [Fact]
    public async Task What_is_being_held_back_is_visible()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.PerIndexerDefault;

        await throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Null(await throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromMilliseconds(1), CancellationToken.None));

        // A throttle nobody can see is indistinguishable from a hang.
        var state = Assert.Single(throttle.Describe());
        Assert.Equal("indexer.example", state.Host);
        Assert.Equal(1, state.GrantedCount);
        Assert.Equal(1, state.RefusedCount);
        Assert.True(state.NextPermitUtc > time.GetUtcNow());
    }

    [Fact]
    public async Task Concurrent_callers_are_paced_one_after_another_not_all_at_once()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = Create(time);
        var rate = OutboundRate.PerIndexerDefault;

        var calls = Enumerable.Range(0, 4)
            .Select(_ => throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(60), CancellationToken.None).AsTask())
            .ToArray();

        // Four callers computing "my slot is now" from the same instant is
        // exactly the burst this prevents, so only one may be through.
        //
        // Waited for by state rather than by duration. The pacing itself runs
        // on a fake clock and is exact; what is not exact is how soon this
        // machine gets round to running the continuations, and a fixed 50 ms
        // grace was quietly asserting that it would be prompt. On a loaded
        // runner it is not, and this failed having found nothing wrong.
        await SettledAsync(calls, expected: 1);
        Assert.Equal(1, calls.Count(call => call.IsCompletedSuccessfully));

        for (var index = 0; index < 4; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await SettledAsync(calls, expected: Math.Min(index + 2, calls.Length));
        }

        var waits = await Task.WhenAll(calls);
        Assert.All(waits, wait => Assert.NotNull(wait));
        Assert.Equal([0, 2, 4, 6], waits.Select(wait => (int)wait!.Value.TotalSeconds).Order().ToArray());
    }

    /// <summary>
    /// Waits until the expected number of callers have come through, or gives
    /// up after long enough that the machine is not the explanation.
    ///
    /// <para>The deadline is real time, but it is a ceiling rather than a
    /// measurement: a slow machine takes longer and still passes, and a throttle
    /// that never lets the caller through still fails.</para>
    /// </summary>
    private static async Task SettledAsync(Task<TimeSpan?>[] calls, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (calls.Count(call => call.IsCompletedSuccessfully) < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    private static OutboundRequestThrottle Create(TimeProvider time)
        => new(time, NullLogger<OutboundRequestThrottle>.Instance);
}
