using Deluno.Api.Monitoring;
using Deluno.Api.SystemInfo;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What tells an open System screen to read monitoring and the databases again (#1294, #1179): it looks only while a screen is
/// watching, the first look only remembers, and it pushes only when what the screen shows has moved. The data folder is a real
/// temp folder with real database files whose sizes change.
/// </summary>
public sealed class SystemChangeWatcherTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("deluno-watcher-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingPublisher : NullRealtimeEventPublisher
    {
        public List<string> Entities { get; } = [];

        public override Task PublishEntityChangedAsync(string entityType, string entityId, CancellationToken cancellationToken)
        {
            Entities.Add(entityType);
            return Task.CompletedTask;
        }
    }

    private sealed class Watching(int dashboard) : IRealtimeWatchers
    {
        public int Dashboard { get; set; } = dashboard;
        public int Count(string subject) => subject == "dashboard" ? Dashboard : 0;
    }

    private sealed class Monitoring : IMonitoringService
    {
        public int Reads { get; private set; }
        public MonitoringAlertItem[] Alerts { get; set; } = [];
        public DateTimeOffset Generated { get; set; } = DateTimeOffset.UnixEpoch;

        public Task<MonitoringDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new MonitoringDashboardSnapshot(
                Generated,
                new MonitoringReadinessSummary("ready", true, 5, 0),
                new MonitoringStorageSummary("data", 1000, 500, 50, false),
                new MonitoringServiceSummary(1, 1, 1, 1, 0, 0, 0, 0),
                new MonitoringPerformanceSummary(0, null, null, null, new ApiLatencySnapshot(Generated, Generated, 0, 0, 0, 0, 0)),
                Alerts));
        }

        public Task<IReadOnlyList<MonitoringAlertItem>> GetAlertsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Deluno.Contracts.Page<MonitoringDiagnosticItem>> SearchDiagnosticsAsync(MonitoringDiagnosticsQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MonitoringExportSnapshot> BuildExportSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private (SystemChangeWatcher Watcher, RecordingPublisher Publisher, Monitoring Monitoring, Watching Watching) Build(int watchers)
    {
        File.WriteAllText(Path.Combine(_dataRoot, "movies.db"), new string('x', 100));
        var publisher = new RecordingPublisher();
        var monitoring = new Monitoring();
        var watching = new Watching(watchers);
        var watcher = new SystemChangeWatcher(
            Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
            monitoring,
            publisher,
            watching,
            TimeProvider.System,
            NullLogger<SystemChangeWatcher>.Instance);
        return (watcher, publisher, monitoring, watching);
    }

    private static MonitoringAlertItem Alert(string code) => new(code, "warning", $"{code} summary", $"{code} details", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task With_nobody_watching_it_does_nothing_at_all()
    {
        var (watcher, publisher, monitoring, _) = Build(watchers: 0);

        await watcher.TickAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(_dataRoot, "movies.db"), new string('x', 5000));
        monitoring.Alerts = [Alert("a")];
        await watcher.TickAsync(CancellationToken.None);

        Assert.Empty(publisher.Entities);
        Assert.Equal(0, monitoring.Reads);
    }

    [Fact]
    public async Task The_first_look_only_remembers_and_pushes_nothing()
    {
        var (watcher, publisher, monitoring, _) = Build(watchers: 1);

        await watcher.TickAsync(CancellationToken.None);

        Assert.Empty(publisher.Entities);
        Assert.Equal(1, monitoring.Reads);
    }

    [Fact]
    public async Task A_look_at_the_same_readings_pushes_nothing_even_though_the_timestamp_moved()
    {
        var (watcher, publisher, monitoring, _) = Build(watchers: 1);
        await watcher.TickAsync(CancellationToken.None);

        monitoring.Generated = DateTimeOffset.UtcNow;
        await watcher.TickAsync(CancellationToken.None);

        Assert.Empty(publisher.Entities);
    }

    [Fact]
    public async Task A_new_alert_pushes_monitoring_changed_once_and_not_again_until_something_else_moves()
    {
        var (watcher, publisher, monitoring, _) = Build(watchers: 1);
        await watcher.TickAsync(CancellationToken.None);

        monitoring.Alerts = [Alert("storage")];
        await watcher.TickAsync(CancellationToken.None);
        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(["Monitoring"], publisher.Entities);
    }

    [Fact]
    public async Task A_database_that_grew_pushes_system_databases_changed()
    {
        var (watcher, publisher, _, _) = Build(watchers: 1);
        await watcher.TickAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(_dataRoot, "movies.db"), new string('x', 9000));
        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(["SystemDatabases"], publisher.Entities);
    }

    [Fact]
    public async Task Looking_resumes_when_a_screen_opens_again_and_the_first_look_after_that_pushes_nothing()
    {
        var (watcher, publisher, monitoring, watching) = Build(watchers: 0);
        await watcher.TickAsync(CancellationToken.None);
        watching.Dashboard = 1;

        await watcher.TickAsync(CancellationToken.None);

        Assert.Empty(publisher.Entities);
        Assert.Equal(1, monitoring.Reads);
    }
}
