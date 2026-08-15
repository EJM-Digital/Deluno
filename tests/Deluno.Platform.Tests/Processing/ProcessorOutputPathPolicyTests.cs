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
    public void Rejects_missing_paths()
    {
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(null, "C:\\processed"));
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary("C:\\processed\\movie.mkv", null));
    }
}
