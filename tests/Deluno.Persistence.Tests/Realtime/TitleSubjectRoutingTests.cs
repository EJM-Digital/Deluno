using Deluno.Contracts;
using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1179: a title page joins <c>title:{id}</c> and hears about its own film or show only, so the
/// server sends it the title's own changes and journey events, and nobody else's.
/// </summary>
public sealed class TitleSubjectRoutingTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher _publisher;

    public TitleSubjectRoutingTests()
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

    /// <summary>What was sent to these subjects since sequence one, by event name.</summary>
    private async Task<string[]> SentToAsync(params string[] subjects)
    {
        await _publisher.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await Task.Delay(10); // reason: poll interval inside a bounded wait (200 tries above)
            var result = _publisher.Resume(1, subjects);
            if (result.Status == RealtimeResumeStatus.Replayed && result.Envelopes.Count > 0)
            {
                await Task.Delay(50); // reason: lets any further event settle so the list is the whole burst; absence needs real time
                return [.. _publisher.Resume(1, subjects).Envelopes.Select(envelope => $"{envelope.Name}@{envelope.Subject}")];
            }
        }

        return [];
    }

    [Fact]
    public async Task A_title_change_goes_to_the_dashboard_the_title_page_and_its_libraries()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishTitleChangedAsync("Movie", "m1", ["lib-a", "lib-b"], CancellationToken.None);

        var sent = await SentToAsync("dashboard", "title:m1", "library:lib-a", "library:lib-b", "title:other");

        Assert.Contains("MovieChanged@dashboard", sent);
        Assert.Contains("MovieChanged@title:m1", sent);
        Assert.Contains("MovieChanged@library:lib-a", sent);
        Assert.Contains("MovieChanged@library:lib-b", sent);
    }

    [Fact]
    public async Task A_change_to_one_title_never_reaches_another_titles_page()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishTitleChangedAsync("Series", "s1", [], CancellationToken.None);

        var sent = await SentToAsync("title:s2");

        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_handler_that_publishes_a_film_change_reaches_the_title_page_too()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishEntityChangedAsync("Movie", "m7", CancellationToken.None);
        await _publisher.PublishEntityChangedAsync("Movie", "*", CancellationToken.None);

        var sent = await SentToAsync("title:m7", "title:*");

        Assert.Equal(["MovieChanged@title:m7"], sent);
    }

    [Fact]
    public async Task Journey_and_search_events_about_a_title_reach_its_page()
    {
        await _publisher.PublishEntityChangedAsync("Warm", "up", CancellationToken.None);
        await _publisher.PublishJourneyStageChangedAsync(
            new JourneyStageChangedEvent("d1", "Heat.1995", "movies", "movie", "m1", "lib-a", "importing", null, "2026-10-08T00:00:00Z"),
            CancellationToken.None);
        await _publisher.PublishReleasesTurnedDownAsync(
            new ReleasesTurnedDownEvent("Heat", "movie", "m1", "2026-10-08T00:00:00Z", []),
            CancellationToken.None);
        await _publisher.PublishImportStateChangedAsync("j1", "completed", "movie", "m1", "Heat", null, "2026-10-08T00:00:00Z", CancellationToken.None);

        var sent = await SentToAsync("title:m1");

        Assert.Contains("JourneyStageChanged@title:m1", sent);
        Assert.Contains("ReleasesTurnedDown@title:m1", sent);
        Assert.Contains("ImportStateChanged@title:m1", sent);
    }
}
