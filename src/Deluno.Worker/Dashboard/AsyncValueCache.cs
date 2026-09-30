namespace Deluno.Worker.Dashboard;

/// <summary>
/// One value, remembered for a short while, rebuilt by one caller at a time.
///
/// <para>Many screens ask the Dashboard for its summary at once and every one
/// of them used to pay for the whole build. Here the first caller after the
/// value expires builds it and everyone who arrives meanwhile awaits that same
/// build (single flight), so a burst of requests costs one build.</para>
///
/// <para>With <c>serveStale</c> a stale value is returned immediately and the
/// rebuild happens in the background; without it, callers wait for a fresh
/// value. The summary itself waits (a reading that predates the change the
/// person just made would be wrong on screen); slow, rarely-changing sources
/// such as dispatch totals serve stale.</para>
/// </summary>
public sealed class AsyncValueCache<T>(TimeSpan timeToLive, TimeProvider timeProvider, bool serveStale = false)
    where T : class
{
    private readonly object _gate = new();
    private T? _value;
    private DateTimeOffset _loadedUtc;
    private Task<T>? _flight;

    /// <summary>How many builds have started; for tests and diagnostics.</summary>
    public int Builds { get; private set; }

    public async Task<T> GetAsync(Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        Task<T> pending;
        lock (_gate)
        {
            var fresh = _value is not null && timeProvider.GetUtcNow() - _loadedUtc < timeToLive;
            if (fresh)
            {
                return _value!;
            }

            _flight ??= StartFlight(factory);
            if (_value is not null && serveStale)
            {
                return _value;
            }

            pending = _flight;
        }

        return await pending.WaitAsync(cancellationToken);
    }

    /// <summary>The value as last built, however old, or null before the first build.</summary>
    public T? Peek()
    {
        lock (_gate)
        {
            return _value;
        }
    }

    /// <summary>Forget the value so the next caller rebuilds it.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _value = null;
        }
    }

    private Task<T> StartFlight(Func<CancellationToken, Task<T>> factory)
    {
        Builds++;
        // Not the caller's token: the build is shared, so one caller giving up
        // must not cancel it for the others.
        var flight = Task.Run(async () =>
        {
            try
            {
                var built = await factory(CancellationToken.None);
                lock (_gate)
                {
                    _value = built;
                    _loadedUtc = timeProvider.GetUtcNow();
                }

                return built;
            }
            finally
            {
                lock (_gate)
                {
                    _flight = null;
                }
            }
        });

        // A stale-serving background rebuild that fails has no awaiter to hear
        // about it; observe it so it is not an unobserved exception.
        _ = flight.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return flight;
    }
}
