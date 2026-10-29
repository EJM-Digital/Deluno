using Deluno.Realtime;
using Deluno.Realtime.Hubs;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>The low-severity rows of the live hub (#1329): the connection count and the worker-driven Ping.</summary>
public sealed class RealtimeWatcherLowGapTests
{
    private sealed class NoStream : IRealtimeResumeSource
    {
        public string InstanceId => "test-instance";

        public long CurrentSequence => 0;

        public RealtimeResumeResult Resume(long lastSeq, IReadOnlyCollection<string> subjects, string? instanceId = null)
            => new(RealtimeResumeStatus.CaughtUp, []);
    }

    [Fact]
    public void The_connection_count_never_goes_below_zero_if_Disconnected_is_called_more_often_than_Connected()
    {
        var tracker = new RealtimeWatcherTracker();

        tracker.Disconnected();
        Assert.Equal(0, tracker.ConnectionCount);

        tracker.Connected();
        tracker.Connected();
        tracker.Disconnected();
        tracker.Disconnected();
        tracker.Disconnected();
        tracker.Disconnected();
        Assert.Equal(0, tracker.ConnectionCount);

        tracker.Connected();
        Assert.Equal(1, tracker.ConnectionCount);
    }

    [Fact]
    public async Task The_hubs_Ping_method_answers_a_client_invoke_at_once_and_changes_nothing()
    {
        var tracker = new RealtimeWatcherTracker();
        tracker.Connected();
        using var hub = new ActivityHub(new NoStream(), tracker);

        var ping = hub.Ping();

        Assert.True(ping.IsCompletedSuccessfully);
        await ping;
        Assert.Equal(1, tracker.ConnectionCount);
        Assert.Equal(0, tracker.Count("dashboard"));
        Assert.Contains(typeof(ActivityHub).GetMethods(), method => method.Name == "Ping" && method.IsPublic && method.GetParameters().Length == 0);
    }
}
