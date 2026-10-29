using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Coverage register, area services: the machine telemetry sampler takes a CPU, memory and disk reading on a fixed one-minute cadence
/// and stores it for the charts, prunes readings older than 48 hours once an hour, and a reading that fails is only a gap in the chart:
/// the sampler goes on with the next minute.
/// </summary>
public sealed class MachineTelemetrySamplerCadenceTests
{
    private sealed class ScriptedProbe(Func<int, MachineTelemetrySample> read) : IMachineProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public MachineTelemetrySample Read(string? volumePath) => read(Interlocked.Increment(ref _calls));
    }

    private static MachineTelemetrySample Sample(DateTimeOffset at) => new(at, 12.5, 400_000_000, 8_000_000_000, 10, 20, 5, 30, 40);

    private static (MachineTelemetrySampler Sampler, Mock<IMachineTelemetryRepository> Repository) Build(ScriptedProbe probe, FakeTimeProvider time)
    {
        var repository = new Mock<IMachineTelemetryRepository>();
        repository.Setup(item => item.RecordSampleAsync(It.IsAny<MachineTelemetrySample>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        repository.Setup(item => item.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var services = new ServiceCollection()
            .AddSingleton(repository.Object)
            .AddSingleton(Mock.Of<ILibrariesRepository>())
            .BuildServiceProvider();

        return (new MachineTelemetrySampler(
            NullLogger<MachineTelemetrySampler>.Instance, services.GetRequiredService<IServiceScopeFactory>(), probe, time), repository);
    }

    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, Func<bool> done, TimeSpan limit)
    {
        var step = TimeSpan.FromSeconds(30);
        for (var moved = TimeSpan.Zero; moved < limit; moved += step)
        {
            if (done()) return true;
            time.Advance(step);
            await Task.Delay(10); // reason: lets the background service run after each fake-clock step; it has no signal to await, and the loop is bounded by limit
        }

        return done();
    }

    [Fact]
    public async Task A_reading_is_stored_at_start_and_then_once_a_minute_and_readings_older_than_48_hours_are_pruned_once_an_hour()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var probe = new ScriptedProbe(_ => Sample(time.GetUtcNow()));
        var (sampler, repository) = Build(probe, time);
        var prunes = new List<(DateTimeOffset At, DateTimeOffset Cutoff)>();
        repository.Setup(item => item.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset cutoff, CancellationToken _) => { prunes.Add((time.GetUtcNow(), cutoff)); return 0; });

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, () => probe.Calls >= 1, TimeSpan.FromSeconds(30)), "No reading at start.");

            Assert.True(await AdvanceUntilAsync(time, () => probe.Calls >= 6, TimeSpan.FromMinutes(6)), "Readings did not follow one a minute.");
            await Task.Delay(100); // reason: the store call after the sixth reading runs on the service thread; no signal to await, bounded
            Assert.Equal(6, probe.Calls); // one at start plus one for each of five minutes, not more
            repository.Verify(item => item.RecordSampleAsync(It.IsAny<MachineTelemetrySample>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
            repository.Verify(item => item.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);

            Assert.True(await AdvanceUntilAsync(time, () => probe.Calls >= 66, TimeSpan.FromHours(1)), "Readings stopped before the hour was up.");
            await Task.Delay(100); // reason: as above
            repository.Verify(item => item.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            Assert.All(prunes, pair => Assert.Equal(TimeSpan.FromHours(48), pair.At - pair.Cutoff));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_reading_that_fails_is_a_gap_in_the_chart_and_the_next_minute_is_sampled_again()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var probe = new ScriptedProbe(call => call == 2 ? throw new InvalidOperationException("The machine refused a reading.") : Sample(time.GetUtcNow()));
        var (sampler, repository) = Build(probe, time);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, () => probe.Calls >= 4, TimeSpan.FromMinutes(5)), "The sampler stopped after a failed reading.");
            await Task.Delay(100); // reason: as above
            repository.Verify(item => item.RecordSampleAsync(It.IsAny<MachineTelemetrySample>(), It.IsAny<CancellationToken>()), Times.Exactly(probe.Calls - 1));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }
}
