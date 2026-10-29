using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The low-severity rows of the server's push publisher (#1329): what survives a start-up, what an activity push leaves off, which
/// subjects hear a title's events and where the stream position is shown.
/// </summary>
public sealed class RealtimePublisherLowGapTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher _publisher;

    public RealtimePublisherLowGapTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        _services = services.BuildServiceProvider();
        _publisher = new SignalRRealtimeEventPublisher(
            _services.GetRequiredService<IHubContext<ActivityHub>>(),
            NullLogger<SignalRRealtimeEventPublisher>.Instance,
            TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync(CancellationToken.None);
        _publisher.Dispose();
        await _services.DisposeAsync();
    }

    private async Task<RealtimeEnvelope[]> HeardAsync(int expected, params string[] subjects)
    {
        await _publisher.StartAsync(CancellationToken.None);
        RealtimeEnvelope[] heard = [];
        await Wait.UntilAsync(() =>
        {
            var result = _publisher.Resume(1, subjects);
            heard = result.Status == RealtimeResumeStatus.Replayed ? [.. result.Envelopes] : [];
            return heard.Length >= expected;
        }, $"the publisher to deliver {expected} events");
        return heard;
    }

    private static JsonElement Body(RealtimeEnvelope envelope) => JsonSerializer.SerializeToElement(envelope.Data);

    [Fact]
    public async Task Events_published_before_the_publisher_has_started_still_get_a_sequence_and_are_delivered()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishQueueItemRemovedAsync("q1", CancellationToken.None);
        await _publisher.PublishQueueItemRemovedAsync("q2", CancellationToken.None);
        await _publisher.PublishQueueItemRemovedAsync("q3", CancellationToken.None);
        Assert.Equal(4, _publisher.CurrentSequence);

        var heard = await HeardAsync(3, "queue");

        Assert.Equal([2L, 3L, 4L], heard.Select(item => item.Seq));
        Assert.Equal(["q1", "q2", "q3"], heard.Select(item => Body(item).GetProperty("id").GetString()));
    }

    [Fact]
    public async Task An_activity_push_leaves_details_over_4096_characters_off_and_keeps_smaller_ones()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        var small = "{\"note\":\"" + new string('a', 100) + "\"}";
        var edge = "{\"note\":\"" + new string('a', 4096 - 11) + "\"}";
        var large = "{\"note\":\"" + new string('a', 4096) + "\"}";
        Assert.Equal(4096, edge.Length);

        await _publisher.PublishActivityEventAddedAsync(new ActivityEventPush("a1", "Small", "library.scan", "info", "2026-10-01T12:00:00Z", DetailsJson: small), CancellationToken.None);
        await _publisher.PublishActivityEventAddedAsync(new ActivityEventPush("a2", "Edge", "library.scan", "info", "2026-10-01T12:00:00Z", DetailsJson: edge), CancellationToken.None);
        await _publisher.PublishActivityEventAddedAsync(new ActivityEventPush("a3", "Large", "library.scan", "info", "2026-10-01T12:00:00Z", DetailsJson: large), CancellationToken.None);

        var heard = await HeardAsync(3, "activity");

        Assert.Equal(small, Body(heard[0]).GetProperty("detailsJson").GetString());
        Assert.Equal(edge, Body(heard[1]).GetProperty("detailsJson").GetString());
        Assert.Equal(JsonValueKind.Null, Body(heard[2]).GetProperty("detailsJson").ValueKind);
        Assert.Equal("Large", Body(heard[2]).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Movie", true)]
    [InlineData("movie", true)]
    [InlineData("Series", true)]
    [InlineData("show", true)]
    [InlineData("Episode", false)]
    [InlineData(null, false)]
    public async Task An_import_state_change_goes_to_the_queue_and_to_the_titles_own_subject_only_for_a_movie_or_series(string? entityType, bool toTitle)
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishImportStateChangedAsync("job-1", "finished", entityType, "t1", "Big Buck Bunny", null, "2026-10-01T12:00:00Z", CancellationToken.None);

        await HeardAsync(1, "queue");
        await Wait.UntilAsync(() => _publisher.Resume(1, ["queue", "title:t1"]).Envelopes.Count >= (toTitle ? 2 : 1), "the import push to reach its subjects");

        var all = _publisher.Resume(1, ["queue", "title:t1"]).Envelopes;
        Assert.Contains(all, item => item.Subject == "queue" && item.Name == "ImportStateChanged");
        Assert.Equal(toTitle, all.Any(item => item.Subject == "title:t1" && item.Name == "ImportStateChanged"));
    }

    [Fact]
    public async Task Journey_recovery_and_turned_down_events_about_a_title_also_go_to_that_titles_subject_and_other_events_do_not()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishLiveEventAsync("JourneyStageChanged",
            new JourneyStageChangedEvent("d1", "Release", "movies", "Movie", "m1", "lib-1", "grabbed", null, "2026-10-01T12:00:00Z"), CancellationToken.None);
        await _publisher.PublishLiveEventAsync("RecoveryCaseChanged",
            new RecoveryCaseChangedEvent("c1", "opened", "importFailed", "Film", "Series", "s1", "2026-10-01T12:00:00Z"), CancellationToken.None);
        await _publisher.PublishLiveEventAsync("ReleasesTurnedDown",
            new ReleasesTurnedDownEvent("Film", "Movie", "m2", "2026-10-01T12:00:00Z", []), CancellationToken.None);
        await _publisher.PublishLiveEventAsync("JourneyStageChanged",
            new JourneyStageChangedEvent("d2", "Release", "tv", "Episode", "e1", "lib-1", "grabbed", null, "2026-10-01T12:00:00Z"), CancellationToken.None);

        await _publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _publisher.Resume(1, ["live"]).Envelopes.Count >= 4, "the four live events to be delivered");

        var live = _publisher.Resume(1, ["live"]).Envelopes;
        Assert.Equal(4, live.Count);
        Assert.Single(_publisher.Resume(1, ["title:m1"]).Envelopes, item => item.Name == "JourneyStageChanged");
        Assert.Single(_publisher.Resume(1, ["title:s1"]).Envelopes, item => item.Name == "RecoveryCaseChanged");
        Assert.Single(_publisher.Resume(1, ["title:m2"]).Envelopes, item => item.Name == "ReleasesTurnedDown");
        Assert.Empty(_publisher.Resume(1, ["title:e1"]).Envelopes);
    }

    [Fact]
    public async Task A_checked_connection_is_sent_to_both_the_live_and_the_dashboard_subject()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishConnectionCheckedAsync(
            new ConnectionCheckedEvent("indexer", "i1", "Jackett", true, false, "Working.", "2026-10-01T12:00:00Z"), CancellationToken.None);

        await _publisher.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _publisher.Resume(1, ["live", "dashboard"]).Envelopes.Count(item => item.Name == "ConnectionChecked") >= 2, "both subjects to hear ConnectionChecked");

        var heard = _publisher.Resume(1, ["live", "dashboard"]).Envelopes.Where(item => item.Name == "ConnectionChecked").ToArray();
        Assert.Equal(["dashboard", "live"], heard.Select(item => item.Subject).Order());
        Assert.All(heard, item => Assert.Equal("Jackett", Body(item).GetProperty("Name").GetString()));
    }

    [Fact]
    public async Task The_stream_position_header_is_added_to_api_paths_only()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddSingleton<IRealtimeResumeSource>(_publisher))
                .Configure(app =>
                {
                    app.UseDelunoRealtimePosition();
                    app.Run(context => context.Response.WriteAsync("ok"));
                }))
            .Build();
        await host.StartAsync();
        using var client = host.GetTestClient();

        var api = await client.GetAsync("/api/anything");
        var outside = await client.GetAsync("/health/anything");

        Assert.Equal("1", Assert.Single(api.Headers.GetValues(RealtimePositionHeader.Name)));
        Assert.Equal(_publisher.InstanceId, Assert.Single(api.Headers.GetValues(RealtimePositionHeader.InstanceName)));
        Assert.False(outside.Headers.Contains(RealtimePositionHeader.Name));
        Assert.False(outside.Headers.Contains(RealtimePositionHeader.InstanceName));
        await host.StopAsync();
    }
}
