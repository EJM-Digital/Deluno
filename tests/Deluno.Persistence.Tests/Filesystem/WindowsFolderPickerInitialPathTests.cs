using System.Reflection;
using Xunit;

namespace Deluno.Persistence.Tests.Filesystem;

public sealed class WindowsFolderPickerInitialPathTests
{
    private static readonly Type Picker = typeof(Deluno.Filesystem.FilesystemEndpointRouteBuilderExtensions).Assembly
        .GetType("Deluno.Filesystem.WindowsFolderPicker", throwOnError: true)!;

    [Fact]
    public void The_start_folder_is_sent_with_the_wide_path_message_and_the_path_flag()
    {
        // Windows' shell: BFFM_SETSELECTIONW = WM_USER (0x400) + 103, and wParam = TRUE means lParam is a path.
        // The picker sent 0x0466 (the ANSI message) with wParam 0, so the window opened at the person's own folder (#1371).
        var message = (uint)Picker.GetField("BrowseSetSelectionW", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var flag = (IntPtr)Picker.GetField("SelectionIsAPath", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(0x400u + 103u, message);
        Assert.NotEqual(IntPtr.Zero, flag);
    }
}
