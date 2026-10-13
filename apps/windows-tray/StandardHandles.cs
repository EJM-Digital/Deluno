using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deluno.Tray;

/// <summary>
/// Points the process's standard input, output and error at the null device (#1258).
///
/// <para>Deluno.exe inherits the standard handles its launcher gave it. Called at the very start of <c>Main</c>, before
/// Velopack or anything else writes, this means no code path (a library, <c>Console.Write</c>, a native call) can wait on
/// a pipe nobody reads, and a program Deluno starts without redirecting its output inherits the null device and not that
/// pipe either. Reading standard input ends at once.</para>
/// </summary>
internal static class StandardHandles
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;

    /// <summary>
    /// Replaces the three standard handles and the console's writers and reader. Returns what the handles were, in words,
    /// for the start-up log. Never throws: a process that cannot do this carries on as it was.
    /// </summary>
    internal static string DetachToNullDevice()
    {
        string before;
        try
        {
            before = $"in={Kind(StdInput)}, out={Kind(StdOutput)}, err={Kind(StdError)}";
        }
        catch
        {
            before = "unreadable";
        }

        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            var device = CreateFile("NUL", GenericRead | GenericWrite, FileShareRead | FileShareWrite, ref security, OpenExisting, 0, IntPtr.Zero);
            if (device.IsInvalid)
            {
                return before + "; the null device could not be opened, so they were left as they were";
            }

            // Kept open for the life of the process: it is every standard handle from now on.
            GC.KeepAlive(device);
            _device = device;
            var handle = device.DangerousGetHandle();
            SetStdHandle(StdInput, handle);
            SetStdHandle(StdOutput, handle);
            SetStdHandle(StdError, handle);
            Console.SetIn(TextReader.Null);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return before + "; now the null device";
        }
        catch (Exception exception)
        {
            return before + $"; could not be replaced ({exception.Message})";
        }
    }

    private static SafeFileHandle? _device;

    private static string Kind(int which)
    {
        var handle = GetStdHandle(which);
        if (handle == IntPtr.Zero)
        {
            return "none";
        }

        if (handle == new IntPtr(-1))
        {
            return "invalid";
        }

        return GetFileType(handle) switch
        {
            1 => "file or null device",
            2 => "console",
            3 => "pipe",
            _ => "unknown"
        };
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
}
