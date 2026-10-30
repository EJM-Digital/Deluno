using System.Reflection;
using Deluno.Jobs.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Coverage register (#1329), background workers inventory: a lane with Enabled=false logs and does not start (the Dashboard
/// reads it as disabled); a lane staggers its start by a random jitter that never passes two seconds; and the failure monitor
/// watches only BackgroundService instances, not a plain IHostedService.
/// </summary>
public sealed class ALaneThatIsSwitchedOffDoesNotStartAndStartsSpreadOutTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task A_lane_marked_disabled_logs_that_it_is_not_starting_registers_nothing_and_reads_as_disabled_on_the_board()
    {
        var logger = new ListLogger<DelunoHeartbeatWorker>();
        var signal = new Mock<IJobLaneSignal>(MockBehavior.Strict);
        var board = new JobLaneStateBoard(TimeProvider.System);
        var worker = new DelunoHeartbeatWorker(logger, new Mock<IServiceScopeFactory>().Object, TimeProvider.System, signal.Object, board);
        var lane = new JobLane("test.off", TimeSpan.FromSeconds(5), ["test.job"], Enabled: false);

        var run = (Task)typeof(DelunoHeartbeatWorker)
            .GetMethod("RunLaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [lane, CancellationToken.None])!;
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(run.IsCompletedSuccessfully);
        Assert.Contains(logger.Lines, line => line.StartsWith("Information:", StringComparison.Ordinal) && line.Contains("test.off disabled; not starting.", StringComparison.Ordinal));
        var reading = Assert.Single(board.Snapshot());
        Assert.Equal("test.off", reading.Name);
        Assert.Equal(JobLaneStateBoard.Disabled, reading.State);
        signal.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, 250)]
    [InlineData(4, 1_000)]
    [InlineData(8, 2_000)]
    [InlineData(30, 2_000)]
    [InlineData(300, 2_000)]
    public void A_lanes_start_jitter_is_a_quarter_of_its_interval_and_never_more_than_two_seconds(int intervalSeconds, int expectedMilliseconds)
    {
        var lane = new JobLane("test.jitter", TimeSpan.FromSeconds(intervalSeconds), ["test.job"]);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), lane.Jitter);
    }

    [Fact]
    public void Every_lane_Deluno_runs_starts_within_two_seconds_of_the_others()
    {
        Assert.All(JobLanes.All, lane => Assert.InRange(lane.Jitter, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
    }

    private sealed class PlainHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // A task of its own that ends in an error: only a BackgroundService has an ExecuteTask the monitor can watch.
            _task = Task.Run(() => throw new InvalidOperationException("a plain service's own task failed"), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await (_task ?? Task.CompletedTask);
            }
            catch (InvalidOperationException)
            {
                // the failure the test planted, seen here so it is not left unobserved
            }
        }

        private Task? _task;
    }

    [Fact]
    public async Task A_plain_hosted_service_is_not_watched_so_its_own_failure_never_shows_as_a_stopped_background_service()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDelunoBackgroundServiceSafety();
                services.AddHostedService<PlainHostedService>();
            })
            .Build();

        await host.StartAsync();
        try
        {
            var state = host.Services.GetRequiredService<IBackgroundServiceFailureState>();
            Assert.Empty(state.Failures);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
