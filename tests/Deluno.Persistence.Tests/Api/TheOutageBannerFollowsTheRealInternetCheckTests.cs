using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Connectivity;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The banner that says the internet is down (#901) reads <c>GET /api/system/connectivity</c>, which is the real check:
/// real connections to the hosts it is told to try. Here those hosts are a listener on this machine that is switched off and
/// on, so every host fails together and then one answers, on a real database (the outage windows) and the real Activity feed.
/// </summary>
public sealed class TheOutageBannerFollowsTheRealInternetCheckTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task The_status_follows_a_listener_going_away_and_coming_back_with_one_activity_line_and_one_push_each_way()
    {
        var port = FreePort();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var pushes = new RecordingRealtimeEventPublisher();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Connectivity:CheckHosts"] = $"127.0.0.1:{port}" })
            .Build();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddSingleton<IConnectivityMonitor>(provider => new ConnectivityMonitor(
                provider.GetRequiredService<IConnectivityOutageRepository>(),
                provider.GetRequiredService<IActivityFeedRepository>(),
                clock,
                configuration,
                NullLogger<ConnectivityMonitor>.Instance,
                pushes)));

        // The internet is there: one host answers.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var online = await app.Client.GetFromJsonAsync<ConnectivityStatus>("/api/system/connectivity");
        Assert.False(online!.IsOutage);
        Assert.Equal("Connected.", online.Message);
        Assert.Equal([$"127.0.0.1:{port}"], online.CheckedHosts);
        Assert.Empty(pushes.DashboardEvents);

        // Every host stops answering: after the answer's short life, the banner reads an outage since that moment.
        listener.Stop();
        clock.Advance(TimeSpan.FromSeconds(21));
        var outage = await app.Client.GetFromJsonAsync<ConnectivityStatus>("/api/system/connectivity");
        Assert.True(outage!.IsOutage);
        Assert.Equal(clock.GetUtcNow(), outage.SinceUtc);
        Assert.StartsWith("Internet down since", outage.Message, StringComparison.Ordinal);
        var down = Assert.Single(pushes.DashboardEvents);
        Assert.Equal("ConnectivityChanged", down.Name);
        Assert.True(((ConnectivityChangedEvent)down.Payload).IsOutage);

        // Asked again at once, the same answer is reused: still one push, still one Activity line.
        var again = await app.Client.GetFromJsonAsync<ConnectivityStatus>("/api/system/connectivity");
        Assert.True(again!.IsOutage);
        Assert.Single(pushes.DashboardEvents);

        // The way back.
        var back = new TcpListener(IPAddress.Loopback, port);
        back.Start();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(21));
            var recovered = await app.Client.GetFromJsonAsync<ConnectivityStatus>("/api/system/connectivity");
            Assert.False(recovered!.IsOutage);
            Assert.Null(recovered.SinceUtc);
            Assert.Equal(2, pushes.DashboardEvents.Count);
            Assert.False(((ConnectivityChangedEvent)pushes.DashboardEvents[1].Payload).IsOutage);
        }
        finally
        {
            back.Stop();
        }

        // One line each way in the real Activity feed, in plain words.
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(20, null, null, CancellationToken.None);
        var lines = activity.Where(item => item.Category.StartsWith("connectivity.outage.", StringComparison.Ordinal)).OrderBy(item => item.CreatedUtc).ToArray();
        Assert.Equal(["connectivity.outage.started", "connectivity.outage.resolved"], lines.Select(item => item.Category).ToArray());
        Assert.Contains("cleanup and searching paused", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("Internet connection is back", lines[1].Message, StringComparison.Ordinal);

        // The outage is kept as a closed window, so a stall check can subtract it.
        var windows = await app.Services.GetRequiredService<IConnectivityOutageRepository>().ListWindowsAsync(CancellationToken.None);
        var window = Assert.Single(windows);
        Assert.NotNull(window.EndUtc);
    }

    [Fact]
    public async Task One_host_answering_is_enough_to_say_the_internet_is_up()
    {
        var up = FreePort();
        var dead = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, up);
        listener.Start();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:Connectivity:CheckHosts"] = $"127.0.0.1:{dead}, 127.0.0.1:{up}" })
                .Build();
            await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
                services.AddSingleton<IConnectivityMonitor>(provider => new ConnectivityMonitor(
                    provider.GetRequiredService<IConnectivityOutageRepository>(),
                    provider.GetRequiredService<IActivityFeedRepository>(),
                    new FixedTimeProvider(DateTimeOffset.UtcNow),
                    configuration,
                    NullLogger<ConnectivityMonitor>.Instance)));

            var status = await app.Client.GetFromJsonAsync<ConnectivityStatus>("/api/system/connectivity");

            Assert.False(status!.IsOutage);
            Assert.Equal(2, status.CheckedHosts.Count);
        }
        finally
        {
            listener.Stop();
        }
    }
}
