using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Health;

public sealed class StartupGateTests
{
    [Fact]
    public async Task Gate_is_not_ready_until_every_registered_database_is_migrated()
    {
        var gate = new DelunoStartupGate();

        Assert.False(gate.IsReady);
        Assert.Contains("series", gate.PendingDatabases);

        foreach (var databaseName in new[] { "platform", "movies", "series", "jobs", "cache" })
        {
            gate.MarkReady(databaseName);
        }

        await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.True(gate.IsReady);
        Assert.Empty(gate.PendingDatabases);
    }

    [Fact]
    public async Task Failed_database_keeps_gate_closed_and_is_reported()
    {
        var gate = new DelunoStartupGate();
        gate.MarkFailed("series", new InvalidOperationException("migration failed"));

        Assert.False(gate.IsReady);
        Assert.Equal("migration failed", gate.FailedDatabases["series"]);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            gate.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public void A_database_can_be_marked_ready_after_a_transient_failure()
    {
        var gate = new DelunoStartupGate();
        gate.MarkFailed("series", new InvalidOperationException("temporary"));
        gate.MarkReady("series");

        Assert.DoesNotContain("series", gate.FailedDatabases.Keys);
        Assert.DoesNotContain("series", gate.PendingDatabases);
    }

    [Fact]
    public void An_unregistered_database_is_refused_whether_it_is_ready_or_failed()
    {
        var gate = new DelunoStartupGate();

        Assert.Throws<ArgumentException>(() => gate.MarkReady("nope"));
        Assert.Throws<ArgumentException>(() => gate.MarkFailed("nope", new InvalidOperationException("x")));
    }

    [Fact]
    public void The_last_database_ready_moves_start_up_on_to_the_web_host_once()
    {
        var steps = new List<StartupPhase>();
        var gate = new DelunoStartupGate(new Recorder(steps));

        foreach (var database in DelunoStorageLayout.Databases)
        {
            Assert.Empty(steps);
            gate.MarkReady(database.Key);
        }
        gate.MarkReady("platform");

        Assert.Equal([StartupPhase.WebHost], steps);
    }

    private sealed class Recorder(List<StartupPhase> steps) : IStartupProgress
    {
        public void Enter(StartupPhase phase, string? detail = null) => steps.Add(phase);
    }
}
