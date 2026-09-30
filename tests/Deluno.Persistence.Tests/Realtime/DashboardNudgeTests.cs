using Deluno.Contracts;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The Dashboard refetches one summary when told something changed (#1082). The
/// hub says so with one coalesced DashboardSummaryChanged nudge, never one per
/// progress tick.
/// </summary>
public sealed class DashboardNudgeTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher _publisher;

    public DashboardNudgeTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<TimeProvider>(_clock);
        _services = services.BuildServiceProvider();
        _publisher = new SignalRRealtimeEventPublisher(
            _services.GetRequiredService<IHubContext<ActivityHub>>(),
            NullLogger<SignalRRealtimeEventPublisher>.Instance,
            _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync(CancellationToken.None);
        _publisher.Dispose();
        await _services.DisposeAsync();
    }

    private async Task<IReadOnlyList<RealtimeEnvelope>> WaitForDashboardEventsAsync(string name, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        IReadOnlyList<RealtimeEnvelope> found = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = _publisher.Resume(1, [RealtimeGroups.Dashboard]);
            found = result.Envelopes.Where(envelope => envelope.Name == name).ToArray();
            if (found.Count >= expected)
            {
                break;
            }

            await Task.Delay(10);
        }

        return found;
    }

    private async Task<int> NudgesAfterSettlingAsync()
    {
        // Let anything queued reach the resume window before counting.
        await Task.Delay(150);
        return _publisher.Resume(1, [RealtimeGroups.Dashboard]).Envelopes.Count(envelope => envelope.Name == "DashboardSummaryChanged");
    }

    private async Task StartWithAnOpeningEventAsync()
    {
        await _publisher.StartAsync(CancellationToken.None);
        // Sequence 1 is the opening event, so Resume(1, ...) can read everything after it.
        await _publisher.PublishEntityChangedAsync("Widget", "w1", CancellationToken.None);
        await WaitForDashboardEventsAsync("WidgetChanged", 1);
    }

    [Fact]
    public async Task The_first_change_nudges_at_once_and_a_burst_folds_into_one_more_nudge_two_seconds_later()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishHealthChangedAsync("indexer", "problem", "Down", CancellationToken.None);
        Assert.Single(await WaitForDashboardEventsAsync("DashboardSummaryChanged", 1));

        for (var tick = 0; tick < 60; tick++)
        {
            await _publisher.PublishDownloadProgressAsync("d1", "Film", tick, 1.5, null, "downloading", CancellationToken.None);
            await _publisher.PublishQueueItemStatusChangedAsync("d1", "downloading", null, CancellationToken.None);
        }

        _clock.Advance(TimeSpan.FromMilliseconds(1900));
        Assert.Equal(1, await NudgesAfterSettlingAsync());

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, (await WaitForDashboardEventsAsync("DashboardSummaryChanged", 2)).Count);
        Assert.Equal(2, await NudgesAfterSettlingAsync());
    }

    [Fact]
    public async Task A_quiet_spell_ends_with_an_immediate_nudge_on_the_next_change()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishActivityEventAddedAsync("a1", "Something happened", "search", "info", "2026-09-30T12:00:00Z", CancellationToken.None);
        Assert.Single(await WaitForDashboardEventsAsync("DashboardSummaryChanged", 1));

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _publisher.PublishImportStateChangedAsync("j1", "completed", "movie", "m1", "Film", null, "2026-09-30T12:01:00Z", CancellationToken.None);

        Assert.Equal(2, (await WaitForDashboardEventsAsync("DashboardSummaryChanged", 2)).Count);
    }

    [Fact]
    public async Task An_event_the_dashboard_does_not_read_never_nudges()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishEntityChangedAsync("Widget", "w2", CancellationToken.None);
        await _publisher.PublishEntityChangedAsync("QualityProfile", "q1", CancellationToken.None);

        Assert.Equal(0, await NudgesAfterSettlingAsync());
    }

    [Fact]
    public async Task The_nudge_goes_to_the_dashboard_group_and_carries_no_numbers()
    {
        await StartWithAnOpeningEventAsync();

        await _publisher.PublishSearchRunCompletedAsync("l1", "Films", "movies", 5, 2, 3, "2026-09-30T12:00:00Z", CancellationToken.None);

        var nudge = Assert.Single(await WaitForDashboardEventsAsync("DashboardSummaryChanged", 1));
        Assert.Equal(RealtimeGroups.Dashboard, nudge.Subject);
        Assert.Contains("asOfUtc", System.Text.Json.JsonSerializer.Serialize(nudge.Data));
    }
}
