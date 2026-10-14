using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deluno.Persistence.Tests.Support;

/// <summary>Makes a real hard link with the operating system's own call, for tests that need two names for one file.</summary>
internal static class RealHardLink
{
    public static void Create(string existingFile, string newLink)
    {
        var made = OperatingSystem.IsWindows()
            ? CreateHardLink(newLink, existingFile, IntPtr.Zero)
            : link(existingFile, newLink) == 0;
        if (!made)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not hard link {existingFile} to {newLink}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int link(string oldpath, string newpath);
}
