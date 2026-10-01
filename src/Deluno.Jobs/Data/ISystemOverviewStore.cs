namespace Deluno.Jobs.Data;

/// <summary>One day's free-space reading of a drive.</summary>
public sealed record DriveSpaceDay(string VolumeKey, DateOnly Day, long FreeBytes, long TotalBytes);

/// <summary>What the job queue did today, for the System screen (#1098).</summary>
/// <param name="JobsToday">Jobs that finished today (done or given up) plus those running now.</param>
/// <param name="JobsFailedToday">Jobs that gave up today plus those failing and waiting to try again.</param>
/// <param name="OldestWaitingSeconds">How long the longest-waiting ready job has waited; null when none is waiting.</param>
public sealed record JobDayStats(int JobsToday, int JobsFailedToday, double? OldestWaitingSeconds);

/// <summary>The small records behind the System screen that the machine cannot give: free space day by day, and starts.</summary>
public interface ISystemOverviewStore
{
    /// <summary>Records today's free space for a drive; a later reading the same day replaces the earlier one.</summary>
    Task RecordDriveSpaceAsync(string volumeKey, DateOnly day, long freeBytes, long totalBytes, CancellationToken cancellationToken);

    Task<IReadOnlyList<DriveSpaceDay>> ListDriveSpaceAsync(DateOnly since, CancellationToken cancellationToken);

    Task RecordStartAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken);

    Task<int> CountStartsSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>Drops free-space days and starts older than the cutoff.</summary>
    Task PruneAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);

    Task<JobDayStats> GetJobDayStatsAsync(DateTimeOffset sinceUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
