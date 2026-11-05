using Deluno.Realtime;
using Deluno.Realtime.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// The hub's two helpers are optional: a host that counts watchers and writes the reconnect line has them, one without does not
/// (the unit-test and tool hosts). Joining, leaving and resuming answer the same either way, and the reconnect line says "none" for a
/// page that named no earlier run (#1497).
/// </summary>
public sealed class TheActivityHubWorksWithoutAWatcherTrackerOrALogTests
{
    private sealed class Source : IRealtimeResumeSource
    {
        public string InstanceId => "this-run";

        public long CurrentSequence => 0;

        public string? Asked { get; private set; }

        public RealtimeResumeResult Resume(long lastSeq, IReadOnlyCollection<string> subjects, string? instanceId = null)
        {
            Asked = instanceId;
            return new(RealtimeResumeStatus.CaughtUp, [], InstanceId);
        }
    }

    private sealed class Lines : ILogger<ActivityHub>
    {
        public List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Written.Add(formatter(state, exception));
    }

    private sealed class FakeContext : HubCallerContext
    {
        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class FakeGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ActivityHub HubFor(Source source, RealtimeWatcherTracker? tracker = null, ILogger<ActivityHub>? log = null)
        => new(source, tracker, log) { Context = new FakeContext(), Groups = new FakeGroups() };

    [Fact]
    public async Task A_hub_with_no_watcher_tracker_joins_leaves_and_disconnects_without_error()
    {
        using var hub = HubFor(new Source());

        await hub.OnConnectedAsync();
        await hub.Subscribe([RealtimeGroups.Dashboard, "title:abc-1"]);
        await hub.Unsubscribe([RealtimeGroups.Dashboard]);
        await hub.Unsubscribe([RealtimeGroups.Dashboard]);
        await hub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task A_hub_with_a_watcher_tracker_counts_what_a_connection_joins_and_all_of_it_goes_when_it_drops()
    {
        var tracker = new RealtimeWatcherTracker();
        using var hub = HubFor(new Source(), tracker);

        await hub.OnConnectedAsync();
        await hub.Subscribe([RealtimeGroups.Dashboard, RealtimeGroups.Live]);
        await hub.Unsubscribe([RealtimeGroups.Live]);
        Assert.Equal(1, tracker.Count(RealtimeGroups.Dashboard));
        Assert.Equal(0, tracker.Count(RealtimeGroups.Live));

        await hub.OnDisconnectedAsync(null);

        Assert.Equal(0, tracker.Count(RealtimeGroups.Dashboard));
        Assert.Equal(0, tracker.ConnectionCount);
    }

    [Fact]
    public void A_resume_with_no_log_still_asks_the_source_and_a_page_naming_no_run_is_passed_on_as_none_named()
    {
        var source = new Source();
        using var hub = HubFor(source);

        var result = hub.Resume(5, [RealtimeGroups.Dashboard], null);

        Assert.Equal(RealtimeResumeStatus.CaughtUp, result.Status);
        Assert.Equal(string.Empty, source.Asked);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("earlier-run", "earlier-run")]
    public void The_reconnect_line_names_the_run_the_page_came_from_or_says_none(string? pageRun, string said)
    {
        var lines = new Lines();
        using var hub = HubFor(new Source(), log: lines);

        hub.Resume(5, [RealtimeGroups.Dashboard], pageRun);

        var line = Assert.Single(lines.Written);
        Assert.Contains($"of run {said};", line);
        Assert.Contains("this run is this-run", line);
    }
}
