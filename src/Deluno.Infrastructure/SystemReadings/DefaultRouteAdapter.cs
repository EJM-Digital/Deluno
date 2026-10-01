using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The network adapter that carries the machine's default route: the one the operating system would
/// use to reach the internet. Found by asking the system which local address it would send from (a
/// UDP "connect" sends nothing), then taking the adapter that owns it. The same on Windows and Linux.
/// </summary>
public static class DefaultRouteAdapter
{
    /// <summary>A well-known public address, used only to ask the routing table which way out it would take. Nothing is sent to it.</summary>
    private static readonly IPAddress RouteProbeAddress = IPAddress.Parse("8.8.8.8");

    public static NetworkAdapterReading? Read()
    {
        try
        {
            var local = LocalAddressForDefaultRoute();
            if (local is null)
            {
                return null;
            }

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                if (!properties.UnicastAddresses.Any(address => address.Address.Equals(local)))
                {
                    continue;
                }

                var statistics = adapter.GetIPStatistics();
                return new NetworkAdapterReading(
                    adapter.Name,
                    local.ToString(),
                    SpeedToMbps(adapter.Speed),
                    statistics.BytesReceived,
                    statistics.BytesSent);
            }

            return null;
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Bits per second to megabits per second; null when the adapter does not know its speed.</summary>
    public static long? SpeedToMbps(long bitsPerSecond) => bitsPerSecond > 0 ? bitsPerSecond / 1_000_000 : null;

    private static IPAddress? LocalAddressForDefaultRoute()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(RouteProbeAddress, 53));
        return (socket.LocalEndPoint as IPEndPoint)?.Address;
    }
}
