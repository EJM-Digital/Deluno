using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform.Tests.Resilience;

/// <summary>
/// #1522: on a machine with only a private IPv6 address every IPv6 try to the metadata gateway failed slowly, and one search
/// spent 1.8 s connecting, every time. The addresses are raced and the connection is kept for the next search.
/// </summary>
public sealed class AMachineWithOnlyAPrivateIPv6AddressConnectsOverIPv4AtOnceTests
{
    private static readonly IPAddress DeadIPv6 = IPAddress.Parse("2606:4700:3036::6815:474e");
    private static readonly IPAddress LiveIPv4 = IPAddress.Loopback;

    [Fact]
    public async Task An_IPv6_address_that_never_answers_does_not_hold_up_the_IPv4_one_beyond_the_head_start()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tried = new List<IPAddress>();

        var clock = Stopwatch.StartNew();
        using var socket = await LoopbackFirstConnection.RaceAsync(
            [DeadIPv6, LiveIPv4],
            TimeSpan.FromMilliseconds(250),
            async (address, token) =>
            {
                lock (tried) { tried.Add(address); }
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    await Task.Delay(Timeout.Infinite, token); // reason: the fake IPv6 host never answers and never refuses; the race cancels it
                }

                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(new IPEndPoint(address, port), token);
                return s;
            },
            failedFamilyHost: null,
            CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1500), $"connecting took {clock.Elapsed}");
        Assert.Equal([DeadIPv6, LiveIPv4], tried);
    }

    [Fact]
    public async Task An_IPv6_address_that_fails_at_once_is_followed_by_IPv4_without_waiting_for_the_head_start_and_is_rested()
    {
        const string host = "gateway-one.example";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clock = Stopwatch.StartNew();
        using var socket = await LoopbackFirstConnection.RaceAsync(
            [DeadIPv6, LiveIPv4],
            TimeSpan.FromSeconds(30),
            async (address, token) =>
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    throw new SocketException((int)SocketError.NetworkUnreachable);
                }

                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(new IPEndPoint(address, port), token);
                return s;
            },
            host,
            CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        // The next connection to that name starts with IPv4.
        Assert.Equal(AddressFamily.InterNetwork, LoopbackFirstConnection.OrderAddresses(host, [DeadIPv6, LiveIPv4])[0].AddressFamily);
    }

    [Fact]
    public async Task Every_address_failing_reports_the_last_failure()
    {
        await Assert.ThrowsAsync<SocketException>(() => LoopbackFirstConnection.RaceAsync(
            [DeadIPv6, LiveIPv4],
            TimeSpan.FromMilliseconds(10),
            (address, token) => throw new SocketException((int)SocketError.ConnectionRefused),
            failedFamilyHost: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task Two_requests_in_a_row_from_two_requests_scopes_use_one_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true) // reason: the fake server accepts until the test cancels it
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                    _ = Task.Run(() => KeepAliveAsync(client, stop.Token));
                }
            }
            catch (OperationCanceledException) { }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.GetTempPath(), EnvironmentName = "Test" });
        services.AddDelunoInfrastructure(new ConfigurationBuilder().Build());
        services.AddHttpClient<Probe>(client => client.Timeout = TimeSpan.FromSeconds(30));
        using var provider = services.BuildServiceProvider();

        for (var i = 0; i < 3; i++)
        {
            using var scope = provider.CreateScope();
            Assert.Equal("ok", await scope.ServiceProvider.GetRequiredService<Probe>().GetAsync($"http://localhost:{port}/"));
        }

        Assert.Equal(1, accepted);
        await stop.CancelAsync();
        listener.Stop();
        await serving;
    }

    private static async Task KeepAliveAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer, token) > 0)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"), token);
                }
            }
            catch (Exception) when (token.IsCancellationRequested || true) { }
        }
    }

    public sealed class Probe(HttpClient http)
    {
        public Task<string> GetAsync(string url) => http.GetStringAsync(url);
    }
}
