using System.ComponentModel;
using System.Runtime.InteropServices;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The sharing footprint (#288) judged against real folders and a real hard link in a temp folder, not against typed-out paths:
/// a download and its library copy that really are one set of file data cost nothing; two mounts cannot share one copy; the same
/// volume without links says what to change; a folder that does not exist makes no claim.
/// </summary>
public sealed class SharingFootprintOnRealFilesTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("deluno-footprint-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { /* a scanner may hold a file for a moment */ }
    }

    private string Folder(params string[] parts)
        => Directory.CreateDirectory(Path.Combine([root, .. parts])).FullName;

    private static void Link(string existing, string link)
    {
        var made = OperatingSystem.IsWindows()
            ? CreateHardLink(link, existing, IntPtr.Zero)
            : LinkPosix(existing, link) == 0;
        if (!made)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkPosix(string oldpath, string newpath);

    [Fact]
    public void A_download_really_hard_linked_into_the_library_on_one_volume_costs_nothing()
    {
        var download = Path.Combine(Folder("downloads"), "Sintel.mkv");
        File.WriteAllBytes(download, [1, 2, 3, 4]);
        var imported = Path.Combine(Folder("library", "Sintel"), "Sintel.mkv");
        Link(download, imported);

        // The two names are one set of file data: a write through one shows in the other.
        File.AppendAllText(imported, "more");
        Assert.Equal(File.ReadAllBytes(imported), File.ReadAllBytes(download));

        Assert.True(SharingFootprint.SharesOneCopy(download, Folder("library"), useHardlinks: true));
        Assert.Null(SharingFootprint.Describe(download, Folder("library"), useHardlinks: true));
    }

    [Fact]
    public void A_library_on_another_drive_than_the_real_download_cannot_share_one_copy_and_the_note_names_both()
    {
        var download = Path.Combine(Folder("downloads"), "Sintel.mkv");
        File.WriteAllBytes(download, [1]);
        // A drive letter that is not the one the temp folder is on, so the answer is "another drive" on Windows and Linux alike.
        var downloadDrive = SharingFootprint.WindowsRootOf(download) is { } letter ? letter[0] : 'Q';
        var otherDrive = downloadDrive == 'Z' ? 'Y' : 'Z';
        var library = $@"{otherDrive}:\Media\Movies";

        Assert.False(SharingFootprint.SharesOneCopy(download, library, useHardlinks: true));
        var note = SharingFootprint.Describe(download, library, useHardlinks: true);

        Assert.NotNull(note);
        Assert.Contains($"{otherDrive}:", note);
        Assert.Contains("takes its own space", note);
    }

    [Fact]
    public void The_same_volume_without_single_copy_links_says_what_to_change()
    {
        var download = Path.Combine(Folder("downloads"), "Sintel.mkv");
        File.WriteAllBytes(download, [1]);

        Assert.False(SharingFootprint.SharesOneCopy(download, Folder("library"), useHardlinks: false));
        Assert.Contains("Keep seeding without a second copy", SharingFootprint.Describe(download, Folder("library"), useHardlinks: false));
    }

    [Fact]
    public void A_missing_or_blank_folder_makes_no_claim()
    {
        var download = Path.Combine(Folder("downloads"), "Sintel.mkv");
        File.WriteAllBytes(download, [1]);

        Assert.False(SharingFootprint.SharesOneCopy(download, "  ", useHardlinks: true));
        Assert.Null(SharingFootprint.Describe(download, null, useHardlinks: true));
        Assert.Null(SharingFootprint.Describe("", Folder("library"), useHardlinks: true));
    }
}
