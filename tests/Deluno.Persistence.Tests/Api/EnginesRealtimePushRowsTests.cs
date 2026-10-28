using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The server-side pushes of the engines area (SystemTaskChanged, UpdateStatusChanged, OutboundThrottleChanged) fire at the moments the register names.</summary>
public sealed class EnginesRealtimePushRowsTests
{
    private static async Task<(ApplicationTestHost App, RecordingRealtimeEventPublisher Publisher)> StartAsync()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));
        return (app, publisher);
    }

    [Fact]
    public async Task A_scheduled_task_pushes_SystemTaskChanged_when_it_is_claimed_and_when_its_outcome_is_recorded_but_not_when_the_claim_is_refused()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var key = "engines-row-task";

        Assert.True(await jobs.TryClaimScheduledPassAsync(key, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(1, publisher.Published.Count(item => item == ("SystemTask", key)));

        Assert.False(await jobs.TryClaimScheduledPassAsync(key, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(1, publisher.Published.Count(item => item == ("SystemTask", key)));

        await jobs.RecordScheduledPassOutcomeAsync(key, DateTimeOffset.UtcNow, "completed", 12, null, CancellationToken.None);
        Assert.Equal(2, publisher.Published.Count(item => item == ("SystemTask", key)));
    }

    [Theory]
    [InlineData("/api/updates/check")]
    [InlineData("/api/updates/download")]
    [InlineData("/api/updates/restart-now")]
    public async Task Each_stage_of_an_update_pushes_UpdateStatusChanged(string route)
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;
        var before = publisher.Published.Count(item => item == ("UpdateStatus", "updates"));

        var response = await app.Client.PostAsync(route, content: null);

        Assert.True(response.IsSuccessStatusCode, $"{route} answered {(int)response.StatusCode}");
        Assert.True(publisher.Published.Count(item => item == ("UpdateStatus", "updates")) > before, route + " pushed no UpdateStatusChanged");
    }

    [Fact]
    public async Task Saving_the_update_preferences_pushes_UpdateStatusChanged()
    {
        var (app, publisher) = await StartAsync();
        await using var _ = app;

        var response = await app.Client.PutAsJsonAsync("/api/updates/preferences", new { autoCheck = true });

        Assert.True(response.IsSuccessStatusCode, $"answered {(int)response.StatusCode}");
        Assert.Contains(("UpdateStatus", "updates"), publisher.Published);
    }

    private sealed class Watchers(int count) : IRealtimeWatchers
    {
        public int Count(string subject) => subject == "dashboard" ? count : 0;
    }

    [Fact]
    public async Task The_throttle_watcher_pushes_OutboundThrottleChanged_only_when_the_figures_change_and_someone_is_watching()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var throttle = new OutboundRequestThrottle(TimeProvider.System, NullLogger<OutboundRequestThrottle>.Instance);
        var watched = new OutboundThrottleWatcher(throttle, publisher, new Watchers(1), TimeProvider.System, NullLogger<OutboundThrottleWatcher>.Instance);
        var unwatched = new OutboundThrottleWatcher(throttle, publisher, new Watchers(0), TimeProvider.System, NullLogger<OutboundThrottleWatcher>.Instance);
        var rate = new OutboundRate(100, TimeSpan.FromSeconds(1), 100);

        await watched.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(("OutboundThrottle", "outbound"), publisher.Published);

        Assert.NotNull(await throttle.TryAcquireAsync("indexer.example", rate, TimeSpan.FromSeconds(1), CancellationToken.None));
        await unwatched.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(("OutboundThrottle", "outbound"), publisher.Published);

        await watched.TickAsync(CancellationToken.None);
        Assert.Single(publisher.Published, item => item == ("OutboundThrottle", "outbound"));

        await watched.TickAsync(CancellationToken.None);
        Assert.Single(publisher.Published, item => item == ("OutboundThrottle", "outbound"));
    }
}
