using System.Runtime.InteropServices;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #819/#926's shared check: whether a download's file and the library's copy
/// still share one set of file data, read from the real filesystem rather
/// than guessed from configuration.
/// </summary>
public sealed class HardlinkInspectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-hardlink-test-").FullName;
    private readonly HardlinkInspector _inspector = new(new FileIdentity());

    [Fact]
    public void Two_paths_still_hardlinked_report_true()
    {
        var library = Path.Combine(_root, "library.mkv");
        File.WriteAllText(library, "one set of file data");
        var download = Path.Combine(_root, "download.mkv");
        Assert.True(TryCreateHardlink(library, download));

        var result = _inspector.StillLinked(download, library);

        Assert.True(result);
    }

    [Fact]
    public void A_download_that_is_its_own_separate_copy_reports_false()
    {
        var library = Path.Combine(_root, "library2.mkv");
        File.WriteAllText(library, "library copy");
        var download = Path.Combine(_root, "download2.mkv");
        File.WriteAllText(download, "a completely separate copy, not linked");

        var result = _inspector.StillLinked(download, library);

        Assert.False(result);
    }

    [Fact]
    public void A_deleted_library_copy_reports_false_not_cannot_say()
    {
        var library = Path.Combine(_root, "gone.mkv");
        var download = Path.Combine(_root, "download3.mkv");
        File.WriteAllText(download, "still on disk");
        // library was never created - stands in for "upgraded or removed".

        var result = _inspector.StillLinked(download, library);

        Assert.False(result);
    }

    [Fact]
    public void A_missing_download_file_cannot_say_rather_than_claiming_linked_or_not()
    {
        var library = Path.Combine(_root, "library4.mkv");
        File.WriteAllText(library, "still there");
        var download = Path.Combine(_root, "vanished.mkv");
        // download was never created - a different problem (#927).

        var result = _inspector.StillLinked(download, library);

        Assert.Null(result);
    }

    [Fact]
    public void Blank_paths_cannot_say()
    {
        Assert.Null(_inspector.StillLinked(null, @"C:\library.mkv"));
        Assert.Null(_inspector.StillLinked(@"C:\download.mkv", ""));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A minimal, test-only hardlink creator mirroring ImportPipelineService's own.</summary>
    private static bool TryCreateHardlink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHardLink(newPath, existingPath, IntPtr.Zero);
        }

        return link(existingPath, newPath) == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int link(string oldpath, string newpath);
}
