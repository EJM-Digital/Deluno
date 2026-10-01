using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Deluno.Infrastructure.Storage;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>What one request to a service showed: how long it took to answer, and the time on the server's clock if it said.</summary>
public sealed record HttpTiming(double Milliseconds, DateTimeOffset? ServerDate);

/// <summary>The network readings Deluno takes by asking: each one a real request, none a guess. Tests use fakes.</summary>
public interface INetworkProbe
{
    /// <summary>How long it takes to look up <paramref name="name"/>; null when the lookup fails.</summary>
    Task<double?> LookupMillisecondsAsync(string name, CancellationToken cancellationToken);

    /// <summary>How long <paramref name="host"/> takes to answer; null when it does not.</summary>
    Task<double?> ReachMillisecondsAsync(string host, CancellationToken cancellationToken);

    /// <summary>One request to <paramref name="url"/>; null when nothing answered. Any answer counts, even an error page.</summary>
    Task<HttpTiming?> RequestAsync(Uri url, CancellationToken cancellationToken);
}

public sealed record NetworkCheckResult(
    double? DnsLookupMs,
    double? NasMs,
    string? NasName,
    double? MetadataMs,
    bool? ClockInSync,
    DateTimeOffset CheckedUtc);

/// <summary>
/// The network half of the System overview: a name lookup, the NAS, the metadata service, and whether
/// the clock agrees with the metadata service's. Every check runs at once with its own short limit, so
/// one dead share costs one limit, not one each.
/// </summary>
public sealed class NetworkChecker(INetworkProbe probe, TimeProvider timeProvider, TimeSpan? perCheckLimit = null)
{
    /// <summary>A name every resolver knows, looked up to time the resolver.</summary>
    public const string WellKnownName = "www.cloudflare.com";

    /// <summary>How far the clock may be from the metadata service's before it counts as out of step.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DefaultCheckLimit = TimeSpan.FromSeconds(4);

    public async Task<NetworkCheckResult> RunAsync(string? nasHost, Uri? metadataUrl, CancellationToken cancellationToken)
    {
        var dns = Limited(token => probe.LookupMillisecondsAsync(WellKnownName, token), perCheckLimit ?? DefaultCheckLimit, cancellationToken);
        var nas = string.IsNullOrWhiteSpace(nasHost)
            ? Task.FromResult<double?>(null)
            : Limited(token => probe.ReachMillisecondsAsync(nasHost, token), perCheckLimit ?? DefaultCheckLimit, cancellationToken);
        var metadata = metadataUrl is null
            ? Task.FromResult<HttpTiming?>(null)
            : Limited(token => probe.RequestAsync(metadataUrl, token), perCheckLimit ?? DefaultCheckLimit, cancellationToken);

        await Task.WhenAll(dns, nas, metadata);

        var timing = metadata.Result;
        return new NetworkCheckResult(
            dns.Result,
            nas.Result,
            string.IsNullOrWhiteSpace(nasHost) ? null : nasHost,
            timing?.Milliseconds,
            ClockInSync(timeProvider.GetUtcNow(), timing?.ServerDate),
            timeProvider.GetUtcNow());
    }

    /// <summary>True when the two clocks agree within the tolerance, false when they do not, null when the service gave no time.</summary>
    public static bool? ClockInSync(DateTimeOffset localUtc, DateTimeOffset? serverUtc)
        => serverUtc is { } server ? (localUtc - server).Duration() <= ClockTolerance : null;

    /// <summary>The server of a network path such as <c>\\nas\media</c>; null for a local folder.</summary>
    public static string? NetworkHostOf(string? path)
    {
        var share = VolumeSpace.NetworkShareRoot(path);
        if (share is null)
        {
            return null;
        }

        var host = share.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    private static async Task<TResult?> Limited<TResult>(Func<CancellationToken, Task<TResult>> work, TimeSpan limitFor, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(limitFor);
        try
        {
            return await work(limit.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or SocketException or PingException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }
}

/// <summary>The real probe: the system resolver, a TCP connection to the file-sharing port with a ping as the fallback, and an HTTP request.</summary>
public sealed class NetworkProbe(HttpClient http) : INetworkProbe
{
    /// <summary>SMB, the port a network share is served on.</summary>
    private const int FileSharingPort = 445;

    public async Task<double?> LookupMillisecondsAsync(string name, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken);
            return addresses.Length == 0 ? null : Elapsed(started);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public async Task<double?> ReachMillisecondsAsync(string host, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, FileSharingPort, cancellationToken);
            return Elapsed(started);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            // Not serving files on the usual port: a reply to a ping still says the machine is there.
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success ? Math.Max(0, reply.RoundtripTime) : null;
        }
        catch (Exception exception) when (exception is PingException or SocketException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<HttpTiming?> RequestAsync(Uri url, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new HttpTiming(Elapsed(started), response.Headers.Date);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static double Elapsed(long startedTimestamp) => Math.Round(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds, 1);
}
