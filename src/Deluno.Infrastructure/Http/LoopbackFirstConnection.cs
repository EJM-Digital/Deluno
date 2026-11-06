using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Deluno.Infrastructure.Http;

/// <summary>
/// The one way Deluno opens an HTTP connection (#1178).
///
/// Download clients, processors and companions are usually saved as <c>localhost</c>, and on Windows
/// <c>localhost</c> is tried as IPv6 <c>::1</c> first. A program that listens on IPv4 only refuses that,
/// and Windows retries a refused connection for about two seconds before trying <c>127.0.0.1</c>. Every new
/// connection paid those two seconds, and one pass over five clients made dozens of them: the reading
/// every live screen depends on took 78 seconds, so the Dashboard sat still while downloads ran.
///
/// Here a <c>localhost</c> name is connected as <c>127.0.0.1</c> first and <c>::1</c> second, and every other
/// name exactly as the framework would. The request itself, its Host header and its cookies are untouched.
/// </summary>
public static class LoopbackFirstConnection
{
    /// <summary>A handler that connects this way. Callers set cookies, credentials, redirects or a proxy on it as they need.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectCallback = ConnectAsync };

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (!IsLoopbackName(endpoint.Host))
        {
            return await ConnectNamedHostAsync(endpoint.Host, endpoint.Port, cancellationToken);
        }

        try
        {
            return await ConnectToAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), cancellationToken);
        }
        catch (SocketException) when (Socket.OSSupportsIPv6)
        {
            return await ConnectToAsync(new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port), cancellationToken);
        }
    }

    // ---- Any other name: IPv6 and IPv4 raced (#1522) --------------------------------------------------
    // The framework tries a name's addresses one after another, IPv6 first. On a machine with only a
    // private IPv6 address every IPv6 try fails slowly, and one search to the metadata gateway spent
    // 1.8 s of it, every time. Here the next address starts 250 ms after the one before (sooner if that
    // one fails), the first connection wins, and a host whose IPv6 failed while IPv4 worked is tried
    // IPv4 first for the next ten minutes (RFC 8305).

    internal static readonly TimeSpan AttemptStagger = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Ipv6RestFor = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Ipv6RestUntil = new(StringComparer.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectNamedHostAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        if (addresses.Length <= 1)
        {
            return await ConnectToAsync(new DnsEndPoint(host, port), cancellationToken);
        }

        var ordered = OrderAddresses(host, addresses);
        var socket = await RaceAsync(
            ordered,
            AttemptStagger,
            async (address, token) =>
            {
                var attempt = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await attempt.ConnectAsync(new IPEndPoint(address, port), token);
                    return attempt;
                }
                catch
                {
                    attempt.Dispose();
                    throw;
                }
            },
            failedFamilyHost: host,
            cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    internal static IReadOnlyList<IPAddress> OrderAddresses(string host, IReadOnlyList<IPAddress> addresses)
    {
        var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
        var v4 = addresses.Where(a => a.AddressFamily != AddressFamily.InterNetworkV6).ToList();
        var resting = Ipv6RestUntil.TryGetValue(host, out var until) && until > DateTimeOffset.UtcNow;
        var first = resting ? v4 : v6;
        var second = resting ? v6 : v4;

        // Alternate families, as RFC 8305 asks, so one dead family never holds up the other.
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < second.Count) result.Add(second[i]);
        }

        return result;
    }

    internal static async Task<Socket> RaceAsync(
        IReadOnlyList<IPAddress> addresses,
        TimeSpan stagger,
        Func<IPAddress, CancellationToken, Task<Socket>> attempt,
        string? failedFamilyHost,
        CancellationToken cancellationToken)
    {
        using var losers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<(Task<Socket> Task, IPAddress Address)>();
        var next = 0;
        Exception? last = null;
        var ipv6Failed = false;
        Socket? winner = null;

        while (winner is null)
        {
            if (next < addresses.Count)
            {
                var address = addresses[next++];
                pending.Add((attempt(address, losers.Token), address));
            }

            if (pending.Count == 0)
            {
                break;
            }

            var waits = new List<Task>(pending.Select(p => (Task)p.Task));
            Task? delay = null;
            if (next < addresses.Count)
            {
                delay = Task.Delay(stagger, cancellationToken);
                waits.Add(delay);
            }

            var done = await Task.WhenAny(waits);
            if (ReferenceEquals(done, delay))
            {
                continue;
            }

            foreach (var finished in pending.Where(p => p.Task.IsCompleted).ToList())
            {
                pending.Remove(finished);
                if (finished.Task.IsCompletedSuccessfully)
                {
                    winner ??= finished.Task.Result;
                    if (!ReferenceEquals(winner, finished.Task.Result))
                    {
                        finished.Task.Result.Dispose();
                    }
                }
                else
                {
                    last = finished.Task.Exception?.GetBaseException();
                    ipv6Failed |= finished.Address.AddressFamily == AddressFamily.InterNetworkV6;
                }
            }
        }

        // Whatever is still trying is cancelled, and a late success is closed rather than leaked.
        losers.Cancel();
        foreach (var (task, _) in pending)
        {
            _ = task.ContinueWith(
                done => { if (done.IsCompletedSuccessfully) done.Result.Dispose(); else _ = done.Exception; },
                TaskScheduler.Default);
        }

        if (winner is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw last ?? new SocketException((int)SocketError.HostUnreachable);
        }

        if (ipv6Failed && failedFamilyHost is not null && winner.AddressFamily != AddressFamily.InterNetworkV6)
        {
            Ipv6RestUntil[failedFamilyHost] = DateTimeOffset.UtcNow + Ipv6RestFor;
        }

        return winner;
    }

    internal static bool IsLoopbackName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectToAsync(EndPoint target, CancellationToken cancellationToken)
    {
        // The same socket the framework opens by default: dual-mode, no Nagle delay.
        var socket = target is IPEndPoint ip
            ? new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(target, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
