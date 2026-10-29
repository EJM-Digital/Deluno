using System.Net;
using System.Net.Http.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Contracts;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The live hub's subject rules and the test-only push routes (coverage register #1329, area shell): the hub joins only the
/// subjects the screens use, and the routes a browser test pushes through are not there in a normal install.
/// </summary>
public sealed class RealtimeSubjectAndTestRoutesRowsTests
{
    private static async Task<(IHost Host, HubConnection Connection)> ConnectAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(TimeProvider.System);
                    services.AddSignalR();
                    services.AddSingleton(provider => new SignalRRealtimeEventPublisher(
                        provider.GetRequiredService<IHubContext<ActivityHub>>(),
                        NullLogger<SignalRRealtimeEventPublisher>.Instance,
                        provider.GetRequiredService<TimeProvider>(),
                        SignalRRealtimeEventPublisher.DefaultResumeWindowSize));
                    services.AddSingleton<IRealtimeEventPublisher>(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                    services.AddSingleton<IRealtimeResumeSource>(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                    services.AddSingleton<RealtimeWatcherTracker>();
                    services.AddHostedService(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoRealtime());
                }))
            .Build();
        await host.StartAsync();
        var server = host.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/deluno"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        await connection.StartAsync();
        return (host, connection);
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("queue")]
    [InlineData("activity")]
    [InlineData("live")]
    [InlineData("library:8f2c1a40-aaaa-4bbb-8ccc-0123456789ab")]
    [InlineData("title:movie-42")]
    public async Task Subscribe_accepts_a_subject_the_screens_use(string subject)
    {
        var (host, connection) = await ConnectAsync();
        try
        {
            var tracker = host.Services.GetRequiredService<RealtimeWatcherTracker>();
            await connection.InvokeAsync("Subscribe", new[] { subject });
            Assert.Equal(1, tracker.Count(subject));
            await connection.InvokeAsync("Unsubscribe", new[] { subject });
            Assert.Equal(0, tracker.Count(subject));
        }
        finally
        {
            await connection.DisposeAsync();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("Dashboard")]
    [InlineData("library:")]
    [InlineData("library:has space")]
    [InlineData("title:../../etc")]
    [InlineData("title:")]
    [InlineData("movie:1")]
    [InlineData("")]
    public async Task Subscribe_refuses_any_other_name_with_an_error_that_names_it_and_joins_nothing(string subject)
    {
        var (host, connection) = await ConnectAsync();
        try
        {
            var refused = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", new[] { "dashboard", subject }));

            Assert.Contains($"Realtime subject '{subject}' is not valid.", refused.Message, StringComparison.Ordinal);
            var tracker = host.Services.GetRequiredService<RealtimeWatcherTracker>();
            Assert.Equal(0, tracker.Count("dashboard"));
        }
        finally
        {
            await connection.DisposeAsync();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task The_test_only_push_routes_are_not_in_a_normal_install()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var path in new[] { "/api/testing/realtime/activity", "/api/testing/realtime/download-progress", "/api/testing/realtime/event", "/api/testing/realtime/live-event" })
        {
            var response = await app.Client.PostAsJsonAsync(path, new { message = "x" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_test_only_push_routes_exist_only_when_the_testing_switch_is_on()
    {
        await using var app = await ApplicationTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Testing:EnableRealtimePushEndpoint"] = "true" });

        var response = await app.Client.PostAsJsonAsync("/api/testing/realtime/activity", new { message = "Pushed by a test" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_test_only_push_routes_still_need_a_signed_in_caller_when_the_switch_is_on()
    {
        await using var app = await ApplicationTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Testing:EnableRealtimePushEndpoint"] = "true" });

        var response = await app.CreateAnonymousClient().PostAsJsonAsync("/api/testing/realtime/activity", new { message = "Pushed by nobody" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
