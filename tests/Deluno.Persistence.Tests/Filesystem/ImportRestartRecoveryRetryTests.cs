using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Golden-VM finds (#1525, #1526). A failed import on a share that dropped could not delete its half copy and still deleted its
/// restart mark, so nothing ever cleared the file. And a mark skipped because another program held it was retried only at the
/// next start. Real temp folders throughout.
/// </summary>
public sealed class ImportRestartRecoveryRetryTests
{
    [Fact]
    public async Task A_failed_move_whose_half_copy_will_not_delete_keeps_its_mark_and_recovery_clears_both_once_the_folder_is_back()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);

        // The share is down: the delete is refused.
        ImportPipelineService.RollBackPartialImport(
            scene.SourcePath, scene.DestinationPath, scene.StagingPath, null, restoreSourceOnFailure: false,
            deleteStagingFile: _ => throw new IOException("The handle is invalid."));

        Assert.True(File.Exists(scene.StagingPath));
        Assert.True(File.Exists(scene.MarkerPath), "the mark must stay while the half copy is still there");

        // The folder is back: the next recovery removes the file, then the mark, and never imports or touches the source.
        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(scene.StagingPath));
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.Empty(rig.Pipeline.Imports);
        Assert.Single(await Lines(rig, "half-copied"));
    }

    [Fact]
    public async Task A_rollback_that_deletes_the_half_copy_still_removes_the_mark()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);

        ImportPipelineService.RollBackPartialImport(scene.SourcePath, scene.DestinationPath, scene.StagingPath, null, restoreSourceOnFailure: false);

        Assert.False(File.Exists(scene.StagingPath));
        Assert.False(File.Exists(scene.MarkerPath));
    }

    [Fact]
    public async Task A_half_copy_that_is_still_refused_stays_with_its_mark_and_is_not_written_about()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null);
        ImportPipelineService.RollBackPartialImport(
            scene.SourcePath, scene.DestinationPath, scene.StagingPath, null, false,
            deleteStagingFile: _ => throw new IOException("The handle is invalid."));

        // Held open: on Windows the delete is refused for real; elsewhere the file is read-only-ish via a held mark instead.
        using var held = new FileStream(scene.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        for (var pass = 0; pass < 3; pass++)
        {
            var report = await rig.CreateService().RecoverAsync(CancellationToken.None);
            Assert.Equal(0, report.Recovered);
        }

        Assert.True(File.Exists(scene.StagingPath));
        Assert.Empty(await Lines(rig, "half-copied"));
    }

    [Fact]
    public async Task A_mark_held_open_is_skipped_then_cleared_by_the_next_scheduled_pass_without_a_restart()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: rig.ValidMarkerText());
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        // The schedule lives in the real job store, which reads the same clock the test moves.
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            app.Services.GetRequiredService<IJobQueueRepository>(),
            new ConfigurationBuilder().Build(),
            clock);

        var held = new FileStream(scene.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            await planner.RunImportRestartRecoveryAsync(rig.CreateService(), CancellationToken.None);
            Assert.True(File.Exists(scene.MarkerPath), "held: left for a later pass");
            Assert.Empty(rig.Pipeline.Imports);

            // Inside the interval the pass does not run again.
            await held.DisposeAsync();
            await planner.RunImportRestartRecoveryAsync(rig.CreateService(), CancellationToken.None);
            Assert.Empty(rig.Pipeline.Imports);
        }
        finally
        {
            await held.DisposeAsync();
        }

        // The other program let go; the next scheduled pass finishes it.
        clock.Advance(SystemTasks.IntervalFor(SystemTasks.ImportRestartRecovery) + TimeSpan.FromSeconds(1));
        await planner.RunImportRestartRecoveryAsync(rig.CreateService(), CancellationToken.None);

        Assert.Single(rig.Pipeline.Imports);
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.False(File.Exists(scene.StagingPath));
        Assert.True(File.Exists(scene.SourcePath));
        Assert.Single(await Lines(rig, "interrupted by a restart"));

        // A further pass finds nothing and writes nothing more.
        clock.Advance(TimeSpan.FromMinutes(10));
        await planner.RunImportRestartRecoveryAsync(rig.CreateService(), CancellationToken.None);
        Assert.Single(rig.Pipeline.Imports);
        Assert.Single(await Lines(rig, "interrupted by a restart"));
    }

    private static async Task<IReadOnlyList<string>> Lines(ImportRestartRecoveryKeepsMediaTests.RestartRig rig, string containing)
        => (await ((IActivityFeedRepository)rig.Jobs).ListActivityAsync(100, null, null, CancellationToken.None))
            .Where(item => item.Message.Contains(containing, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Message)
            .ToArray();
}
