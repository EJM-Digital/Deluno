using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Storage;

/// <summary>#1080: a background loop waiting for the databases keeps waiting instead of ending on a timeout.</summary>
public sealed class StartupGateWaitTests
{
    [Fact]
    public async Task Waiting_past_the_slice_does_not_throw_and_returns_once_the_databases_are_ready()
    {
        var gate = new DelunoStartupGate();
        var waiting = gate.WaitUntilReadyAsync(NullLogger.Instance, "Test service", CancellationToken.None, TimeSpan.FromMilliseconds(30));

        await Task.Delay(150); // reason: the gate's own 30 ms slice is real time: the wait must outlast it and still be waiting
        Assert.False(waiting.IsCompleted);

        foreach (var database in DelunoStorageLayout.Databases)
        {
            gate.MarkReady(database.Key);
        }

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Stopping_ends_the_wait_with_false()
    {
        var gate = new DelunoStartupGate();
        using var stopping = new CancellationTokenSource();
        var waiting = gate.WaitUntilReadyAsync(NullLogger.Instance, "Test service", stopping.Token, TimeSpan.FromMilliseconds(30));

        stopping.Cancel();

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
