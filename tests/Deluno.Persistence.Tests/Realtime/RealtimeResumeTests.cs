using System.Collections.Concurrent;
using System.Diagnostics;
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
/// A client that trusts a lossy, unsequenced stream drifts out of sync and
/// never recovers -- that is the failure ADR-002 calls out as worse than
/// polling. These tests exercise the fix end to end: a real client drops
/// mid-stream, reconnects, and resumes from its last sequence number.
/// </summary>
public sealed class RealtimeResumeTests
{
    /// <summary>
    /// Builds a real host with the realtime module wired up. <paramref name="resumeWindowSize"/>
    /// defaults to production (5,000); the boundary test overrides it to a
    /// handful so exceeding the window doesn't require publishing thousands
    /// of events.
    /// </summary>
    private static async Task<Fixture> StartAsync(int resumeWindowSize = SignalRRealtimeEventPublisher.DefaultResumeWindowSize)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(TimeProvider.System);
                        services.AddSignalR();
                        services.AddSingleton(provider => new SignalRRealtimeEventPublisher(
                            provider.GetRequiredService<IHubContext<ActivityHub>>(),
                            NullLogger<SignalRRealtimeEventPublisher>.Instance,
                            provider.GetRequiredService<TimeProvider>(),
                            resumeWindowSize));
                        services.AddSingleton<IRealtimeEventPublisher>(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                        services.AddSingleton<IRealtimeResumeSource>(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                        services.AddHostedService(p => p.GetRequiredService<SignalRRealtimeEventPublisher>());
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapDelunoRealtime());
                    });
            });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return new Fixture(host);
    }

    private sealed class Fixture(IHost host) : IAsyncDisposable
    {
        private readonly TestServer _server = host.GetTestServer();

        public IRealtimeEventPublisher Publisher => _server.Services.GetRequiredService<IRealtimeEventPublisher>();
        public IRealtimeResumeSource ResumeSource => _server.Services.GetRequiredService<IRealtimeResumeSource>();

        public HubConnection Connect() =>
            new HubConnectionBuilder()
                .WithUrl(new Uri(_server.BaseAddress, "hubs/deluno"), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _server.CreateHandler();
                })
                .Build();

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Client_that_disconnects_mid_stream_converges_after_reconnect_via_replay()
    {
        await using var fixture = await StartAsync();

        var receivedBeforeDrop = new ConcurrentQueue<RealtimeEnvelope>();
        await using var first = fixture.Connect();
        first.On<RealtimeEnvelope>("RealtimeEvent", envelope => receivedBeforeDrop.Enqueue(envelope));
        await first.StartAsync();
        await first.InvokeAsync("Subscribe", new[] { RealtimeGroups.Dashboard });

        for (var i = 0; i < 3; i++)
        {
            await fixture.Publisher.PublishHealthChangedAsync("indexer", "healthy", $"probe {i}", CancellationToken.None);
        }
        await WaitUntilAsync(() => receivedBeforeDrop.Count >= 3);

        var lastSeqBeforeDrop = receivedBeforeDrop.Max(envelope => envelope.Seq);

        // Kill the connection mid-stream -- these publishes happen while nobody is listening.
        await first.StopAsync();
        for (var i = 3; i < 6; i++)
        {
            await fixture.Publisher.PublishHealthChangedAsync("indexer", "healthy", $"probe {i}", CancellationToken.None);
        }

        await using var second = fixture.Connect();
        await second.StartAsync();
        await second.InvokeAsync("Subscribe", new[] { RealtimeGroups.Dashboard });

        var result = await WaitForResumeAsync(
            second,
            lastSeqBeforeDrop,
            new[] { RealtimeGroups.Dashboard },
            envelopes => envelopes.Count >= 3);

        Assert.Equal(RealtimeResumeStatus.Replayed, result.Status);
        Assert.Equal([lastSeqBeforeDrop + 1, lastSeqBeforeDrop + 2, lastSeqBeforeDrop + 3], result.Envelopes.Select(e => e.Seq));

        var converged = receivedBeforeDrop
            .Where(envelope => envelope.Seq <= lastSeqBeforeDrop)
            .Concat(result.Envelopes)
            .OrderBy(envelope => envelope.Seq)
            .Select(envelope => envelope.Seq)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), converged);

        await second.StopAsync();
    }

    [Fact]
    public async Task Resume_reports_caught_up_when_the_client_already_has_the_latest_sequence()
    {
        await using var fixture = await StartAsync();

        var received = new ConcurrentQueue<RealtimeEnvelope>();
        await using var connection = fixture.Connect();
        connection.On<RealtimeEnvelope>("RealtimeEvent", envelope => received.Enqueue(envelope));
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", new[] { RealtimeGroups.Dashboard });

        await fixture.Publisher.PublishHealthChangedAsync("indexer", "healthy", "probe", CancellationToken.None);
        await WaitUntilAsync(() => received.Count >= 1);

        var latestSeq = received.Max(envelope => envelope.Seq);

        var caughtUp = fixture.ResumeSource.Resume(latestSeq, new[] { RealtimeGroups.Dashboard });

        Assert.Equal(RealtimeResumeStatus.CaughtUp, caughtUp.Status);
        Assert.Empty(caughtUp.Envelopes);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Resume_requires_resync_when_the_client_has_no_prior_sequence()
    {
        await using var fixture = await StartAsync();

        await fixture.Publisher.PublishHealthChangedAsync("indexer", "healthy", "probe", CancellationToken.None);

        var noHistory = fixture.ResumeSource.Resume(0, new[] { RealtimeGroups.Dashboard });

        Assert.Equal(RealtimeResumeStatus.ResyncRequired, noHistory.Status);
        Assert.Empty(noHistory.Envelopes);
    }

    [Fact]
    public async Task Resume_requires_resync_once_the_gap_exceeds_the_resume_window()
    {
        const int windowSize = 5;
        await using var fixture = await StartAsync(resumeWindowSize: windowSize);

        for (var i = 0; i < windowSize + 3; i++)
        {
            await fixture.Publisher.PublishHealthChangedAsync("indexer", "healthy", $"probe {i}", CancellationToken.None);
        }

        await WaitUntilAsync(() => fixture.ResumeSource.Resume(1, new[] { RealtimeGroups.Dashboard }).Status == RealtimeResumeStatus.ResyncRequired);

        var beyondWindow = fixture.ResumeSource.Resume(1, new[] { RealtimeGroups.Dashboard });

        Assert.Equal(RealtimeResumeStatus.ResyncRequired, beyondWindow.Status);
        Assert.Empty(beyondWindow.Envelopes);
    }

    [Fact]
    public async Task Connections_receive_only_events_for_their_subscribed_subjects()
    {
        await using var fixture = await StartAsync();

        var queueEvents = new ConcurrentQueue<RealtimeEnvelope>();
        var activityEvents = new ConcurrentQueue<RealtimeEnvelope>();
        await using var queueConnection = fixture.Connect();
        await using var activityConnection = fixture.Connect();
        queueConnection.On<RealtimeEnvelope>("RealtimeEvent", envelope => queueEvents.Enqueue(envelope));
        activityConnection.On<RealtimeEnvelope>("RealtimeEvent", envelope => activityEvents.Enqueue(envelope));
        await queueConnection.StartAsync();
        await activityConnection.StartAsync();
        await queueConnection.InvokeAsync("Subscribe", new[] { RealtimeGroups.Queue });
        await activityConnection.InvokeAsync("Subscribe", new[] { RealtimeGroups.Activity });

        await fixture.Publisher.PublishDownloadProgressAsync(
            "download-1",
            "Arrival",
            0.5,
            12.5,
            null,
            "downloading",
            CancellationToken.None);

        await WaitUntilAsync(() => queueEvents.Count == 1);
        await Task.Delay(100);

        Assert.Single(queueEvents);
        Assert.Empty(activityEvents);
        Assert.Equal(RealtimeGroups.Queue, queueEvents.Single().Subject);
    }

    [Fact]
    public async Task Hub_rejects_invalid_realtime_subjects()
    {
        await using var fixture = await StartAsync();
        await using var connection = fixture.Connect();
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Subscribe", new[] { "settings" }));
    }

    private static async Task<RealtimeResumeResult> WaitForResumeAsync(
        HubConnection connection,
        long lastSeq,
        IReadOnlyCollection<string> subjects,
        Func<IReadOnlyList<RealtimeEnvelope>, bool> isConverged,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        RealtimeResumeResult result;
        do
        {
            result = await connection.InvokeAsync<RealtimeResumeResult>("Resume", lastSeq, subjects);
            if (isConverged(result.Envelopes))
            {
                return result;
            }
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        Assert.Fail($"Resume did not converge within the timeout. Last status: {result.Status}, envelopes: {result.Envelopes.Count}.");
        throw new UnreachableException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }
}
