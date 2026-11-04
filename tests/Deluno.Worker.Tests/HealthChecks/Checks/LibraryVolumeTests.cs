using Deluno.Worker.HealthChecks.Checks;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#1138: a library on a network share is on a different volume from a downloads folder on a drive, not "could not tell".</summary>
public sealed class LibraryVolumeTests
{
    [Theory]
    [InlineData(@"\\nas\media\Movies", "//nas/media")]
    [InlineData(@"\\NAS\media", "//NAS/media")]
    [InlineData("//nas/media/TV", "//nas/media")]
    public void A_network_share_is_named_by_its_server_and_share(string path, string volume)
        => Assert.Equal(volume, LibraryFolderChainHealthCheck.VolumeOf(path));

    [Fact]
    public void Two_folders_on_one_share_are_on_one_volume_and_two_shares_are_two()
    {
        Assert.Equal(LibraryFolderChainHealthCheck.VolumeOf(@"\\nas\media\Movies"), LibraryFolderChainHealthCheck.VolumeOf(@"\\nas\media\Downloads"));
        Assert.NotEqual(LibraryFolderChainHealthCheck.VolumeOf(@"\\nas\media\Movies"), LibraryFolderChainHealthCheck.VolumeOf(@"\\nas\other\Movies"));
    }

    // #1464: a folder's volume is the mount that holds it, on Linux as on Windows.
    private static readonly string[] LinuxMounts = ["/", "/data", "/downloads", "/media"];

    [Theory]
    [InlineData("/data/downloads", "/data")]
    [InlineData("/data/media/Films", "/data")]
    [InlineData("/data", "/data")]
    [InlineData("/database/films", "/")]
    [InlineData("/downloads/complete", "/downloads")]
    [InlineData("/media/Films", "/media")]
    [InlineData("/home/films", "/")]
    public void A_folder_is_on_the_longest_mount_that_holds_it(string path, string volume)
        => Assert.Equal(volume, LibraryFolderChainHealthCheck.MountOf(path, LinuxMounts));

    [Fact]
    public void Two_folders_under_one_mount_are_one_volume_and_two_bind_mounts_are_two()
    {
        Assert.Equal(LibraryFolderChainHealthCheck.MountOf("/data/downloads", LinuxMounts), LibraryFolderChainHealthCheck.MountOf("/data/media", LinuxMounts));
        Assert.NotEqual(LibraryFolderChainHealthCheck.MountOf("/downloads/x", LinuxMounts), LibraryFolderChainHealthCheck.MountOf("/media/x", LinuxMounts));
    }

    [Theory]
    [InlineData(@"C:\Downloads", @"C:\")]
    [InlineData(@"d:\Media\Films", @"D:\")]
    [InlineData(@"E:\Films", @"E:\Films")]
    public void A_windows_folder_is_on_its_drive_and_a_folder_no_mount_holds_is_its_own(string path, string volume)
        => Assert.Equal(volume, LibraryFolderChainHealthCheck.MountOf(path, [@"C:\", @"D:\"]));

    [Fact]
    public void A_folder_on_this_computer_is_on_one_of_its_mounts()
        => Assert.Contains(
            LibraryFolderChainHealthCheck.VolumeOf(Path.Combine(Path.GetTempPath(), "films")),
            DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName));
}
