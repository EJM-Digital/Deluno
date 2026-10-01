namespace Deluno.Worker.SystemReadings;

/// <summary>
/// A reading that is expensive or slow (a WMI query, a name lookup, a folder size) and does not change
/// every two seconds. It keeps the last answer, and when that is older than its life it starts one
/// refresh in the background and carries on serving the last answer, so a request or a push never waits
/// on it. Only the very first read waits, and only up to a short limit.
/// </summary>
public sealed class SlowValue<T>(
    TimeSpan life,
    Func<CancellationToken, Task<T>> load,
    TimeProvider timeProvider,
    T whenUnknown)
{
    private readonly object _gate = new();
    private T _value = whenUnknown;
    private bool _loaded;
    private DateTimeOffset _attemptedAt;
    private bool _attempted;
    private Task? _refresh;

    /// <summary>Whether a reading has ever succeeded.</summary>
    public bool HasValue
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>The last reading, or <c>whenUnknown</c> before there is one. Starts a refresh when it is stale.</summary>
    public T Current
    {
        get
        {
            StartRefreshIfStale();
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Like <see cref="Current"/>, but the very first reading is waited for, up to <paramref name="firstReadLimit"/>.</summary>
    public async Task<T> GetAsync(TimeSpan firstReadLimit, CancellationToken cancellationToken)
    {
        var refresh = StartRefreshIfStale();
        bool loaded;
        lock (_gate)
        {
            loaded = _loaded;
        }

        if (!loaded && refresh is not null)
        {
            try
            {
                await refresh.WaitAsync(firstReadLimit, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }

        lock (_gate)
        {
            return _value;
        }
    }

    private Task? StartRefreshIfStale()
    {
        lock (_gate)
        {
            if (_refresh is not null)
            {
                return _refresh;
            }

            if (_attempted && timeProvider.GetUtcNow() - _attemptedAt < life)
            {
                return null;
            }

            return _refresh = Task.Run(async () =>
            {
                try
                {
                    var value = await load(CancellationToken.None);
                    lock (_gate)
                    {
                        _value = value;
                        _loaded = true;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failed reading keeps the last good one, and is tried again at the next ask.
                }
                finally
                {
                    lock (_gate)
                    {
                        _refresh = null;
                        _attempted = true;
                        _attemptedAt = timeProvider.GetUtcNow();
                    }
                }
            });
        }
    }
}
