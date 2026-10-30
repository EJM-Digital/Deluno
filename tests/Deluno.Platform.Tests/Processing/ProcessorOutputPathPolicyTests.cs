using Deluno.Platform.Processing;

namespace Deluno.Platform.Tests.Processing;

public sealed class ProcessorOutputPathPolicyTests
{
    [Fact]
    public void Accepts_a_file_below_the_configured_output_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-processor-output");
        var output = Path.Combine(root, "Dune Part Two", "Dune Part Two.mkv");

        Assert.True(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(output, root));
    }

    [Fact]
    public void Rejects_a_sibling_folder_with_a_shared_prefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "deluno-processor-output");
        var output = Path.Combine(parent + "-untrusted", "Dune Part Two.mkv");

        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(output, parent));
    }

    [Fact]
    public void A_report_that_names_no_usable_file_gets_no_output_path()
    {
        Assert.Null(ProcessorOutputPathPolicy.OutputPathFromFiles(null));
        Assert.Null(ProcessorOutputPathPolicy.OutputPathFromFiles([]));
        Assert.Null(ProcessorOutputPathPolicy.OutputPathFromFiles(["", "   "]));
    }

    [Fact]
    public void One_named_file_is_the_output_path_itself_with_whitespace_entries_ignored()
    {
        var file = Path.Combine(Path.GetTempPath(), "deluno-out", "Dune Part Two.mkv");

        Assert.Equal(file, ProcessorOutputPathPolicy.OutputPathFromFiles(["  ", $"  {file}  ", ""]));
    }

    [Fact]
    public void Several_named_files_get_the_deepest_folder_that_holds_them_all()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-out");
        var sameFolder = ProcessorOutputPathPolicy.OutputPathFromFiles([Path.Combine(root, "Show", "S01E01.mkv"), Path.Combine(root, "Show", "S01E02.mkv")]);
        var siblings = ProcessorOutputPathPolicy.OutputPathFromFiles([Path.Combine(root, "Show", "S01", "a.mkv"), Path.Combine(root, "Show", "S02", "b.mkv"), Path.Combine(root, "Show", "extras", "c.mkv")]);
        var upperCase = ProcessorOutputPathPolicy.OutputPathFromFiles([Path.Combine(root, "Show", "a.mkv"), Path.Combine(root.ToUpperInvariant(), "Show", "b.mkv")]);

        Assert.Equal(Path.Combine(root, "Show"), sameFolder);
        Assert.Equal(Path.Combine(root, "Show"), siblings);
        Assert.Equal(Path.Combine(root, "Show").ToLowerInvariant(), upperCase!.ToLowerInvariant());
    }

    [Fact]
    public void Files_on_different_drives_have_no_common_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // reason: drive letters exist only on Windows; elsewhere every path shares the root
        }

        Assert.Null(ProcessorOutputPathPolicy.OutputPathFromFiles([@"C:\out\first.mkv", @"D:\out\second.mkv"]));
    }

    [Fact]
    public void Rejects_missing_paths()
    {
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(null, "C:\\processed"));
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary("C:\\processed\\movie.mkv", null));
    }
}
