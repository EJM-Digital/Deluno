using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The stored history behind the dashboard's download-speed chart. It is the
/// only measurement Deluno keeps rather than a count, so the things worth
/// pinning are that a reading survives, that the window is a window, and that
/// the table cannot grow without bound.
/// </summary>
public sealed class DownloadThroughputRepositoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

    private static async Task<TestStorage> StorageAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now)),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }

    [Fact]
    public async Task A_reading_survives_and_comes_back_intact()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        await repository.RecordSampleAsync(new DownloadThroughputSample(Now, 12.5, 3), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(12.5, sample.SpeedMbps);
        Assert.Equal(3, sample.ActiveCount);
        Assert.Equal(Now, sample.CapturedUtc);
    }

    /// <summary>
    /// Upload is a first-class reading now that Deluno holds files back so a
    /// site's sharing rule can be met (#288, #289). A chart that can only draw
    /// download cannot answer "am I actually seeding?".
    /// </summary>
    [Fact]
    public async Task Both_directions_survive_the_round_trip()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        await repository.RecordSampleAsync(new DownloadThroughputSample(Now, 12.5, 3, UploadMbps: 0.75), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(12.5, sample.SpeedMbps);
        Assert.Equal(0.75, sample.UploadMbps);
    }

    /// <summary>
    /// Readings taken before upload was measured come back as zero rather than
    /// failing to parse. That zero is the truth about them — Deluno genuinely
    /// did not know — and a chart drawing a flat line at the left is honest in
    /// a way a backfilled guess would not be.
    /// </summary>
    [Fact]
    public async Task A_reading_from_before_upload_was_measured_still_reads()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        await repository.RecordSampleAsync(new DownloadThroughputSample(Now, 4, 1), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(0, sample.UploadMbps);
    }

    [Fact]
    public async Task Readings_come_back_oldest_first_so_a_chart_reads_left_to_right()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        // Recorded out of order on purpose.
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddMinutes(-1), 3, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddMinutes(-5), 1, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddMinutes(-3), 2, 1), CancellationToken.None);

        var samples = await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None);

        Assert.Equal([1d, 2d, 3d], samples.Select(sample => sample.SpeedMbps));
    }

    [Fact]
    public async Task Only_readings_inside_the_window_are_returned()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-10), 99, 9), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddMinutes(-10), 5, 1), CancellationToken.None);

        var samples = await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None);

        Assert.Equal(5, Assert.Single(samples).SpeedMbps);
    }

    [Fact]
    public async Task Re_recording_the_same_instant_replaces_rather_than_duplicates()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        // A restart that re-samples the same second must not put two points on
        // one x — the chart would show a vertical line that never happened.
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now, 4, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now, 8, 2), CancellationToken.None);

        var sample = Assert.Single(await repository.ListSamplesAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(8, sample.SpeedMbps);
        Assert.Equal(2, sample.ActiveCount);
    }

    [Fact]
    public async Task Pruning_drops_what_is_past_retention_and_keeps_the_rest()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-72), 1, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-60), 2, 1), CancellationToken.None);
        await repository.RecordSampleAsync(new DownloadThroughputSample(Now.AddHours(-1), 3, 1), CancellationToken.None);

        var removed = await repository.PruneAsync(Now.AddHours(-48), CancellationToken.None);

        Assert.Equal(2, removed);
        var remaining = await repository.ListSamplesAsync(Now.AddHours(-96), CancellationToken.None);
        Assert.Equal(3, Assert.Single(remaining).SpeedMbps);
    }

    [Fact]
    public async Task An_install_that_has_never_sampled_returns_nothing_rather_than_failing()
    {
        using var storage = await StorageAsync();
        var repository = new SqliteDownloadThroughputRepository(storage.Factory);

        Assert.Empty(await repository.ListSamplesAsync(Now.AddHours(-6), CancellationToken.None));
        Assert.Equal(0, await repository.PruneAsync(Now.AddHours(-48), CancellationToken.None));
    }
}
