using Deluno.Contracts;

namespace Deluno.Worker.Tests.Files;

/// <summary>#1464: a folder's volume is the mount that holds it, on Linux as on Windows.</summary>
public sealed class MountedVolumesTests
{
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
        => Assert.Equal(volume, MountedVolumes.RootOf(path, LinuxMounts, StringComparison.Ordinal));

    [Fact]
    public void Two_folders_under_one_mount_are_one_volume_and_two_bind_mounts_are_two()
    {
        Assert.Equal(MountedVolumes.RootOf("/data/downloads", LinuxMounts, StringComparison.Ordinal), MountedVolumes.RootOf("/data/media", LinuxMounts, StringComparison.Ordinal));
        Assert.NotEqual(MountedVolumes.RootOf("/downloads/x", LinuxMounts, StringComparison.Ordinal), MountedVolumes.RootOf("/media/x", LinuxMounts, StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_tells_folders_apart_by_case_and_windows_does_not()
    {
        Assert.Equal("/", MountedVolumes.RootOf("/Media/Films", LinuxMounts, StringComparison.Ordinal));
        Assert.Equal(@"D:\", MountedVolumes.RootOf(@"d:\Media\Films", [@"C:\", @"D:\"], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:\Downloads", @"C:\")]
    [InlineData(@"E:\Films", null)]
    [InlineData("", null)]
    public void A_windows_folder_is_on_its_drive_and_a_folder_no_mount_holds_has_none(string path, string? volume)
        => Assert.Equal(volume, MountedVolumes.RootOf(path, [@"C:\", @"D:\", ""], StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_default_comparison_is_this_systems()
        => Assert.Equal(OperatingSystem.IsWindows() ? @"D:\" : null, MountedVolumes.RootOf(@"d:\Media", [@"D:\"]));

    [Fact]
    public void The_mount_table_gives_each_mount_point_with_spaces_and_tabs_undone()
        => Assert.Equal(
            ["/", "/media/My Films", "/mnt/tab\there"],
            MountedVolumes.ParseMountTable([
                "overlay / overlay rw,relatime 0 0",
                @"/dev/sdb1 /media/My\040Films ext4 rw 0 0",
                @"/dev/sdc1 /mnt/tab\011here ext4 rw 0 0",
                "",
                "broken-line"]));

    [Fact]
    public void This_computer_has_at_least_one_mount_and_a_temp_folder_is_under_one()
    {
        var roots = MountedVolumes.Roots();
        Assert.NotEmpty(roots);
        Assert.NotNull(MountedVolumes.RootOf(Path.GetTempPath(), roots));
    }
}
