using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Backup;

/// <summary>
/// #1260: the marker naming a staged restore was read with no handling. A cut-short marker named a folder that does not exist,
/// so the marker was deleted and the restore silently dropped.
/// </summary>
public sealed class AStagedRestoreIsNeverDroppedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-staged-" + Guid.NewGuid().ToString("N"));
    private string Marker => Path.Combine(_root, StagedRestore.StagingFolderName, "pending-restore.txt");
    private string Staged => Path.Combine(_root, StagedRestore.StagingFolderName, "20261014-090000");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Stage(string markerText)
    {
        Directory.CreateDirectory(Staged);
        File.WriteAllText(Path.Combine(Staged, "platform.db"), "restored");
        File.WriteAllText(Marker, markerText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_marker_keeps_the_marker_and_the_staged_folder(string text)
    {
        Stage(text);

        var applied = StagedRestore.ApplyPending(_root);

        Assert.Empty(applied);
        Assert.True(File.Exists(Marker));
        Assert.True(File.Exists(Path.Combine(Staged, "platform.db")));
    }

    [Fact]
    public void A_cut_short_marker_keeps_the_marker_and_the_staged_folder_instead_of_dropping_the_restore()
    {
        Stage(Staged[..^6]);

        var applied = StagedRestore.ApplyPending(_root);

        Assert.Empty(applied);
        Assert.True(File.Exists(Marker));
        Assert.True(File.Exists(Path.Combine(Staged, "platform.db")));
    }
}
