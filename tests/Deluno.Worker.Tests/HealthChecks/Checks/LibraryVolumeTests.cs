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
}
