using System.Runtime.InteropServices;

namespace Deluno.Tray;

internal static class NativeMethods
{
    internal const int WM_DELUNO_SHOW = 0x8001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
