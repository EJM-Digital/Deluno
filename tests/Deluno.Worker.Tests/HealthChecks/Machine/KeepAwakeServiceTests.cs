using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Machine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Machine;

/// <summary>
/// The Keep awake button (#1098): every half minute, while the setting is on, Deluno tells Windows the computer is in use.
/// The Windows check and the Windows call are passed in, so every branch runs on any machine.
/// </summary>
public sealed class KeepAwakeServiceTests
{
    private const uint SystemRequired = 1;
    private static readonly TimeSpan HalfAMinute = TimeSpan.FromSeconds(30);

    private sealed class Script
    {
        public int Reads;
        public readonly SemaphoreSlim ReadHappened = new(0);
        public Func<int, CancellationToken, Task<bool>> Answer = (_, _) => Task.FromResult(true);

        public IPlatformSettingsRepository Repository()
        {
            var mock = new Mock<IPlatformSettingsRepository>();
            mock.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
            {
                var read = Interlocked.Increment(ref Reads);
                try
                {
                    return FakePlatformSettingsRepository.Default() with { KeepComputerAwake = await Answer(read, token) };
                }
                finally
                {
                    ReadHappened.Release();
                }
            });
            return mock.Object;
        }
    }

    private static Func<uint, uint> Record(List<uint> calls) => flags =>
    {
        lock (calls) { calls.Add(flags); }
        return 0;
    };

    private static async Task ReadsAsync(Script script, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(await script.ReadHappened.WaitAsync(Wait.DefaultLimit), "the service to read its setting");
        }
    }

    [Fact]
    public async Task Off_Windows_it_does_nothing_and_never_reads_the_setting()
    {
        var script = new Script();
        var calls = new List<uint>();
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, new FakeTimeProvider(), () => false, Record(calls));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, script.Reads);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task With_the_setting_on_it_asks_Windows_to_stay_awake_at_start_and_on_every_half_minute()
    {
        var script = new Script();
        var calls = new List<uint>();
        var clock = new FakeTimeProvider();
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, clock, () => true, Record(calls));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await ReadsAsync(script, 1);
            clock.Advance(HalfAMinute);
            await ReadsAsync(script, 1);
            clock.Advance(HalfAMinute);
            await ReadsAsync(script, 1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await Wait.UntilAsync(() => calls.Count == 3, "three requests, one per read");
        Assert.All(calls, flags => Assert.Equal(SystemRequired, flags));
    }

    [Fact]
    public async Task With_the_setting_off_Windows_is_never_asked_and_switching_it_on_takes_hold_at_the_next_tick()
    {
        var script = new Script { Answer = (read, _) => Task.FromResult(read >= 2) };
        var calls = new List<uint>();
        var clock = new FakeTimeProvider();
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, clock, () => true, Record(calls));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await ReadsAsync(script, 1);
            Assert.Empty(calls);
            clock.Advance(HalfAMinute);
            await ReadsAsync(script, 1);
            await Wait.UntilAsync(() => calls.Count == 1, "the request once the setting is on");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_is_skipped_and_the_loop_goes_on()
    {
        var script = new Script
        {
            Answer = (read, _) => read == 1 ? throw new IOException("the settings file is being written") : Task.FromResult(true)
        };
        var calls = new List<uint>();
        var clock = new FakeTimeProvider();
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, clock, () => true, Record(calls));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await ReadsAsync(script, 1);
            clock.Advance(HalfAMinute);
            await ReadsAsync(script, 1);
            await Wait.UntilAsync(() => calls.Count == 1, "the request after the failed read");
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopping_Deluno_while_it_reads_the_setting_ends_the_loop_without_logging_a_failure()
    {
        var script = new Script
        {
            Answer = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token); // reason: stands for a read that never answers; the stop cancels it
                return true;
            }
        };
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, new FakeTimeProvider(), () => true, _ => 0u);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => script.Reads == 1, "the first read to start");
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Stopping_Deluno_while_it_waits_for_the_next_tick_ends_the_loop()
    {
        var script = new Script { Answer = (_, _) => Task.FromResult(false) };
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, new FakeTimeProvider(), () => true, _ => 0u);

        await service.StartAsync(CancellationToken.None);
        await ReadsAsync(script, 1);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Built_with_only_its_two_services_it_uses_the_real_machine_and_the_real_clock()
    {
        var script = new Script { Answer = (_, _) => Task.FromResult(false) };
        var service = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance, isWindows: () => true);
        var elsewhere = new KeepAwakeService(script.Repository(), NullLogger<KeepAwakeService>.Instance);
        await elsewhere.StartAsync(CancellationToken.None);
        await elsewhere.StopAsync(CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await ReadsAsync(script, 1);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public void It_checks_every_thirty_seconds()
    {
        Assert.Equal(HalfAMinute, KeepAwakeService.Interval);
    }
}
