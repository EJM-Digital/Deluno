using System.Runtime.CompilerServices;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// The System > Health rows for each library's category and save-path chain, and whether its downloads and library share a volume (#1299).
/// </summary>
public sealed class LibraryFolderChainHealthCheckTests
{
    private static LibraryItem Library(string id)
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, Name = id };

    private static FolderChainReport Report(string id, string name, string? downloads, string root, params FolderChainClient[] clients)
        => new(id, name, "movies", false, null, downloads, null, root, clients, []);

    private static FolderChainClient Client(string name, params FolderChainLink[] links)
        => new("c-" + name, name, "qbittorrent", "movies", links);

    private static LibraryFolderChainHealthCheck Check(params FolderChainReport?[] reports)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(reports.Select((_, i) => Library("lib" + i)).ToArray());
        var chain = new Mock<IFolderChainService>();
        for (var i = 0; i < reports.Length; i++)
        {
            chain.Setup(service => service.CheckAsync("lib" + i, It.IsAny<CancellationToken>())).ReturnsAsync(reports[i]);
        }

        return new LibraryFolderChainHealthCheck(libraries.Object, chain.Object);
    }

    [Fact]
    public async Task A_library_whose_every_client_link_matches_reads_ok_on_its_own_row()
    {
        var report = Report("lib0", "Films", null, "/media/films", Client("qBittorrent", new FolderChainLink("category", FolderChainStatuses.Ok, "Matches.")));

        var outcome = Assert.Single(await Check(report).RunAsync(CancellationToken.None));

        Assert.Equal("library:lib0:folder-chain", outcome.Id);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal(HealthCheckCategories.Library, outcome.Category);
        Assert.Equal("The Films library's category and save path", outcome.Name);
    }

    [Fact]
    public async Task A_client_saving_somewhere_the_library_does_not_expect_is_a_problem_that_says_which_link_and_where_to_fix_it()
    {
        var report = Report("lib0", "Films", null, "/media/films", Client("qBittorrent",
            new FolderChainLink("category", FolderChainStatuses.Ok, "Fine."),
            new FolderChainLink("save-path", FolderChainStatuses.Problem, "qBittorrent saves movies to /elsewhere, not /downloads/films.")));

        var outcome = Assert.Single(await Check(report).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("qBittorrent saves movies to /elsewhere, not /downloads/films.", outcome.Message);
        Assert.Contains("no longer matches what the Films library expects", outcome.Cause);
        Assert.Equal("Open the Films library's folder chain and fix the category or path it names.", outcome.Fix);
    }

    [Fact]
    public async Task A_link_that_could_not_be_confirmed_reads_unknown_not_problem()
    {
        var report = Report("lib0", "Films", null, "/media/films", Client("qBittorrent", new FolderChainLink("save-path", FolderChainStatuses.Unknown, "qBittorrent did not say where it saves.")));

        var outcome = Assert.Single(await Check(report).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("qBittorrent did not say where it saves.", outcome.Message);
    }

    [Fact]
    public async Task A_library_the_chain_service_does_not_know_gets_no_row()
    {
        Assert.Empty(await Check((FolderChainReport?)null).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Downloads_and_library_on_different_volumes_are_told_that_every_import_copies_and_on_one_volume_that_imports_can_hardlink()
    {
        var split = Report("lib0", "Films", "//server/downloads/films", "//server/media/films");
        var shared = Report("lib1", "Shows", "//server/media/downloads", "//server/media/shows");

        var outcomes = await Check(split, shared).RunAsync(CancellationToken.None);

        var different = outcomes.Single(outcome => outcome.Id == "library:lib0:volumes");
        Assert.Equal(HealthCheckStatuses.Unknown, different.Status);
        Assert.Contains("on different volumes", different.Message);
        Assert.Contains("Every import copies the file instead", different.Message);
        var same = outcomes.Single(outcome => outcome.Id == "library:lib1:volumes");
        Assert.Equal(HealthCheckStatuses.Ok, same.Status);
        Assert.Equal("Downloads and the library share a volume, so imports can hardlink.", same.Message);
    }

    [Fact]
    public async Task A_library_with_no_downloads_folder_gets_no_volume_row()
    {
        var outcomes = await Check(Report("lib0", "Films", null, "/media/films")).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, outcome => outcome.Id.EndsWith(":volumes", StringComparison.Ordinal));
    }
}
