using Deluno.Contracts;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The stored history of how hard the machine has been working (#272).
///
/// The thing worth pinning hardest is that a *missing* reading stays missing.
/// A whole-volume figure comes from the volume itself and can be refused;
/// storing a zero for it would tell someone their drive was idle while an
/// import crawled, which is the exact question this exists to answer.
/// </summary>
public sealed class MachineTelemetryRepositoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");

    private static async Task<TestStorage> StorageAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }

    private static MachineTelemetrySample Sample(
        DateTimeOffset? capturedUtc = null,
        double cpuPercent = 12.5,
        double? diskBusyPercent = 31.5)
        => new(
            CapturedUtc: capturedUtc ?? Now,
            CpuPercent: cpuPercent,
            MemoryBytes: 512_000_000,
            TotalMemoryBytes: 66_202_787_840,
            ProcessReadBytesPerSecond: 4_719,
            ProcessWriteBytesPerSecond: 1_518,
            DiskBusyPercent: diskBusyPercent,
            DiskReadBytesPerSecond: diskBusyPercent is null ? null : 1_717_259,
            DiskWriteBytesPerSecond: diskBusyPercent is null ? null : 1_192_546);

    [Fact]
    public async Task A_reading_survives_and_comes_back_intact()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(12.5, sample.CpuPercent);
        Assert.Equal(512_000_000, sample.MemoryBytes);
        Assert.Equal(66_202_787_840, sample.TotalMemoryBytes);
        Assert.Equal(4_719, sample.ProcessReadBytesPerSecond);
        Assert.Equal(31.5, sample.DiskBusyPercent);
        Assert.Equal(1_717_259, sample.DiskReadBytesPerSecond);
    }

    /// <summary>
    /// A volume that would not give up its counters is a missing series, not a
    /// quiet one. Rounding that to zero would claim an idle drive.
    /// </summary>
    [Fact]
    public async Task A_refused_whole_disk_reading_stays_missing_rather_than_becoming_zero()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(diskBusyPercent: null), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Null(sample.DiskBusyPercent);
        Assert.Null(sample.DiskReadBytesPerSecond);
        Assert.Null(sample.DiskWriteBytesPerSecond);
        // Deluno's own I/O is read a different way and is still there, which is
        // the whole point of keeping two disk figures rather than one.
        Assert.Equal(4_719, sample.ProcessReadBytesPerSecond);
    }

    [Fact]
    public async Task The_newest_reading_is_what_the_dashboard_gets()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(Now.AddMinutes(-2), cpuPercent: 4), CancellationToken.None);
        await repository.RecordSampleAsync(Sample(Now, cpuPercent: 91), CancellationToken.None);

        var latest = await repository.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(91, latest.CpuPercent);
    }

    [Fact]
    public async Task An_install_that_has_never_sampled_has_no_latest_reading()
    {
        using var storage = await StorageAsync();

        Assert.Null(await new SqliteMachineTelemetryRepository(storage.Factory).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Re_sampling_the_same_instant_replaces_rather_than_duplicating()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(cpuPercent: 4), CancellationToken.None);
        await repository.RecordSampleAsync(Sample(cpuPercent: 88), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(88, sample.CpuPercent);
    }

    [Fact]
    public async Task Readings_come_back_oldest_first_so_a_chart_reads_left_to_right()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(Now, cpuPercent: 3), CancellationToken.None);
        await repository.RecordSampleAsync(Sample(Now.AddMinutes(-5), cpuPercent: 1), CancellationToken.None);
        await repository.RecordSampleAsync(Sample(Now.AddMinutes(-2), cpuPercent: 2), CancellationToken.None);

        var samples = await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None);

        Assert.Equal([1d, 2d, 3d], samples.Select(sample => sample.CpuPercent));
    }

    [Fact]
    public async Task Pruning_drops_only_what_is_past_retention()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteMachineTelemetryRepository(storage.Factory);

        await repository.RecordSampleAsync(Sample(Now.AddHours(-50)), CancellationToken.None);
        await repository.RecordSampleAsync(Sample(Now.AddHours(-1)), CancellationToken.None);

        Assert.Equal(1, await repository.PruneAsync(Now.AddHours(-48), CancellationToken.None));
        Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-72), CancellationToken.None));
    }

    /// <summary>
    /// Working set means nothing without the machine it is a share of, so the
    /// proportion is derived rather than stored — and stays absent where the
    /// total could not be read.
    /// </summary>
    [Fact]
    public void Memory_is_reported_as_a_share_of_the_machine_when_the_machine_is_known()
    {
        Assert.Equal(50, new MachineTelemetrySample(Now, 0, 512, 1024, 0, 0, null, null, null).MemoryPercent);
        Assert.Null(new MachineTelemetrySample(Now, 0, 512, null, 0, 0, null, null, null).MemoryPercent);
    }

    /// <summary>
    /// Rates need two points. The first reading after a restart has nothing to
    /// measure against, and extrapolating from process start would attribute a
    /// whole uptime's worth of I/O to one minute.
    /// </summary>
    [Fact]
    public void The_first_probe_reports_no_rate_rather_than_inventing_one()
    {
        var probe = new MachineProbe(new FixedTimeProvider(Now));

        var first = probe.Read(volumePath: null);

        Assert.Equal(0, first.CpuPercent);
        Assert.Equal(0, first.ProcessReadBytesPerSecond);
        Assert.Equal(0, first.ProcessWriteBytesPerSecond);
        // Memory is a level, not a rate, so it is real from the first reading.
        Assert.True(first.MemoryBytes > 0);
    }

    /// <summary>
    /// A path Deluno was never given cannot produce a whole-volume figure, and
    /// that has to be null rather than a plausible zero.
    /// </summary>
    [Fact]
    public void An_unconfigured_library_volume_yields_no_whole_disk_reading()
    {
        var sample = new MachineProbe(new FixedTimeProvider(Now)).Read(volumePath: null);

        Assert.Null(sample.DiskBusyPercent);
        Assert.Null(sample.DiskReadBytesPerSecond);
    }
}
