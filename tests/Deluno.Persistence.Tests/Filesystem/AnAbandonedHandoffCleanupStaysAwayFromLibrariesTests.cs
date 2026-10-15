using Deluno.Filesystem;
using Deluno.Libraries.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1325: cleaning up an abandoned hand-off never removes anything while two library folders overlap, never when the
/// output folder is, holds or sits inside a library; a blank folder name is ignored.
/// </summary>
public sealed class AnAbandonedHandoffCleanupStaysAwayFromLibrariesTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), $"deluno-handoff-libs-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
    }

    private string Scrap(params string[] parts)
    {
        var folder = Directory.CreateDirectory(Path.Combine([sandbox, .. parts])).FullName;
        var file = Path.Combine(folder, "film.mkv.partial");
        File.WriteAllText(file, "scrap");
        return file;
    }

    private static LibraryItem Library(string id, string root, string? downloads = null, string? output = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            id, id, "movies", "main", root, downloads, null, null, null,
            true, true, "direct", null, output, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static int Clean(string output, IReadOnlyList<LibraryItem>? libraries, params string[] names)
        => ProcessorHandoffOutputCleanup.CleanupAbandonedHandoffOutput(names, output, _ => false, libraries);

    [Fact]
    public void Scraps_go_when_no_library_is_anywhere_near_the_output_folder()
    {
        var scrap = Scrap("output", "Handoff");
        var libraries = new[] { Library("a", Path.Combine(sandbox, "library")) };

        Assert.Equal(1, Clean(Path.Combine(sandbox, "output"), libraries, "Handoff", "", "  "));
        Assert.False(File.Exists(scrap));
    }

    [Fact]
    public void Nothing_is_removed_while_two_libraries_have_folders_that_overlap()
    {
        var scrap = Scrap("output", "Handoff");
        var libraries = new[]
        {
            Library("a", Path.Combine(sandbox, "library"), output: Path.Combine(sandbox, "output")),
            Library("b", Path.Combine(sandbox, "output", "inside")),
        };

        Assert.Equal(0, Clean(Path.Combine(sandbox, "output"), libraries, "Handoff"));
        Assert.True(File.Exists(scrap));
    }

    [Fact]
    public void Nothing_is_removed_when_the_output_folder_holds_a_library()
    {
        var scrap = Scrap("output", "Handoff");
        var libraries = new[] { Library("a", Path.Combine(sandbox, "output", "films")) };

        Assert.Equal(0, Clean(Path.Combine(sandbox, "output"), libraries, "Handoff"));
        Assert.True(File.Exists(scrap));
    }

    [Fact]
    public void Nothing_is_removed_when_the_output_folder_is_inside_a_library()
    {
        var scrap = Scrap("library", "output", "Handoff");
        var libraries = new[] { Library("a", Path.Combine(sandbox, "library")) };

        Assert.Equal(0, Clean(Path.Combine(sandbox, "library", "output"), libraries, "Handoff"));
        Assert.True(File.Exists(scrap));
    }
}
