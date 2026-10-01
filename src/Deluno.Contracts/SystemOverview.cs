namespace Deluno.Contracts;

/// <summary>
/// Everything the System screen shows about Deluno and the machine it runs on (#1098).
/// Served by <c>GET /api/system/overview</c> and pushed as <c>SystemOverviewChanged</c> on the
/// dashboard subject every two seconds while someone is watching, in the same shape.
///
/// Every value is a real reading from the machine Deluno runs on. A value that cannot be read is
/// null and the screen leaves it out: nothing here is estimated or made up.
/// </summary>
public sealed record SystemOverview(
    SystemOverviewInstance Instance,
    SystemOverviewMachine Machine,
    SystemOverviewNetwork Network,
    IReadOnlyList<SystemOverviewDrive> Drives,
    SystemOverviewHistory History);

/// <param name="RunsAs">service, app or docker.</param>
/// <param name="CpuPercent">Deluno's own share of the machine, 0 to 100.</param>
/// <param name="MemoryBytes">Deluno's own working set.</param>
/// <param name="OldestWaitingSeconds">How long the longest-waiting ready job has waited; null when nothing is waiting.</param>
/// <param name="AnswerMs">How long a typical request takes (the median of the last 15 minutes).</param>
/// <param name="AnswerSlowestMs">How long the slowest 5% of requests take.</param>
/// <param name="RequestsToday">Requests answered since midnight or since Deluno started, whichever is later.</param>
/// <param name="RestartsThisWeek">Starts recorded in the last seven days, not counting the current run.</param>
/// <param name="BrowsersLive">Screens currently connected for live updates.</param>
public sealed record SystemOverviewInstance(
    string Version,
    bool Latest,
    string Channel,
    DateTimeOffset StartedUtc,
    string Address,
    string RunsAs,
    double CpuPercent,
    long MemoryBytes,
    int WorkersBusy,
    double? OldestWaitingSeconds,
    double AnswerMs,
    double AnswerSlowestMs,
    int JobsToday,
    int JobsFailedToday,
    long RequestsToday,
    long RequestErrorsToday,
    long DataBytes,
    int DatabaseCount,
    DateTimeOffset? LastBackupUtc,
    long? LastBackupBytes,
    int RestartsThisWeek,
    int BrowsersLive);

public sealed record SystemOverviewMachine(
    string Os,
    long UptimeSeconds,
    bool? RebootPending,
    double? CpuTemperatureC,
    double CpuPercent,
    int Cores,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskReadBytesPerSec,
    long DiskWriteBytesPerSec,
    long DiskIops);

public sealed record SystemOverviewNetwork(
    string? Adapter,
    long? LinkMbps,
    string? Ip,
    long InBytesPerSec,
    long OutBytesPerSec,
    bool Internet,
    double? DnsLookupMs,
    double? NasMs,
    string? NasName,
    double? MetadataMs,
    bool? ClockInSync);

/// <param name="Health">good, warning, failing or unknown.</param>
/// <param name="FullInDays">Days until the drive is full at the pace of the last seven days; null when it is not filling.</param>
/// <param name="ReadBytesPerSec">Null when the operating system gives no reading for this drive (a network share, say).</param>
public sealed record SystemOverviewDrive(
    string Name,
    string Path,
    long TotalBytes,
    long FreeBytes,
    long DelunoBytes,
    double? FullInDays,
    long? ReadBytesPerSec,
    long? WriteBytesPerSec,
    long? Iops,
    double? BusyPercent,
    string Health,
    string? HealthNote);

/// <summary>The last ten minutes, one reading every ten seconds, oldest first.</summary>
public sealed record SystemOverviewHistory(
    IReadOnlyList<double> Cpu,
    IReadOnlyList<double> Memory,
    IReadOnlyList<double> Disk,
    IReadOnlyList<double> NetIn);

public static class SystemOverviewEventNames
{
    /// <summary>Pushed on the dashboard subject with a full <see cref="SystemOverview"/> as its data.</summary>
    public const string Changed = "SystemOverviewChanged";
}
