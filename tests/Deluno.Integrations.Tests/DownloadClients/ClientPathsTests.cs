using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#616: a folder is joined the way the client writes its paths, not the way this computer does.</summary>
public sealed class ClientPathsTests
{
    [Theory]
    [InlineData("/data", "/downloads/movies/", "/downloads/movies")]
    [InlineData("/data", "  D:\\Downloads\\Movies\\  ", "D:\\Downloads\\Movies")]
    [InlineData("/data", "\\\\nas\\share\\", "\\\\nas\\share")]
    [InlineData("/data/", "movies", "/data/movies")]
    [InlineData("/data", "/movies/", "/movies")]
    [InlineData("C:\\Downloads\\", "movies", "C:\\Downloads\\movies")]
    [InlineData("C:/Downloads", "movies", "C:/Downloads/movies")]
    [InlineData("C:/Down\\loads", "movies", "C:/Down\\loads\\movies")]
    [InlineData("relative", " movies/ ", "relative\\movies")]
    [InlineData("  /data/  ", "movies", "/data/movies")]
    public void A_folder_is_taken_as_it_is_when_it_is_a_full_path_and_otherwise_put_inside_the_root_the_way_the_root_is_written(string root, string folder, string expected)
    {
        Assert.Equal(expected, ClientPaths.ClientFolder(root, folder));
    }

    [Theory]
    [InlineData("/data/", null, "/data")]
    [InlineData("/data/", "", "/data")]
    [InlineData("C:\\Downloads\\", "   ", "C:\\Downloads")]
    public void With_no_folder_the_root_itself_is_the_answer(string root, string? folder, string expected)
    {
        Assert.Equal(expected, ClientPaths.ClientFolder(root, folder));
    }

    [Theory]
    [InlineData(null, "movies")]
    [InlineData("", "movies")]
    [InlineData("   ", "movies")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void With_no_root_a_relative_folder_cannot_be_placed(string? root, string? folder)
    {
        Assert.Null(ClientPaths.ClientFolder(root, folder));
    }

    [Fact]
    public void A_full_path_needs_no_root()
    {
        Assert.Equal("/mnt/dl", ClientPaths.ClientFolder(null, "/mnt/dl/"));
    }

    [Theory]
    [InlineData("/data", true)]
    [InlineData("\\\\nas\\share", true)]
    [InlineData("C:\\data", true)]
    [InlineData("c:/data", true)]
    [InlineData("C:\\", true)]
    [InlineData("C:/", true)]
    [InlineData("C:data", false)]
    [InlineData("C:", false)]
    [InlineData("1:\\data", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void A_full_client_path_is_a_drive_path_a_share_or_a_posix_absolute_path(string path, bool full)
    {
        Assert.Equal(full, ClientPaths.IsFullClientPath(path));
    }
}
