using Deluno.Api;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Every write that can change what the bell counts or lists tells the open bell (#1179).</summary>
public sealed class AttentionChangedPushTests
{
    // The request paths of apps/web/src/lib/alert-actions.ts, and the Keep and Remove of leftovers.
    [Theory]
    [InlineData("/api/attention/no-release/movie/abc/dismiss", "noRelease")]
    [InlineData("/api/attention/no-release/series/abc/dismiss", "noRelease")]
    [InlineData("/api/attention/held-releases/h1/dismiss", "heldReleases")]
    [InlineData("/api/attention/held-releases/h1/approve", "heldReleases")]
    [InlineData("/api/movies/import-recovery/c1/dismiss", "recovery")]
    [InlineData("/api/movies/import-recovery/c1/retry", "recovery")]
    [InlineData("/api/movies/import-recovery/c1/stop-wanting", "recovery")]
    [InlineData("/api/movies/import-recovery/c1/import-original", "recovery")]
    [InlineData("/api/movies/import-recovery/c1/search-again", "recovery")]
    [InlineData("/api/series/import-recovery/c1/dismiss", "recovery")]
    [InlineData("/api/series/import-recovery/c1/retry", "recovery")]
    [InlineData("/api/series/import-recovery/c1/stop-wanting", "recovery")]
    [InlineData("/api/jobs/j1/dismiss", "jobs")]
    [InlineData("/api/jobs/retry-failed", "jobs")]
    [InlineData("/api/integrations/processors/handoffs/h1/retry", "handoff")]
    [InlineData("/api/integrations/processors/handoffs/h1/dismiss", "handoff")]
    [InlineData("/api/integrations/processors/handoffs/h1/import-original", "handoff")]
    [InlineData("/api/integrations/processors/handoffs/h1/search-again", "handoff")]
    [InlineData("/api/download-clients/orphans/keep", "orphans")]
    [InlineData("/api/download-clients/orphans/remove", "orphans")]
    [InlineData("/api/download-clients/c1/queue/q1/remove", "client")]
    [InlineData("/api/download-clients/c1/queue/q1/health/stalled/ignore", "client")]
    [InlineData("/api/connections/indexer/i1/version/dismiss", "update")]
    [InlineData("/api/system/health/indexer:i1/snooze", "health")]
    [InlineData("/api/system/health/indexer:i1/unsnooze", "health")]
    [InlineData("/api/system/health/background-problems/startup:x/retry", "health")]
    [InlineData("/api/system/component-recovery/ffmpeg/dismiss", "health")]
    public async Task A_successful_write_to_a_bell_route_pushes_attention_changed(string path, string reason)
    {
        Assert.Equal(reason, AttentionChangedPush.ReasonFor(new PathString(path)));

        var publisher = new RecordingRealtimeEventPublisher();
        await Run(publisher, "POST", path, 204);

        var pushed = await WaitForAsync(publisher, 1);
        Assert.Equal(reason, ((AttentionChangedEvent)Assert.Single(pushed).Payload).Reason);
    }

    [Fact]
    public async Task A_read_a_failure_or_a_route_the_bell_does_not_read_pushes_nothing()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        await Run(publisher, "GET", "/api/attention/no-release", 200);
        await Run(publisher, "POST", "/api/attention/no-release/movie/abc/dismiss", 404);
        await Run(publisher, "POST", "/api/movies", 200);
        await Task.Delay(AttentionCoalesce + TimeSpan.FromMilliseconds(300)); // reason: a push that must NOT happen can only be shown absent once the production coalesce window (real timer) has passed
        Assert.Empty(publisher.DashboardEvents);
    }

    [Fact]
    public async Task A_burst_of_writes_sends_one_push_naming_everything_that_moved()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        for (var i = 0; i < 6; i++)
        {
            await Run(publisher, "POST", $"/api/attention/no-release/movie/m{i}/dismiss", 204);
        }

        await Run(publisher, "POST", "/api/jobs/j1/dismiss", 204);
        var pushed = await WaitForAsync(publisher, 1);
        await Task.Delay(AttentionCoalesce + TimeSpan.FromMilliseconds(300)); // reason: shows no second push follows, which only the production coalesce window (real timer) passing can show

        Assert.Single(publisher.DashboardEvents, item => item.Name == LiveEventNames.AttentionChanged);
        Assert.Equal("jobs,noRelease", ((AttentionChangedEvent)Assert.Single(pushed).Payload).Reason);
    }

    [Fact]
    public async Task A_server_side_change_asks_for_the_same_push()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        publisher.RequestAttentionChanged("noRelease");
        publisher.RequestAttentionChanged("noRelease");
        var pushed = await WaitForAsync(publisher, 1);
        Assert.Equal("noRelease", ((AttentionChangedEvent)Assert.Single(pushed).Payload).Reason);
    }

    private static readonly TimeSpan AttentionCoalesce = LiveEventPublisherExtensions.AttentionCoalesceWindow;

    private static async Task<IReadOnlyList<(string Name, object Payload)>> WaitForAsync(RecordingRealtimeEventPublisher publisher, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var events = publisher.DashboardEvents.Where(item => item.Name == LiveEventNames.AttentionChanged).ToArray();
            if (events.Length >= count)
            {
                return events;
            }

            await Task.Delay(25); // reason: poll interval inside a bounded wait (5 s above)
        }

        throw new Xunit.Sdk.XunitException("AttentionChanged was not pushed.");
    }

    private static async Task Run(RecordingRealtimeEventPublisher publisher, string method, string path, int status)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRealtimeEventPublisher>(publisher)
            .BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseDelunoAttentionChangedPush();
        builder.Run(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        await builder.Build()(context);
    }
}
