using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform.Tests.Resilience;

/// <summary>
/// #1178: a download client saved as <c>localhost</c> that listens on IPv4 only cost two seconds on every new
/// connection on Windows (IPv6 <c>::1</c> is tried first, refused, and retried), and the reading every live
/// screen depends on took 78 seconds on the rig. Every connection Deluno opens goes through one connect step.
/// </summary>
public sealed class LoopbackFirstConnectionTests
{
    [Fact]
    public async Task A_localhost_client_listening_on_IPv4_only_answers_at_once()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeAsync(listener, requests: 3);

        using var http = new HttpClient(LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            // Connection: close, as several clients answer, so each request opens a new connection.
            Assert.Equal("ok", await http.GetStringAsync($"http://localhost:{port}/"));
        }

        clock.Stop();
        await serving;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"Three connections to localhost took {clock.Elapsed.TotalMilliseconds:N0} ms.");
    }

    [Fact]
    public async Task A_localhost_client_listening_on_IPv6_only_is_still_reached()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return; // No IPv6 loopback on this machine.
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeAsync(listener, requests: 1);
        using var http = new HttpClient(LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };

        Assert.Equal("ok", await http.GetStringAsync($"http://localhost:{port}/"));
        await serving;
    }

    [Fact]
    public void Every_named_client_connects_this_way()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.GetTempPath(), EnvironmentName = "Test" });
        services.AddDelunoInfrastructure(new ConfigurationBuilder().Build());
        services.AddHttpClient("download-clients");
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("download-clients");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(sockets.ConnectCallback);
    }

    /// <summary>A handler built by hand would quietly bring the two seconds back, so none is.</summary>
    [Fact]
    public void No_code_builds_an_HTTP_handler_or_client_that_skips_it()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var bareClient = new Regex(@"new HttpClient\s*(\(\s*\)|\{)");
        foreach (var file in new[] { "src", Path.Combine("apps", "windows-tray") }
                     .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            if (file.EndsWith("LoopbackFirstConnection.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var where = $"{Path.GetRelativePath(root, file)}:{i + 1}";
                if (line.Contains("new HttpClientHandler", StringComparison.Ordinal) || bareClient.IsMatch(line))
                {
                    offenders.Add(where);
                }
                else if (line.Contains("new SocketsHttpHandler", StringComparison.Ordinal) || Regex.IsMatch(line, @"SocketsHttpHandler \w+\([^)]*\) => new\(\)"))
                {
                    var initializer = string.Join('\n', lines.Skip(i).Take(4));
                    if (!initializer.Contains("LoopbackFirstConnection", StringComparison.Ordinal))
                    {
                        offenders.Add(where);
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Build handlers from LoopbackFirstConnection: " + string.Join(", ", offenders));
    }

    private static async Task ServeAsync(TcpListener listener, int requests)
    {
        for (var i = 0; i < requests; i++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var read = 0;
            while (!Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(response);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Deluno.Infrastructure")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
