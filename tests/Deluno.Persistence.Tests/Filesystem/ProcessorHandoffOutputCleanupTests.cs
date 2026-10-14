using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #953: cleaning up an abandoned hand-off's own folder must remove the
/// processor's scraps and any folder left empty by doing so, but never a
/// complete file -- that is what #921's Health row still offers a person to
/// import or delete.
/// </summary>
public sealed class ProcessorHandoffOutputCleanupTests
{
    [Fact]
    public void Scraps_and_the_now_empty_handoff_folder_are_removed()
    {
        using var root = TempRoot.Create();
        var handoffFolder = Directory.CreateDirectory(Path.Combine(root.Path, "Interstellar.2014.WEB.1080p-DELUNO")).FullName;
        var partial = Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.mkv.partial");
        File.WriteAllText(partial, "still writing");
        var nested = Directory.CreateDirectory(Path.Combine(handoffFolder, "sample")).FullName;
        File.WriteAllText(Path.Combine(nested, "sample.mkv.tmp"), "sample");

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"],
            root.Path,
            path => false);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(handoffFolder), "the folder the scraps left behind should go too, once empty");
    }

    [Fact]
    public void A_complete_file_is_kept_and_so_is_its_folder()
    {
        using var root = TempRoot.Create();
        var handoffFolder = Directory.CreateDirectory(Path.Combine(root.Path, "Interstellar.2014.WEB.1080p-DELUNO")).FullName;
        var complete = Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.mkv");
        File.WriteAllText(complete, "finished output");
        var partial = Path.Combine(handoffFolder, "Interstellar.2014.WEB.1080p-DELUNO.mkv.partial");
        File.WriteAllText(partial, "an older attempt");

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"],
            root.Path,
            path => string.Equals(path, complete, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(complete), "a complete file is #921's problem to offer, not this cleanup's to delete");
        Assert.False(File.Exists(partial));
        Assert.True(Directory.Exists(handoffFolder), "the folder holding a kept file must stay");
    }

    /// <summary>#1306: a hand-off folder name that climbs ("..") or names the output root itself is never cleaned.</summary>
    [Fact]
    public void A_folder_name_that_climbs_out_of_the_output_root_is_never_touched()
    {
        using var parent = TempRoot.Create();
        var outputRoot = Directory.CreateDirectory(Path.Combine(parent.Path, "output")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(parent.Path, "elsewhere")).FullName;
        var elsewhereFile = Path.Combine(elsewhere, "keep.mkv.partial");
        File.WriteAllText(elsewhereFile, "not ours");
        var rootFile = Path.Combine(outputRoot, "root.mkv.partial");
        File.WriteAllText(rootFile, "the output root itself");

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["../elsewhere", "..\elsewhere", "..", "."],
            outputRoot,
            path => false);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(elsewhereFile));
        Assert.True(File.Exists(rootFile));
    }

    [Fact]
    public void A_sibling_hand_offs_folder_is_never_touched()
    {
        using var root = TempRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "Interstellar.2014.WEB.1080p-DELUNO"));
        var siblingFolder = Directory.CreateDirectory(Path.Combine(root.Path, "Arrival.2016.WEB.1080p-DELUNO")).FullName;
        var siblingFile = Path.Combine(siblingFolder, "Arrival.2016.WEB.1080p-DELUNO.mkv.partial");
        File.WriteAllText(siblingFile, "unrelated");

        ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"],
            root.Path,
            path => false);

        Assert.True(File.Exists(siblingFile));
    }

    [Fact]
    public void No_matching_folder_at_all_removes_nothing()
    {
        using var root = TempRoot.Create();

        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"],
            root.Path,
            path => false);

        Assert.Equal(0, removed);
    }

    [Fact]
    public void A_missing_output_root_removes_nothing()
    {
        var removed = ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(
            ["Interstellar.2014.WEB.1080p-DELUNO"],
            Path.Combine(Path.GetTempPath(), $"deluno-missing-{Guid.NewGuid():n}"),
            path => false);

        Assert.Equal(0, removed);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; }

        private TempRoot(string path) => Path = path;

        public static TempRoot Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deluno-handoff-cleanup-{Guid.NewGuid():n}");
            Directory.CreateDirectory(path);
            return new TempRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
