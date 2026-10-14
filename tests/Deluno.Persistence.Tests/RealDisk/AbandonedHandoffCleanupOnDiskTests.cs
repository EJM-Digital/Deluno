using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Cleaning up an abandoned hand-off on a real temp folder: only what the processor was still writing goes, and a folder
/// name that climbs out of the processor's output root, or names the root itself, is never cleaned.
/// </summary>
public sealed class AbandonedHandoffCleanupOnDiskTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "deluno-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData(@"..\elsewhere")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("nested/../../elsewhere")]
    [InlineData("ABSOLUTE")]
    public void A_hand_off_folder_name_that_climbs_out_of_the_output_root_or_names_the_root_is_never_cleaned(string folderName)
    {
        var outputRoot = Directory.CreateDirectory(Path.Combine(_parent, "output")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName;
        var elsewhereFile = Path.Combine(elsewhere, "keep.mkv.partial");
        File.WriteAllText(elsewhereFile, "not ours");
        var rootFile = Path.Combine(outputRoot, "root.mkv.partial");
        File.WriteAllText(rootFile, "the output root itself");
        var name = folderName == "ABSOLUTE" ? elsewhere : folderName;

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput([name], outputRoot, path => false);

        Assert.Equal(0, removed);
        Assert.Equal("not ours", File.ReadAllText(elsewhereFile));
        Assert.Equal("the output root itself", File.ReadAllText(rootFile));
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public void The_hand_offs_own_scraps_are_removed_but_a_complete_file_and_its_folder_stay()
    {
        var outputRoot = Directory.CreateDirectory(Path.Combine(_parent, "output")).FullName;
        var handoff = Directory.CreateDirectory(Path.Combine(outputRoot, "Interstellar.2014.WEB.1080p-DELUNO")).FullName;
        var complete = Path.Combine(handoff, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        File.WriteAllText(complete, "finished output");
        var scrap = Path.Combine(handoff, "Interstellar.2014.WEB.1080p-DELUNO.mkv.partial");
        File.WriteAllText(scrap, "an older attempt");

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"], outputRoot, path => string.Equals(path, complete, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, removed);
        Assert.Equal("finished output", File.ReadAllText(complete));
        Assert.False(File.Exists(scrap));
        Assert.True(Directory.Exists(handoff));
    }
}
