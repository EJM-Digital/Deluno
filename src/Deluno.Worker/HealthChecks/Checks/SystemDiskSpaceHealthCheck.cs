using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Deluno's own data root, not a library's (#909) -- the same 12% floor
/// <c>MonitoringService</c> already alerts on, read here too so it also
/// shows on Health, the Dashboard tile and the bell rather than only in
/// Monitoring's own alert list.
/// </summary>
public sealed class SystemDiskSpaceHealthCheck(IOptions<StoragePathOptions> storageOptions, SystemDiskSpaceHealthCheck.DriveReader? readDrive = null) : IHealthCheck
{
    /// <summary>What the drive under a path reports. Replaceable so a test can give a drive that is nearly full or not ready.</summary>
    public delegate (bool IsReady, long TotalSize, long AvailableFreeSpace) DriveReader(string fullPath);

    private static (bool IsReady, long TotalSize, long AvailableFreeSpace) ReadRealDrive(string fullPath)
    {
        var drive = new DriveInfo(fullPath);
        // A drive that is not ready makes TotalSize throw an IOException, which RunAsync reports as "could not read".
        return (drive.IsReady, drive.TotalSize, drive.AvailableFreeSpace);
    }

    private const double LowStorageThresholdPercent = 12d;

    public string Category => HealthCheckCategories.System;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        const string id = "system:disk-space";
        const string name = "Deluno's own storage";
        var dataRoot = storageOptions.Value.DataRoot;

        try
        {
            var fullPath = Path.GetFullPath(dataRoot);
            var drive = (readDrive ?? ReadRealDrive)(fullPath);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return Task.FromResult<IReadOnlyList<HealthCheckOutcome>>(
                    [HealthCheckOutcome.Unknown(id, Category, name, $"Deluno could not read free space for {fullPath}.")]);
            }

            var percent = Math.Round((double)drive.AvailableFreeSpace / drive.TotalSize * 100d, 2);
            IReadOnlyList<HealthCheckOutcome> result = percent <= LowStorageThresholdPercent
                ? [HealthCheckOutcome.Problem(
                    id, Category, name,
                    $"Only {percent:0.#}% free remains under {fullPath}.",
                    cause: "The drive Deluno's own data lives on is running low on space.",
                    fix: "Free up space on this drive, or move Deluno's data root to a larger one.")]
                : [HealthCheckOutcome.Ok(id, Category, name, $"{percent:0.#}% free under {fullPath}.")];
            return Task.FromResult(result);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<HealthCheckOutcome>>(
                [HealthCheckOutcome.Unknown(id, Category, name, $"Deluno could not read free space for {dataRoot}: {exception.Message}")]);
        }
    }
}
