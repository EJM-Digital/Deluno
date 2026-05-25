using System.Collections.Concurrent;

namespace Deluno.Downloader.Nzb.Nntp;

/// <summary>
/// Bounded NNTP connection pool, one per server. Connections are created
/// lazily up to <see cref="NntpServerOptions.MaxConnections"/> and reused.
///
/// Production-grade behaviour added on top of the spike version:
/// <list type="bullet">
///   <item><description>Connections older than
///     <see cref="NntpConnection.DefaultMaxConnectionAge"/> are not reused
///     (provider cert rotation + dead-connection problem).</description></item>
///   <item><description>Connections idle longer than
///     <see cref="NntpConnection.DefaultIdleHealthCheckThreshold"/> get a
///     <c>DATE</c> health check on borrow — broken sockets are discarded
///     and replaced.</description></item>
///   <item><description>Borrowed connections that throw mark themselves bad
///     so they don't go back in the pool.</description></item>
/// </list>
/// </summary>
public sealed class NntpConnectionPool : IAsyncDisposable
{
    private readonly NntpServerOptions _options;
    private readonly ConcurrentBag<NntpConnection> _idle = new();
    private readonly SemaphoreSlim _slots;
    private bool _disposed;

    public NntpServerOptions Options => _options;

    public NntpConnectionPool(NntpServerOptions options)
    {
        _options = options;
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    public async Task<PooledNntpConnection> RentAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            while (_idle.TryTake(out var existing))
            {
                if (existing.Age >= NntpConnection.DefaultMaxConnectionAge)
                {
                    await SafeDispose(existing).ConfigureAwait(false);
                    continue;
                }
                if (existing.IdleDuration >= NntpConnection.DefaultIdleHealthCheckThreshold)
                {
                    if (!await existing.HealthCheckAsync(ct).ConfigureAwait(false))
                    {
                        await SafeDispose(existing).ConfigureAwait(false);
                        continue;
                    }
                }
                return new PooledNntpConnection(this, existing);
            }

            var conn = await NntpConnection.ConnectAsync(_options, ct).ConfigureAwait(false);
            return new PooledNntpConnection(this, conn);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    internal void Return(NntpConnection conn, bool discard)
    {
        if (_disposed || discard || conn.Age >= NntpConnection.DefaultMaxConnectionAge)
        {
            _ = SafeDispose(conn);
        }
        else
        {
            _idle.Add(conn);
        }
        _slots.Release();
    }

    private static async Task SafeDispose(NntpConnection conn)
    {
        try { await conn.DisposeAsync().ConfigureAwait(false); }
        catch { /* swallow */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        while (_idle.TryTake(out var conn))
            await SafeDispose(conn).ConfigureAwait(false);
        _slots.Dispose();
    }
}

public sealed class PooledNntpConnection : IAsyncDisposable
{
    private readonly NntpConnectionPool _pool;
    public NntpConnection Connection { get; }
    public bool MarkBad { get; set; }

    internal PooledNntpConnection(NntpConnectionPool pool, NntpConnection conn)
    {
        _pool = pool;
        Connection = conn;
    }

    public ValueTask DisposeAsync()
    {
        _pool.Return(Connection, MarkBad);
        return ValueTask.CompletedTask;
    }
}
