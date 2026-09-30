using Deluno.Infrastructure.Storage;

namespace Deluno.Worker.Tests.Dashboard;

/// <summary>Sizing a drive, including a Windows network share, which .NET's DriveInfo cannot do (#1082).</summary>
public sealed class VolumeSpaceTests
{
    [Theory]
    [InlineData(@"\\nas\media\Movies", true)]
    [InlineData(@"\\nas\media", true)]
    [InlineData("//nas/media/TV", true)]
    [InlineData(@"C:\Media\Movies", false)]
    [InlineData("/mnt/media/Movies", false)]
    [InlineData(@"\\?\C:\Media", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_network_path_is_told_apart_from_a_drive_path(string? path, bool expected)
        => Assert.Equal(expected, VolumeSpace.IsNetworkSharePath(path));

    [Theory]
    [InlineData(@"\\nas\media\Movies\Dune", @"\\nas\media")]
    [InlineData("//nas/media/TV", @"\\nas\media")]
    [InlineData(@"\\nas\media", @"\\nas\media")]
    [InlineData(@"\\nas", null)]
    [InlineData(@"C:\Media", null)]
    public void Every_folder_on_one_share_has_the_same_share_root(string path, string? expected)
        => Assert.Equal(expected, VolumeSpace.NetworkShareRoot(path));

    [Fact]
    public void A_network_share_is_sized_when_the_machine_can_reach_one()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The administrative share of this machine's own system drive is a real UNC path.
        var unc = @"\\localhost\" + Path.GetPathRoot(Environment.SystemDirectory)![0] + @"$\Windows";
        if (!Directory.Exists(unc))
        {
            return;
        }

        var reading = VolumeSpace.TryRead(unc);

        Assert.NotNull(reading);
        Assert.True(reading!.TotalBytes > 0);
        Assert.True(reading.FreeBytes >= 0);
        Assert.Equal(@"\\LOCALHOST\C$", VolumeSpace.NetworkShareRoot(unc)!.ToUpperInvariant());
    }

    [Fact]
    public void A_folder_that_exists_is_read_and_one_that_does_not_is_read_through_its_parent()
    {
        var real = Path.GetTempPath();
        Assert.NotNull(VolumeSpace.TryRead(real));
        Assert.NotNull(VolumeSpace.TryRead(Path.Combine(real, Guid.NewGuid().ToString("N"), "not-yet")));
        Assert.Null(VolumeSpace.TryRead(""));
    }
}
