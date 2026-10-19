using Microsoft.Data.Sqlite;

namespace Deluno.Infrastructure.Storage;

/// <summary>Recognises a write that failed because the disk holding the data folder is full (#1361).</summary>
public static class DiskFull
{
    public const string PlainMessage =
        "Deluno's data folder is full, so this could not be saved. Free some space on the disk that holds it, and try again.";

    private const int SqliteFull = 13;
    private const int ErrorDiskFull = 112;   // Windows ERROR_DISK_FULL
    private const int ErrorHandleDiskFull = 39;
    private const int Enospc = 28;            // Linux ENOSPC

    public static bool Caused(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && (sqlite.SqliteErrorCode == SqliteFull
                || sqlite.Message.Contains("disk is full", StringComparison.OrdinalIgnoreCase)
                || sqlite.Message.Contains("database or disk is full", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (current is IOException io)
            {
                var code = io.HResult & 0xFFFF;
                if (code is ErrorDiskFull or ErrorHandleDiskFull || io.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
                    || io.Message.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase)
                    || (OperatingSystem.IsLinux() && io.HResult == Enospc))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
