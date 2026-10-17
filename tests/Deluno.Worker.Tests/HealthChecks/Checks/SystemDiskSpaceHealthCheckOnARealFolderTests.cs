using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>Deluno's own data drive on System > Health (#1294), read from a real folder on the real drive.</summary>
public sealed class SystemDiskSpaceHealthCheckOnARealFolderTests
{
    private static SystemDiskSpaceHealthCheck DiskCheck(string dataRoot)
        => new(Options.Create(new StoragePathOptions { DataRoot = dataRoot }));

    [Fact]
    public async Task Deluno_own_drive_reads_the_real_free_space_and_says_ok_or_problem_by_the_twelve_percent_floor()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-disk-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(root));
            var freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100d;

            var outcome = Assert.Single(await DiskCheck(root).RunAsync(CancellationToken.None));

            Assert.Equal("system:disk-space", outcome.Id);
            Assert.Equal("Deluno's own storage", outcome.Name);
            if (freePercent > 12.5)
            {
                Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
                Assert.EndsWith($"% free under {Path.GetFullPath(root)}.", outcome.Message);
            }
            else if (freePercent < 11.5)
            {
                Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
                Assert.Contains("free remains under", outcome.Message);
                Assert.Contains("Free up space on this drive", outcome.Fix);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_data_root_that_cannot_be_read_is_unknown_not_a_problem_and_not_fine()
    {
        var outcome = Assert.Single(await DiskCheck("bad\0path").RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.StartsWith("Deluno could not read free space for bad", outcome.Message);
    }
}
