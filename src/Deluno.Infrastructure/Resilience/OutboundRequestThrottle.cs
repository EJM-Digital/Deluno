using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Deluno.Infrastructure.Resilience;

/// <summary>
/// Paces outbound requests to a remote host <em>before</em> they are sent.
///
/// Deluno already backs off after an indexer rate-limits it. That is too late:
/// being rate-limited by a private tracker is itself the damage, and repeatedly
/// tripping it is how an account gets flagged. This is the other half — a hard
/// floor on how often Deluno may talk to one host, applied on the way out.
///
/// A token bucket rather than a fixed sleep, because the two things being
/// protected want different shapes and this expresses both:
///
/// <list type="bullet">
/// <item>An indexer wants a <em>minimum interval</em> — one request every two
/// seconds, no bursting. That is a bucket of one token refilled every two
/// seconds.</item>
/// <item>A metadata provider wants a <em>budget</em> — TMDB permits roughly
/// fifty requests a second and a burst is harmless, but twenty thousand
/// unpaced lookups is not. That is a bucket of ten tokens refilled ten a
/// second.</item>
/// </list>
///
/// State is per process and in memory on purpose. It is a pacing decision that
/// only matters for requests this process is about to make, and persisting it
/// would put a write on the outbound path — which is exactly the contention
/// this is meant to reduce.
/// </summary>
public interface IOutboundRequestThrottle
{
    /// <summary>
    /// Waits for permission to call <paramref name="host"/>.
    ///
    /// Returns how long the caller waited, or <c>null</c> if permission did not
    /// arrive within <paramref name="maxWait"/>. A refusal is never silent: the
    /// caller is expected to report the host it skipped, because a working
    /// throttle and a hung one look identical from the outside.
    /// </summary>
    ValueTask<TimeSpan?> TryAcquireAsync(
        string host,
        OutboundRate rate,
        TimeSpan maxWait,
        CancellationToken cancellationToken);

    /// <summary>
    /// What every paced host is doing right now — the shape a UI needs to say
    /// "waiting on X for another 4 seconds" instead of showing nothing.
    /// </summary>
    IReadOnlyList<OutboundThrottleHostState> Describe();
}

/// <param name="Permits">Requests allowed per <paramref name="Per"/>.</param>
/// <param name="Per">The window those permits refill over.</param>
/// <param name="Burst">
/// How many permits may accumulate. One means strict pacing with no bursting,
/// which is what an indexer wants.
/// </param>
public sealed record OutboundRate(int Permits, TimeSpan Per, int Burst)
{
    /// <summary>
    /// One request every two seconds, no bursting.
    ///
    /// Two seconds is not invented: it is the hard floor Prowlarr enforces for
    /// its per-indexer request delay, below which a user cannot configure an
    /// indexer however much they want to. Matching the reference implementation
    /// of this ecosystem is more defensible than a number chosen here.
    /// </summary>
    public static OutboundRate PerIndexerDefault { get; } = new(1, TimeSpan.FromSeconds(2), 1);

    /// <summary>
    /// Ten requests a second with a burst of ten.
    ///
    /// TMDB's documented ceiling is around fifty requests a second per IP; this
    /// sits five times under it. It is not arbitrary caution — a 20,500-title
    /// backfill run during this work sent about 20,000 requests and took 394
    /// rate-limit responses, with nothing pacing it. At ten a second the same
    /// backfill drains in about half an hour and stays well inside the limit.
    /// </summary>
    public static OutboundRate MetadataProviderDefault { get; } = new(10, TimeSpan.FromSeconds(1), 10);

    public static OutboundRate FromInterval(TimeSpan interval)
        => new(1, interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval, 1);

    internal TimeSpan RefillPeriod => Permits <= 0
        ? TimeSpan.FromSeconds(2)
        : TimeSpan.FromTicks(Math.Max(1, Per.Ticks / Permits));
}

public sealed record OutboundThrottleHostState(
    string Host,
    int Waiting,
    DateTimeOffset NextPermitUtc,
    long GrantedCount,
    long RefusedCount,
    TimeSpan TotalWaited);

public sealed class OutboundRequestThrottle(
    TimeProvider timeProvider,
    ILogger<OutboundRequestThrottle> logger)
    : IOutboundRequestThrottle
{
    private readonly ConcurrentDictionary<string, HostBucket> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<TimeSpan?> TryAcquireAsync(
        string host,
        OutboundRate rate,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return TimeSpan.Zero;
        }

        var bucket = _hosts.GetOrAdd(host.Trim(), key => new HostBucket(key));
        var startedUtc = timeProvider.GetUtcNow();

        // One waiter at a time per host. Without it, ten callers would each
        // compute "the next slot is in 2s" from the same starting point and all
        // fire together — which is the burst this exists to prevent.
        Interlocked.Increment(ref bucket.Waiting);
        try
        {
            if (!await bucket.Gate.WaitAsync(maxWait, cancellationToken))
            {
                Interlocked.Increment(ref bucket.Refused);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref bucket.Waiting);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref bucket.Waiting);
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var delay = bucket.Reserve(now, rate);
            var remaining = maxWait - (now - startedUtc);

            if (delay > remaining)
            {
                // Handing the reservation back rather than sleeping past the
                // caller's budget; the job that wanted this can be retried or
                // reported, but it must not sit on a lease it will lose.
                bucket.Release(now, rate);
                Interlocked.Increment(ref bucket.Refused);

                logger.LogDebug(
                    "Outbound throttle refused {Host}: next permit in {Delay}, caller could wait {Remaining}.",
                    host,
                    delay,
                    remaining);

                return null;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }

            Interlocked.Increment(ref bucket.Granted);
            var waited = timeProvider.GetUtcNow() - startedUtc;
            Interlocked.Add(ref bucket.TotalWaitedTicks, waited.Ticks);
            return waited;
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    public IReadOnlyList<OutboundThrottleHostState> Describe()
        => _hosts.Values
            .Select(bucket => bucket.Describe())
            .OrderBy(state => state.Host, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// One host's bucket. <see cref="NextPermitUtc"/> walks forward as permits
    /// are taken; a caller that arrives after it has passed waits for nothing.
    /// </summary>
    private sealed class HostBucket(string host)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Waiting;
        public long Granted;
        public long Refused;
        public long TotalWaitedTicks;

        private DateTimeOffset _nextPermitUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _previousPermitUtc = DateTimeOffset.MinValue;

        public TimeSpan Reserve(DateTimeOffset now, OutboundRate rate)
        {
            _previousPermitUtc = _nextPermitUtc;

            // Anything older than the burst window is forfeited rather than
            // banked, so a host left alone overnight does not earn the right to
            // a thousand requests at once.
            var earliest = now - TimeSpan.FromTicks(rate.RefillPeriod.Ticks * Math.Max(0, rate.Burst - 1));
            if (_nextPermitUtc < earliest)
            {
                _nextPermitUtc = earliest;
            }

            var permitUtc = _nextPermitUtc;
            _nextPermitUtc = permitUtc + rate.RefillPeriod;

            return permitUtc <= now ? TimeSpan.Zero : permitUtc - now;
        }

        public void Release(DateTimeOffset now, OutboundRate rate) => _nextPermitUtc = _previousPermitUtc;

        public OutboundThrottleHostState Describe()
            => new(
                host,
                Volatile.Read(ref Waiting),
                _nextPermitUtc,
                Interlocked.Read(ref Granted),
                Interlocked.Read(ref Refused),
                TimeSpan.FromTicks(Interlocked.Read(ref TotalWaitedTicks)));
    }
}
