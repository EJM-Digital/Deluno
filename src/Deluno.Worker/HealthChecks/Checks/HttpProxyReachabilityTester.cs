using System.Net.Http.Json;
using System.Net.Sockets;
using Deluno.Connections.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Tests a configured indexer proxy (#748) for real, the way each proxy kind
/// actually answers:
///
/// <para>A FlareSolverr/Byparr proxy is an HTTP API of its own, so this sends
/// it the same <c>sessions.list</c> command <see
/// cref="Deluno.Integrations.Search.Trackers.FlareSolverrCloudflareHelper"/>
/// could -- one that needs no session and changes nothing -- and checks it
/// answered <c>ok</c>.</para>
///
/// <para>An HTTP/SOCKS4/SOCKS5 proxy has no such API; the real thing Deluno
/// asks of it is "can Deluno open a TCP connection to you", so this connects
/// directly to its host and port rather than routing a request through it at
/// an arbitrary third-party address, which would make the result depend on
/// that address's own uptime as much as the proxy's.</para>
/// </summary>
public sealed class HttpProxyReachabilityTester(IHttpClientFactory httpClientFactory, TimeSpan? connectTimeout = null) : IProxyReachabilityTester
{
    /// <summary>How long a forward proxy has to accept the connection before it is reported as not answering.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan ConnectTimeout = connectTimeout ?? DefaultConnectTimeout;

    public async Task<ProxyReachabilityResult> TestAsync(ProxyItem proxy, CancellationToken cancellationToken)
    {
        return string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase)
            ? await TestFlareSolverrAsync(proxy, cancellationToken)
            : await TestForwardProxyAsync(proxy, cancellationToken);
    }

    private async Task<ProxyReachabilityResult> TestFlareSolverrAsync(ProxyItem proxy, CancellationToken cancellationToken)
    {
        var trimmed = proxy.Address.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return new ProxyReachabilityResult(false, "No address is configured for this proxy.");
        }

        var endpoint = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/v1";

        try
        {
            var http = httpClientFactory.CreateClient(HealthCheckHttpClients.Proxy);
            using var response = await http.PostAsJsonAsync(endpoint, new { cmd = "sessions.list" }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProxyReachabilityResult(false, $"{proxy.Name} answered with HTTP {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<FlareSolverrStatus>(cancellationToken: cancellationToken);
            return string.Equals(answer?.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? new ProxyReachabilityResult(true, $"{proxy.Name} answered its sessions.list command.")
                : new ProxyReachabilityResult(false, $"{proxy.Name} did not answer sessions.list with status ok.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or System.Text.Json.JsonException)
        {
            return new ProxyReachabilityResult(false, $"Deluno could not reach {proxy.Name} at {trimmed}: {exception.Message}");
        }
    }

    private async Task<ProxyReachabilityResult> TestForwardProxyAsync(ProxyItem proxy, CancellationToken cancellationToken)
    {
        if (!TryParseHostAndPort(proxy, out var host, out var port))
        {
            return new ProxyReachabilityResult(false, $"{proxy.Address} is not a usable proxy address (expected host:port).");
        }

        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await client.ConnectAsync(host, port, linked.Token);
            return new ProxyReachabilityResult(true, $"{proxy.Name} accepted a connection at {host}:{port}.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ProxyReachabilityResult(false, $"{proxy.Name} did not accept a connection at {host}:{port} within {ConnectTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException)
        {
            return new ProxyReachabilityResult(false, $"Deluno could not connect to {proxy.Name} at {host}:{port}: {exception.Message}");
        }
    }

    /// <summary>
    /// Parses this proxy's address as <c>scheme://host:port</c> or a bare
    /// <c>host:port</c>, defaulting the port to the usual one for HTTP or
    /// SOCKS when the address does not give one.
    /// </summary>
    private static bool TryParseHostAndPort(ProxyItem proxy, out string host, out int port)
    {
        var address = proxy.Address.Trim();
        var withScheme = address.Contains("://", StringComparison.Ordinal) ? address : $"{NormalizedScheme(proxy.ProxyType)}://{address}";

        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            port = uri.IsDefaultPort ? DefaultPort(proxy.ProxyType) : uri.Port;
            return true;
        }

        host = string.Empty;
        port = 0;
        return false;
    }

    private static string NormalizedScheme(string proxyType) => string.Equals(proxyType, ProxyTypes.Http, StringComparison.OrdinalIgnoreCase) ? "http" : "socks5";

    private static int DefaultPort(string proxyType) => string.Equals(proxyType, ProxyTypes.Http, StringComparison.OrdinalIgnoreCase) ? 8080 : 1080;

    private sealed record FlareSolverrStatus(string? Status);
}
