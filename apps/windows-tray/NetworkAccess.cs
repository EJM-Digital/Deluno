using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Deluno.Tray;

/// <summary>
/// Enumerates the URLs that Deluno is currently reachable at, so the tray
/// tooltip + log can show both <c>http://localhost:&lt;port&gt;/</c> and the
/// LAN URLs (e.g. <c>http://192.168.1.42:&lt;port&gt;/</c>) other devices
/// on the same network can use.
///
/// Only enumerates interfaces that are Up and not loopback/tunnel. Filters
/// IPv4 only — most users' LAN routing assumes v4, and IPv6 link-local
/// addresses are noisy and rarely useful in a browser address bar.
/// </summary>
internal static class NetworkAccess
{
    public static IReadOnlyList<string> GetReachableUrls(int port)
    {
        var urls = new List<string> { $"http://localhost:{port}/" };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var ipProps = nic.GetIPProperties();
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    // Skip 169.254.x.x APIPA addresses (no DHCP lease) — they
                    // can't be reached from other LAN devices anyway.
                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;

                    urls.Add($"http://{ua.Address}:{port}/");
                }
            }
        }
        catch
        {
            // Network enumeration can throw on locked-down hosts; localhost
            // is always the fallback.
        }

        return urls;
    }

    /// <summary>
    /// Multi-line string for log files / tray tooltip showing every URL the
    /// server is reachable at.
    /// </summary>
    public static string FormatReachableUrls(int port)
    {
        var urls = GetReachableUrls(port);
        return string.Join(Environment.NewLine, urls.Select(u => "  " + u));
    }
}
