using Deluno.Filesystem;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Restart recovery on real files: a marker another program really holds open, and a library whose drive is not
/// mounted. The injected-failure variations live in ImportRestartRecoveryVariationsTests; these use the real
/// file system the way a media server, an antivirus scan or an unplugged drive does.
/// </summary>
public sealed class ImportRestartRecoveryOnRealFilesTests
{
    [Fact]
    public async Task A_marker_another_program_really_holds_open_is_left_alone_and_recovered_on_the_next_pass()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);

        ImportRestartRecoveryReport held;
        // Another program has the marker open exclusively, as a media server or a sync client would.
        using (new FileStream(scene.MarkerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held = await rig.CreateService().RecoverAsync(CancellationToken.None);

            // Not taken for damaged: the staging file (the only copy of a move import) and the marker both stay.
            Assert.True(File.Exists(scene.StagingPath));
            Assert.True(File.Exists(scene.MarkerPath));
        }

        Assert.Equal(new ImportRestartRecoveryReport(0, 0, 1), held);
        Assert.Empty(rig.Pipeline.Imports);
        Assert.Empty(await rig.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.StagingPath));

        // Released, the very next pass finishes it.
        var released = await rig.CreateService().RecoverAsync(CancellationToken.None);
        Assert.Equal(1, released.Recovered);
        Assert.Single(rig.Pipeline.Imports);
        Assert.False(File.Exists(scene.MarkerPath));
    }

    [Fact]
    public async Task A_library_whose_drive_is_not_mounted_is_skipped_and_the_others_are_still_recovered()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("move", markerText: null);

        var gone = Path.Combine(Path.GetDirectoryName(rig.Root)!, "unplugged");
        Directory.CreateDirectory(gone);
        await rig.Libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Unplugged", MediaType: "movies", Purpose: "Extra", RootPath: gone,
                DownloadsPath: rig.DownloadsPath, QualityProfileId: null, ImportWorkflow: "standard",
                ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null, AutoSearchEnabled: false, MissingSearchEnabled: false,
                UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 24, MaxItemsPerRun: 25),
            CancellationToken.None);
        // The drive goes away after Deluno was told about it.
        Directory.Delete(gone);
        Assert.False(Directory.Exists(gone));

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(new ImportRestartRecoveryReport(1, 0, 0), report);
        Assert.Single(rig.Pipeline.Imports);
        // Nothing was created in the place the drive should be, and the mounted library was not disturbed.
        Assert.False(Directory.Exists(gone));
        Assert.True(Directory.Exists(rig.Root));
    }
}
