using Deluno.Infrastructure.Storage;
using Deluno.Quality.Guides;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// Coverage register, area services: the guide update check hosted service never blocks Deluno from starting. Starting it returns at
/// once while the databases are not ready, the check is asked only after they are, and a check that throws (the public guide host being
/// down) is logged and does not end the service. Whether a check is opt-in and weekly is the check service's own claim
/// (<c>GuideUpdateCheckServiceTests</c>).
/// </summary>
public sealed class TheGuideUpdateCheckNeverBlocksStartTests
{
    private sealed class Gate : IDelunoStartupGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => _ready.Task.IsCompletedSuccessfully;
        public IReadOnlyList<string> PendingDatabases => IsReady ? [] : ["jobs"];
        public IReadOnlyDictionary<string, string> FailedDatabases => new Dictionary<string, string>();
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _ready.Task.WaitAsync(timeout, cancellationToken);
        public void MarkReady(string databaseName) => _ready.TrySetResult();
        public void MarkFailed(string databaseName, Exception exception) { }
    }

    private sealed class Check(bool fail) : IGuideUpdateCheckService
    {
        private int _asked;
        public int Asked => Volatile.Read(ref _asked);

        public Task<GuideUpdateCheckState> RunIfDueAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asked);
            if (fail) throw new HttpRequestException("The public guide host did not answer.");
            return Task.FromResult(new GuideUpdateCheckState(false, null, null, "disabled", null, null, DateTimeOffset.UtcNow));
        }

        public Task<GuideUpdateCheckState> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GuideUpdateCheckState> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GuideUpdateCheckState> CheckNowAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<bool> WaitAsync(Func<bool> done)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (done()) return true;
            await Task.Delay(25); // reason: the service runs on its own thread with no signal to await; bounded to five seconds
        }

        return done();
    }

    [Fact]
    public async Task Starting_returns_at_once_and_the_check_is_asked_only_once_the_databases_are_ready()
    {
        var gate = new Gate();
        var check = new Check(fail: false);
        var service = new GuideUpdateCheckHostedService(check, gate, NullLogger<GuideUpdateCheckHostedService>.Instance);

        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(150); // reason: gives a wrongly un-gated service time to ask; there is nothing to await
            Assert.Equal(0, check.Asked);

            gate.MarkReady("all");
            Assert.True(await WaitAsync(() => check.Asked >= 1), "The check was not asked once the databases were ready.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_check_that_throws_because_the_guide_host_is_down_does_not_end_the_service()
    {
        var gate = new Gate();
        gate.MarkReady("all");
        var check = new Check(fail: true);
        var service = new GuideUpdateCheckHostedService(check, gate, NullLogger<GuideUpdateCheckHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitAsync(() => check.Asked >= 1));
            await Task.Delay(100); // reason: gives a service that died on the exception time to finish; there is nothing to await
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Null(service.ExecuteTask.Exception);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
