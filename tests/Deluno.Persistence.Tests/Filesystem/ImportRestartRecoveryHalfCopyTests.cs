using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// A real kill of Deluno in the middle of copying a large file (seen on the golden VM, 31 Oct 2026): the copy writes to
/// a temporary file beside the staging name before it moves it into place, so the kill leaves that half-written copy,
/// the restart mark, and the untouched original. Recovery imports the original again; the half-written copy, which is a
/// redundant partial of a file that is still there, must not stay behind for ever (it was 30 GB on the VM).
/// </summary>
public sealed class ImportRestartRecoveryHalfCopyTests
{
    [Fact]
    public async Task A_half_written_copy_a_kill_left_beside_the_staging_name_is_removed_when_the_import_is_recovered()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        // The kill came while the copy was still being written: no staging file yet, the original untouched.
        var scene = rig.Lay("copy", markerText: null, stagingPresent: false);
        var half = scene.StagingPath + ".deluno-copy-halfwritten.tmp";
        await File.WriteAllBytesAsync(half, scene.Bytes.Take(1000).ToArray());

        var report = await rig.CreateService().RecoverAsync(CancellationToken.None);

        Assert.Equal(1, report.Recovered);
        Assert.False(File.Exists(half), "the half-written copy is left behind");
        Assert.False(File.Exists(scene.MarkerPath));
        Assert.True(File.Exists(scene.SourcePath), "the original is the media; recovery must not touch it");
        Assert.Equal(scene.Bytes, await File.ReadAllBytesAsync(scene.SourcePath));
        Assert.Single(rig.Pipeline.Imports);
    }

    [Fact]
    public async Task A_half_written_copy_is_kept_when_the_original_it_was_copied_from_is_gone()
    {
        using var rig = await ImportRestartRecoveryKeepsMediaTests.RestartRig.CreateAsync();
        var scene = rig.Lay("copy", markerText: null, stagingPresent: false);
        File.Delete(scene.SourcePath);
        var half = scene.StagingPath + ".deluno-copy-halfwritten.tmp";
        await File.WriteAllBytesAsync(half, scene.Bytes.Take(1000).ToArray());

        await rig.CreateService().RecoverAsync(CancellationToken.None);

        // Nothing can say the partial is redundant, so a person's media is never deleted on a guess.
        Assert.True(File.Exists(half));
    }
}
