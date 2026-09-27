using System.Net;
using System.Net.Sockets;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Connectivity;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Platform.Tests.Connectivity;

/// <summary>
/// #901: the internet-outage gate has to tell "every host failed" apart from
/// "one bad release", so these exercise the monitor's actual TCP probing
/// against local sockets rather than mocking the network away.
/// </summary>
public sealed class ConnectivityMonitorTests
{
    [Fact]
    public async Task All_hosts_unreachable_is_an_outage_and_is_announced_once()
    {
        var refused = RefusedPorts(3);
        var repository = new FakeOutageRepository();
        var activity = new FakeActivityFeedRepository();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T14:02:00Z"));
        var monitor = CreateMonitor(repository, activity, time, refused);

        var first = await monitor.GetStatusAsync(CancellationToken.None);
        Assert.True(first.IsOutage);
        Assert.Equal(time.GetUtcNow(), first.SinceUtc);
        Assert.Single(activity.Recorded);
        Assert.Equal("connectivity.outage.started", activity.Recorded[0].Category);
        Assert.Contains(time.GetUtcNow().ToLocalTime().ToString("HH:mm"), activity.Recorded[0].Message);

        // A second check while still down does not announce a second time -
        // the design bar (#901) is one banner, not a repeat per poll.
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await monitor.GetStatusAsync(CancellationToken.None);
        Assert.True(second.IsOutage);
        Assert.Single(activity.Recorded);
    }

    [Fact]
    public async Task One_reachable_host_is_not_an_outage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();

        var hosts = RefusedPorts(2).Append($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}").ToArray();
        var repository = new FakeOutageRepository();
        var activity = new FakeActivityFeedRepository();
        var monitor = CreateMonitor(repository, activity, new ManualTimeProvider(DateTimeOffset.UtcNow), hosts);

        var status = await monitor.GetStatusAsync(CancellationToken.None);

        Assert.False(status.IsOutage);
        Assert.Null(status.SinceUtc);
        Assert.Empty(activity.Recorded);

        using var accepted = await acceptTask;
    }

    [Fact]
    public async Task Recovery_closes_the_window_and_announces_once()
    {
        var repository = new FakeOutageRepository();
        var activity = new FakeActivityFeedRepository();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T14:02:00Z"));

        // Start down: probe every host as refused.
        var down = CreateMonitor(repository, activity, time, RefusedPorts(2));
        await down.GetStatusAsync(CancellationToken.None);
        Assert.Single(repository.Windows);
        Assert.Null(repository.Windows[0].EndUtc);

        // Recovers: a fresh monitor sharing the same repository sees one
        // reachable host and closes the window.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        var up = CreateMonitor(repository, activity, time, [$"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"]);
        var status = await up.GetStatusAsync(CancellationToken.None);

        Assert.False(status.IsOutage);
        Assert.Single(repository.Windows);
        Assert.Equal(time.GetUtcNow(), repository.Windows[0].EndUtc);
        Assert.Contains(activity.Recorded, item => item.Category == "connectivity.outage.resolved");

        using var accepted = await acceptTask;
    }

    [Fact]
    public async Task Outage_overlap_is_clamped_to_the_requested_span()
    {
        var repository = new FakeOutageRepository();
        repository.Windows.Add(new ConnectivityOutageWindow(
            DateTimeOffset.Parse("2026-09-27T14:00:00Z"),
            DateTimeOffset.Parse("2026-09-27T14:30:00Z")));
        var monitor = CreateMonitor(repository, new FakeActivityFeedRepository(), new ManualTimeProvider(DateTimeOffset.UtcNow), RefusedPorts(1));

        // Fully inside the window: the whole outage counts.
        var full = await monitor.OutageOverlapAsync(
            DateTimeOffset.Parse("2026-09-27T13:00:00Z"),
            DateTimeOffset.Parse("2026-09-27T15:00:00Z"),
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(30), full);

        // A "since" that starts after the outage window ends never counts it.
        var none = await monitor.OutageOverlapAsync(
            DateTimeOffset.Parse("2026-09-27T15:00:00Z"),
            DateTimeOffset.Parse("2026-09-27T16:00:00Z"),
            CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, none);

        // Partial overlap: only the part inside [since, until] counts.
        var partial = await monitor.OutageOverlapAsync(
            DateTimeOffset.Parse("2026-09-27T14:15:00Z"),
            DateTimeOffset.Parse("2026-09-27T14:45:00Z"),
            CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(15), partial);
    }

    [Fact]
    public async Task An_outage_still_open_overlaps_up_to_the_requested_moment()
    {
        var repository = new FakeOutageRepository();
        repository.Windows.Add(new ConnectivityOutageWindow(DateTimeOffset.Parse("2026-09-27T14:00:00Z"), null));
        var monitor = CreateMonitor(repository, new FakeActivityFeedRepository(), new ManualTimeProvider(DateTimeOffset.UtcNow), RefusedPorts(1));

        var overlap = await monitor.OutageOverlapAsync(
            DateTimeOffset.Parse("2026-09-27T13:30:00Z"),
            DateTimeOffset.Parse("2026-09-27T14:20:00Z"),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(20), overlap);
    }

    private static ConnectivityMonitor CreateMonitor(
        IConnectivityOutageRepository repository,
        IActivityFeedRepository activity,
        TimeProvider timeProvider,
        IReadOnlyList<string> hosts)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deluno:Connectivity:CheckHosts"] = string.Join(',', hosts)
            })
            .Build();

        return new ConnectivityMonitor(repository, activity, timeProvider, configuration, NullLogger<ConnectivityMonitor>.Instance);
    }

    /// <summary>
    /// Ports nothing listens on, on loopback: a connect attempt there is
    /// refused immediately rather than timing out, so these tests stay fast
    /// and need no real network or DNS.
    /// </summary>
    private static string[] RefusedPorts(int count)
    {
        var ports = new List<string>();
        for (var i = 0; i < count; i++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            // Stopped immediately: the port is very likely free again and
            // refuses the next connection attempt outright.
            ports.Add($"127.0.0.1:{port}");
        }

        return [.. ports];
    }

    private sealed class FakeOutageRepository : IConnectivityOutageRepository
    {
        public List<ConnectivityOutageWindow> Windows { get; } = [];

        public Task<IReadOnlyList<ConnectivityOutageWindow>> ListWindowsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ConnectivityOutageWindow>>([.. Windows]);

        public Task RecordOutageStartedAsync(DateTimeOffset startUtc, CancellationToken cancellationToken)
        {
            if (Windows.Count == 0 || Windows[^1].EndUtc is not null)
            {
                Windows.Add(new ConnectivityOutageWindow(startUtc, null));
            }

            return Task.CompletedTask;
        }

        public Task RecordOutageEndedAsync(DateTimeOffset endUtc, CancellationToken cancellationToken)
        {
            if (Windows.Count > 0 && Windows[^1].EndUtc is null)
            {
                Windows[^1] = Windows[^1] with { EndUtc = endUtc };
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeActivityFeedRepository : IActivityFeedRepository
    {
        public List<ActivityEventItem> Recorded { get; } = [];

        public Task<ActivityEventItem> RecordActivityAsync(
            string category,
            string message,
            string? detailsJson,
            string? relatedJobId,
            string? relatedEntityType,
            string? relatedEntityId,
            CancellationToken cancellationToken)
        {
            var item = new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, DateTimeOffset.UtcNow);
            Recorded.Add(item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
        {
            Recorded.AddRange(activities);
            return Task.FromResult(activities);
        }

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ActivityEventItem>>([.. Recorded]);

        public Task<Deluno.Contracts.Page<ActivityEventItem>> ListActivityPageAsync(Deluno.Contracts.PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => Task.FromResult(Deluno.Contracts.Page<ActivityEventItem>.Of(Recorded, null));

        public Task<int> PruneActivityAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
