using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Deluno.Security.Contracts;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security.Hardening;

/// <summary>
/// Every address Deluno can point to for the Allowed Hosts card to offer as a
/// tick box (#939): the one answering right now, this machine's name, its LAN
/// IPs, localhost, and any reverse-proxy or Docker host it has actually seen
/// on a request. Detected, never typed -- the acceptance line this exists
/// for.
/// </summary>
public interface IAllowedHostsCandidateProvider
{
    IReadOnlyList<DetectedHostCandidate> DetectCandidates(HttpContext? currentRequest);
}

public sealed class AllowedHostsCandidateProvider(IForwardedHostTracker forwardedHostTracker) : IAllowedHostsCandidateProvider
{
    public IReadOnlyList<DetectedHostCandidate> DetectCandidates(HttpContext? currentRequest)
    {
        var candidates = new List<DetectedHostCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? host, string reason)
        {
            var trimmed = AllowedHostMatcher.StripPort(host ?? string.Empty);
            if (trimmed.Length == 0 || !seen.Add(trimmed)) return;
            candidates.Add(new DetectedHostCandidate(trimmed, reason));
        }

        Add(currentRequest?.Request.Host.Host, "The address you're using right now");
        Add("localhost", "Always answers from this machine");
        Add("127.0.0.1", "Always answers from this machine");

        Add(SafeMachineName(), "This computer's name");

        foreach (var address in LanAddresses())
        {
            Add(address, "A LAN address of this computer");
        }

        foreach (var forwarded in forwardedHostTracker.Seen)
        {
            Add(forwarded, "Seen in a request forwarded from a reverse proxy or Docker host");
        }

        return candidates;
    }

    private static string? SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> LanAddresses()
    {
        List<string> addresses = [];
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only: easy to type and to recognise.
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapters readable -- the rest of the candidates still stand.
        }

        return addresses;
    }
}
