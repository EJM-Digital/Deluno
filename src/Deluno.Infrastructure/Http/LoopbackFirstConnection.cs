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
            return await ConnectToAsync(new DnsEndPoint(endpoint.Host, endpoint.Port), cancellationToken);
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
