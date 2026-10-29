using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Security;

/// <summary>
/// #1329: Allowed Hosts and "skip sign-in on the local network" as they run: a refused address is logged once however often it is
/// tried, Health shows one plain item while Allowed Hosts is off and clears once it is on, and turning the sign-in skip on or off
/// is written to Activity in words.
/// </summary>
public sealed class AllowedHostsAndLocalSignInRowsTests
{
    private sealed class FixedSettings(AllowedHostsSettings settings) : IAllowedHostsSettingsCache
    {
        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
        public void Invalidate() { }
    }

    private sealed class CapturingLogger : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Warnings);
        public void Dispose() { }

        private sealed class Sink(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) { lock (warnings) { warnings.Add(formatter(state, exception)); } }
            }
        }
    }

    private static async Task<(IHost Host, CapturingLogger Log)> StartMiddlewareAsync(params string[] allowed)
    {
        var log = new CapturingLogger();
        var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(log))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IAllowedHostsSettingsCache>(new FixedSettings(new AllowedHostsSettings(true, allowed)));
                    services.AddSingleton<IForwardedHostTracker, ForwardedHostTracker>();
                })
                .Configure(app =>
                {
                    app.UseDelunoAllowedHosts();
                    app.Run(context => context.Response.WriteAsync("answered"));
                }))
            .Build();
        await host.StartAsync();
        return (host, log);
    }

    private static async Task<HttpStatusCode> AskAsync(IHost host, string hostName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = hostName;
        using var client = host.GetTestClient();
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task A_refused_host_is_logged_once_however_often_it_is_tried_and_a_different_refused_host_is_logged_on_its_own()
    {
        var (host, log) = await StartMiddlewareAsync("allowed.example");
        using var _ = host;
        var first = $"refused-{Guid.NewGuid():N}.example";
        var second = $"other-{Guid.NewGuid():N}.example";

        for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(HttpStatusCode.BadRequest, await AskAsync(host, first));

        Assert.Single(log.Warnings, line => line.Contains(first, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, await AskAsync(host, second));
        Assert.Equal(HttpStatusCode.BadRequest, await AskAsync(host, second));
        Assert.Single(log.Warnings, line => line.Contains(second, StringComparison.Ordinal));
        Assert.Equal(2, log.Warnings.Count);
        Assert.All(log.Warnings, line => Assert.Contains("Allowed Hosts does not include it", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_allowed_host_and_localhost_are_answered_and_never_logged()
    {
        var (host, log) = await StartMiddlewareAsync("allowed.example");
        using var _ = host;

        Assert.Equal(HttpStatusCode.OK, await AskAsync(host, "allowed.example"));
        Assert.Equal(HttpStatusCode.OK, await AskAsync(host, "localhost"));
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task Health_shows_one_plain_item_with_a_fix_while_Allowed_Hosts_is_off_and_an_ok_once_it_is_on()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var check = new AllowedHostsHealthCheck(app.Services.GetRequiredService<IAllowedHostsRepository>());

        var off = Assert.Single(await check.RunAsync(CancellationToken.None));
        Assert.Equal(AllowedHostsHealthCheck.Id, off.Id);
        Assert.Equal(HealthCheckStatuses.Suggestion, off.Status);
        Assert.Equal("Deluno answers on any address. Choose which ones it should use.", off.Message);
        Assert.Contains("Open Security", off.Fix, StringComparison.Ordinal);

        var saved = await app.Client.PutAsJsonAsync("/api/security/allowed-hosts", new { enabled = true, allowedHosts = new[] { "localhost", "deluno.example" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var on = Assert.Single(await check.RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Ok, on.Status);
        Assert.Equal("Deluno only answers on 2 chosen addresses.", on.Message);
    }

    [Fact]
    public async Task Turning_the_local_sign_in_skip_on_and_off_writes_a_line_to_Activity_in_words_and_saving_the_same_choice_writes_none()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();

        var on = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new { enabled = true, additionalLocalNetworks = Array.Empty<string>(), allowList = Array.Empty<string>(), trustedProxies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var again = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new { enabled = true, additionalLocalNetworks = Array.Empty<string>(), allowList = Array.Empty<string>(), trustedProxies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var off = await app.Client.PutAsJsonAsync("/api/security/local-network-auth", new { enabled = false, additionalLocalNetworks = Array.Empty<string>(), allowList = Array.Empty<string>(), trustedProxies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var feed = await app.Client.GetFromJsonAsync<JsonElement>("/api/activity?pageSize=50");
        var lines = feed.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("category").GetString() == "security.local-network-auth")
            .Select(item => item.GetProperty("message").GetString())
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("Skip sign-in for devices on the local network turned on.", lines);
        Assert.Contains("Skip sign-in for devices on the local network turned off.", lines);
        _ = activity;
    }
}
