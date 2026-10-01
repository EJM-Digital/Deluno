namespace Deluno.Infrastructure.SystemReadings;

public static class HostReadingSources
{
    /// <summary>The source for the operating system Deluno is running on.</summary>
    public static IHostReadingSource ForThisMachine()
        => OperatingSystem.IsWindows() ? new WindowsHostReadingSource() : new LinuxHostReadingSource();
}
