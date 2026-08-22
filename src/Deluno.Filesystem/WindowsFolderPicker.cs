using System.Runtime.InteropServices;
using System.Text;

namespace Deluno.Filesystem;

internal static class WindowsFolderPicker
{
    private const uint BrowseReturnOnlyFileSystemDirs = 0x00000001;
    private const uint BrowseForNewDialogStyle = 0x00000040;
    private const uint BrowseForEditBox = 0x00000010;
    private const int BrowseCallbackInitialized = 1;
    private const uint BrowseSetSelectionW = 0x0466;
    private const uint CoInitializeApartmentThreaded = 0x2;

    public static Task<WindowsFolderPickerResult> PickAsync(string? initialPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(WindowsFolderPickerResult.Unavailable(
                "The native folder picker is only available in an interactive Windows session."));
        }

        if (!Environment.UserInteractive)
        {
            return Task.FromResult(WindowsFolderPickerResult.Unavailable(
                "The native folder picker is unavailable when Deluno runs as a Windows service. Use Advanced browse or enter the server path manually."));
        }

        var completion = new TaskCompletionSource<WindowsFolderPickerResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var thread = new Thread(() => ShowPicker(initialPath, completion))
            {
                IsBackground = true,
                Name = "Deluno Windows folder picker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
        {
            completion.TrySetResult(WindowsFolderPickerResult.Unavailable(
                $"The native folder picker could not be started: {exception.Message}"));
        }

        return completion.Task;
    }

    private static void ShowPicker(
        string? initialPath,
        TaskCompletionSource<WindowsFolderPickerResult> completion)
    {
        IntPtr displayName = IntPtr.Zero;
        IntPtr title = IntPtr.Zero;
        IntPtr initialPathPointer = IntPtr.Zero;
        IntPtr selectedItem = IntPtr.Zero;
        var comInitialized = false;

        try
        {
            var comResult = CoInitializeEx(IntPtr.Zero, CoInitializeApartmentThreaded);
            comInitialized = comResult >= 0;

            displayName = Marshal.AllocHGlobal(32768 * sizeof(char));
            title = Marshal.StringToCoTaskMemUni("Choose a folder for Deluno");

            BrowseCallback? callback = null;
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                initialPathPointer = Marshal.StringToCoTaskMemUni(initialPath.Trim());
                callback = SetInitialPath;
            }

            var browseInfo = new BrowseInfo
            {
                PszDisplayName = displayName,
                LpszTitle = title,
                UlFlags = BrowseReturnOnlyFileSystemDirs | BrowseForNewDialogStyle | BrowseForEditBox,
                Callback = callback,
                LParam = initialPathPointer
            };

            selectedItem = SHBrowseForFolder(ref browseInfo);
            if (selectedItem == IntPtr.Zero)
            {
                completion.TrySetResult(WindowsFolderPickerResult.FromCancelled());
                return;
            }

            var selectedPath = new StringBuilder(32768);
            if (SHGetPathFromIDList(selectedItem, selectedPath) && !string.IsNullOrWhiteSpace(selectedPath.ToString()))
            {
                completion.TrySetResult(WindowsFolderPickerResult.Selected(selectedPath.ToString()));
            }
            else
            {
                completion.TrySetResult(WindowsFolderPickerResult.Unavailable(
                    "The selected location is not a filesystem folder."));
            }
        }
        catch (Exception exception)
        {
            completion.TrySetResult(WindowsFolderPickerResult.Unavailable(
                $"The native folder picker could not be opened: {exception.Message}"));
        }
        finally
        {
            if (selectedItem != IntPtr.Zero)
            {
                CoTaskMemFree(selectedItem);
            }

            if (initialPathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(initialPathPointer);
            }

            if (title != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(title);
            }

            if (displayName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(displayName);
            }

            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    private static int SetInitialPath(IntPtr windowHandle, uint message, IntPtr _, IntPtr data)
    {
        if (message == BrowseCallbackInitialized && data != IntPtr.Zero)
        {
            SendMessage(windowHandle, BrowseSetSelectionW, IntPtr.Zero, data);
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr HwndOwner;
        public IntPtr PidlRoot;
        public IntPtr PszDisplayName;
        public IntPtr LpszTitle;
        public uint UlFlags;
        public BrowseCallback? Callback;
        public IntPtr LParam;
        public int IImage;
    }

    private delegate int BrowseCallback(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo browseInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr item, StringBuilder path);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed record WindowsFolderPickerResult(
    bool Available,
    string? Path,
    bool Cancelled,
    string? Message)
{
    public static WindowsFolderPickerResult Selected(string path)
        => new(true, path, false, null);

    public static WindowsFolderPickerResult FromCancelled()
        => new(true, null, true, null);

    public static WindowsFolderPickerResult Unavailable(string message)
        => new(false, null, false, message);
}
