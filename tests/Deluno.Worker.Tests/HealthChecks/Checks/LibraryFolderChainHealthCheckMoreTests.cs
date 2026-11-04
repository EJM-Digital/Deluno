using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#1299: which link a library row names when several are wrong, links of the library itself, and volumes that cannot be told apart.</summary>
public sealed class LibraryFolderChainHealthCheckMoreTests
{
    private static LibraryFolderChainHealthCheck Check(FolderChainReport report)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = "lib0", Name = "Films" }]);
        var chain = new Mock<IFolderChainService>();
        chain.Setup(service => service.CheckAsync("lib0", It.IsAny<CancellationToken>())).ReturnsAsync(report);
        return new LibraryFolderChainHealthCheck(libraries.Object, chain.Object);
    }

    private static FolderChainReport Report(string? downloads, string root, FolderChainClient[]? clients = null, FolderChainLink[]? libraryLinks = null)
        => new("lib0", "Films", "movies", false, null, downloads, null, root, clients ?? [], libraryLinks ?? []);

    private static FolderChainLink Link(string status, string message) => new("step", status, message);

    [Fact]
    public async Task A_problem_in_the_librarys_own_links_is_named_as_the_library_problem()
    {
        var report = Report(null, "/media/films", libraryLinks: [Link(FolderChainStatuses.Problem, "The library folder is missing.")]);

        var outcome = Assert.Single(await Check(report).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("The library folder is missing.", outcome.Message);
    }

    [Fact]
    public async Task A_real_problem_is_named_before_a_link_that_could_only_not_be_confirmed()
    {
        var client = new FolderChainClient("c1", "qBittorrent", "qbittorrent", "movies",
            [Link(FolderChainStatuses.Unknown, "Could not confirm."), Link(FolderChainStatuses.Problem, "Saves to the wrong folder.")]);

        var outcome = Assert.Single(await Check(Report(null, "/media/films", [client])).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Saves to the wrong folder.", outcome.Message);
    }

    [Fact]
    public async Task Of_several_links_that_could_not_be_confirmed_the_first_is_named_and_it_is_not_a_problem()
    {
        var client = new FolderChainClient("c1", "qBittorrent", "qbittorrent", "movies",
            [Link(FolderChainStatuses.Ok, "Fine."), Link(FolderChainStatuses.Unknown, "First unknown."), Link(FolderChainStatuses.Unknown, "Second unknown.")]);

        var outcome = Assert.Single(await Check(Report(null, "/media/films", [client])).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("First unknown.", outcome.Message);
        Assert.Equal(HealthCheckCategories.Library, outcome.Category);
        Assert.Equal(HealthCheckCategories.Library, Check(Report(null, "/x")).Category);
    }

    [Fact]
    public async Task Folders_that_cannot_be_read_as_paths_say_they_could_not_be_told_apart()
    {
        var report = Report("/downloads/bad\0folder", "/media/films");

        var volumes = (await Check(report).RunAsync(CancellationToken.None)).Single(outcome => outcome.Id == "library:lib0:volumes");

        Assert.Equal(HealthCheckStatuses.Unknown, volumes.Status);
        Assert.Equal("The Films library's downloads and library folders", volumes.Name);
        Assert.Equal("Deluno could not tell whether downloads and this library share a volume.", volumes.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_library_with_no_root_gets_no_volume_row(string root)
    {
        var outcomes = await Check(Report("/downloads/films", root)).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, outcome => outcome.Id.EndsWith(":volumes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\\\\nas\\media\\films", "//nas/media")]
    [InlineData("//nas/media/films/extra", "//nas/media")]
    [InlineData("  //nas/downloads  ", "//nas/downloads")]
    [InlineData("//nas", "//nas")]
    public void A_network_share_is_a_volume_named_by_its_server_and_share(string path, string volume)
    {
        Assert.Equal(volume, LibraryFolderChainHealthCheck.VolumeOf(path));
    }

    [Fact]
    public async Task Two_folders_on_this_computers_own_drive_share_a_volume()
    {
        var temp = Path.GetTempPath();
        var report = Report(Path.Combine(temp, "downloads"), Path.Combine(temp, "library"));

        var volumes = (await Check(report).RunAsync(CancellationToken.None)).Single(outcome => outcome.Id == "library:lib0:volumes");

        Assert.Equal(HealthCheckStatuses.Ok, volumes.Status);
    }
}
