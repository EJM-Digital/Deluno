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
}
