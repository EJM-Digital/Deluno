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

    /// <summary>What one network adapter says about itself; the addresses and counters are only read for an adapter that is up.</summary>
    public sealed record AdapterFacts(string Name, bool IsUp, IReadOnlyList<IPAddress> Addresses, long BitsPerSecond, long BytesReceived, long BytesSent);

    public static NetworkAdapterReading? Read() => Read(LocalAddressForDefaultRoute, AdaptersOfThisComputer);

    /// <summary>The same, with the route probe and the adapters handed in so every outcome can be shown on any computer.</summary>
    public static NetworkAdapterReading? Read(Func<IPAddress?> localAddress, Func<IEnumerable<AdapterFacts>> adapters)
    {
        try
        {
            var local = localAddress();
            if (local is null)
            {
                return null;
            }

            foreach (var adapter in adapters())
            {
                if (!adapter.IsUp || !adapter.Addresses.Any(address => address.Equals(local)))
                {
                    continue;
                }

                return new NetworkAdapterReading(
                    adapter.Name,
                    local.ToString(),
                    SpeedToMbps(adapter.BitsPerSecond),
                    adapter.BytesReceived,
                    adapter.BytesSent);
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

    public static IEnumerable<AdapterFacts> AdaptersOfThisComputer()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                yield return new AdapterFacts(adapter.Name, false, [], 0, 0, 0);
                continue;
            }

            var statistics = adapter.GetIPStatistics();
            yield return new AdapterFacts(
                adapter.Name,
                true,
                [.. adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address)],
                adapter.Speed,
                statistics.BytesReceived,
                statistics.BytesSent);
        }
    }

    private static IPAddress? LocalAddressForDefaultRoute()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(RouteProbeAddress, 53));
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }
}
