using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// Coverage register (#1329), background workers inventory: the tracker site-switch service makes one pass after the start-up
/// gate. An exception in that pass is caught and logged and never ends the host; waiting for the gate is cancelled cleanly at
/// shutdown, and the pass never runs then.
/// </summary>
public sealed class TheTrackerSiteSwitchServiceSurvivesAFailureAndAShutdownTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-site-switch-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class ListLogger : ILogger<TrackerSiteSwitchHostedService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add($"{formatter(state, exception)} | {exception?.Message}");
            }
        }
    }

    private TrackerSiteSwitchHostedService Build(IConnectionsRepository connections, IDelunoStartupGate gate, ILogger<TrackerSiteSwitchHostedService> logger)
        => new(
            connections,
            new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance),
            Mock.Of<IActivityFeedRepository>(MockBehavior.Strict),
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            gate,
            logger);

    [Fact]
    public async Task A_pass_that_throws_is_logged_as_a_warning_and_the_service_ends_without_an_error()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(item => item.ListIndexersAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("the indexers table is locked"));
        var logger = new ListLogger();
        var service = Build(connections.Object, Mock.Of<IDelunoStartupGate>(), logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("could not check which tracker indexers moved", warning, StringComparison.Ordinal);
        Assert.Contains("the indexers table is locked", warning, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dataRoot, "trackers", TrackerSiteSwitchHostedService.MarkerFileName)));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_while_the_service_waits_for_the_start_up_gate_cancels_cleanly_and_runs_no_pass()
    {
        var gate = new Mock<IDelunoStartupGate>();
        gate.Setup(item => item.WaitAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<TimeSpan, CancellationToken>((_, token) => Task.Delay(Timeout.Infinite, token)); // reason: a gate that never opens; only the shutdown token ends it
        var connections = new Mock<IConnectionsRepository>(MockBehavior.Strict);
        var logger = new ListLogger();
        var service = Build(connections.Object, gate.Object, logger);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Empty(logger.Warnings);
        connections.VerifyNoOtherCalls();
    }
}
