using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1073: the folder a client without categories of its own is told to save a grab into, and the evidence needed to name one.</summary>
public sealed class ClientSaveFoldersBranchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DownloadClientItem Client(string? host = "localhost", string? endpoint = null, string movies = "movies", string tv = "tv")
        => new("c1", "Deluge", "deluge", host, 8112, null, null, endpoint, false, null, movies, tv, null, 1, true, "healthy", null, null, null, Now, Now, Now);

    private static DownloadClientPathMappingItem Mapping(string local, string remote, bool enabled = true, int priority = 1)
        => new("m" + local + remote, "c1", remote, local, enabled, priority, Now, Now);

    [Theory]
    [InlineData(null, ClientSaveFolders.PathStyle.Unknown)]
    [InlineData("", ClientSaveFolders.PathStyle.Unknown)]
    [InlineData("   ", ClientSaveFolders.PathStyle.Unknown)]
    [InlineData("\\\\nas\\share", ClientSaveFolders.PathStyle.Windows)]
    [InlineData(" C:\\Downloads", ClientSaveFolders.PathStyle.Windows)]
    [InlineData("d:/x", ClientSaveFolders.PathStyle.Windows)]
    [InlineData("/data", ClientSaveFolders.PathStyle.Posix)]
    [InlineData("downloads", ClientSaveFolders.PathStyle.Unknown)]
    [InlineData("1:\\x", ClientSaveFolders.PathStyle.Unknown)]
    [InlineData("C", ClientSaveFolders.PathStyle.Unknown)]
    public void A_path_is_windows_or_posix_style_by_how_it_starts(string? path, ClientSaveFolders.PathStyle style)
    {
        Assert.Equal(style, ClientSaveFolders.StyleOf(path));
    }

    [Fact]
    public void A_folder_can_be_named_only_with_evidence_on_every_count()
    {
        static bool Yes(string _) => true;
        var share = (bool same, bool container, string host, string? reported, Func<string, bool>? exists) =>
            ClientSaveFolders.CanShareHostFolder(same, container, host, reported, exists);

        Assert.True(share(true, false, "/data/dl", " /data/dl ", Yes));
        Assert.False(share(false, false, "/data/dl", "/data/dl", Yes));
        Assert.False(share(true, true, "/data/dl", "/data/dl", Yes));
        Assert.False(share(true, false, "/data/dl", null, Yes));
        Assert.False(share(true, false, "relative", "/data/dl", Yes));
        Assert.False(share(true, false, "/data/dl", "C:\\dl", Yes));
        Assert.False(share(true, false, "C:\\dl", "/data/dl", Yes));
        Assert.False(share(true, false, "/data/dl", "/data/dl", _ => false));
    }

    [Fact]
    public void The_folder_check_can_be_replaced_for_one_test_only_and_otherwise_asks_this_computer()
    {
        var asked = new List<string>();
        ClientSaveFolders.DirectoryExistsOverride.Value = path => { asked.Add(path); return true; };
        try
        {
            Assert.True(ClientSaveFolders.CanShareHostFolder(true, false, "/data/dl", "/data/dl"));
        }
        finally
        {
            ClientSaveFolders.DirectoryExistsOverride.Value = null;
        }

        Assert.Equal(["/data/dl"], asked);
        Assert.False(ClientSaveFolders.CanShareHostFolder(true, false, "/definitely/not/a/folder/here", "/definitely/not/a/folder/here"));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("example.invalid", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void A_client_is_on_this_machine_when_its_host_is_this_computer(string? host, bool same)
    {
        Assert.Equal(same, ClientSaveFolders.IsSameMachine(Client(host)));
    }

    [Fact]
    public void This_computers_own_name_is_this_machine()
    {
        Assert.True(ClientSaveFolders.IsSameMachine(Client(Environment.MachineName)));
        Assert.True(ClientSaveFolders.IsSameMachine(Client(System.Net.Dns.GetHostName())));
    }

    [Fact]
    public void With_no_host_the_endpoint_address_is_read_and_a_bad_one_is_not()
    {
        Assert.True(ClientSaveFolders.IsSameMachine(Client(host: null, endpoint: "http://localhost:8112/json")));
        Assert.False(ClientSaveFolders.IsSameMachine(Client(host: " ", endpoint: "http://example.invalid:8112")));
        Assert.False(ClientSaveFolders.IsSameMachine(Client(host: null, endpoint: "not a url")));
        Assert.True(ClientSaveFolders.IsSameMachine(Client(host: "127.0.0.1", endpoint: "http://example.invalid")));
    }

    [Fact]
    public void A_computer_whose_addresses_cannot_be_read_is_not_taken_for_this_machine_and_a_matching_one_is()
    {
        var other = Client("203.0.113.9");

        Assert.False(ClientSaveFolders.IsSameMachine(other, () => throw new System.Net.NetworkInformation.NetworkInformationException()));
        Assert.True(ClientSaveFolders.IsSameMachine(other, () => [System.Net.IPAddress.Parse("203.0.113.9")]));
        Assert.False(ClientSaveFolders.IsSameMachine(other, () => [System.Net.IPAddress.Parse("203.0.113.10")]));
    }

    [Fact]
    public void An_address_one_of_this_computers_interfaces_has_is_this_machine()
    {
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => !System.Net.IPAddress.IsLoopback(address));
        if (own is null)
        {
            return; // reason: a machine with no network address of its own has nothing to prove here
        }

        Assert.True(ClientSaveFolders.IsSameMachine(Client(own.ToString())));
    }

    [Theory]
    [InlineData("tv", null, "tvcat")]
    [InlineData("TV", "  ", "tvcat")]
    [InlineData("movies", null, "moviecat")]
    [InlineData("tv", " own ", "own")]
    public void A_library_link_uses_its_own_category_else_the_clients_for_that_kind_of_media(string mediaType, string? link, string expected)
    {
        var library = ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { MediaType = mediaType };

        Assert.Equal(expected, ClientSaveFolders.RoutedCategory(Client(movies: "moviecat", tv: "tvcat"), library, link));
    }

    [Fact]
    public void A_client_with_no_category_for_the_media_gives_an_empty_one()
    {
        var library = ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { MediaType = "movies" };

        Assert.Equal(string.Empty, ClientSaveFolders.RoutedCategory(Client(movies: null!), library, null));
    }

    [Fact]
    public void A_host_folder_is_written_the_way_the_client_sees_it_through_the_longest_matching_mapping()
    {
        var mappings = new[]
        {
            Mapping("/data", "/downloads"),
            Mapping("/data/movies", "/dl/movies"),
            Mapping("D:\\TV", "\\\\nas\\tv", priority: 5),
            Mapping("D:\\TV", "\\\\other\\tv", priority: 2),
            Mapping("/off", "/never", enabled: false),
            Mapping("", "/blank"),
            Mapping("/nolocal", "")
        };

        Assert.Equal("/dl/movies/kids", ClientSaveFolders.ToClientPath("/data/movies/kids", mappings));
        Assert.Equal("/dl/movies", ClientSaveFolders.ToClientPath("/data/movies/", mappings));
        Assert.Equal("/downloads/other/deep", ClientSaveFolders.ToClientPath("/data/other/deep", mappings));
        Assert.Equal("\\\\other\\tv\\Kids\\Cartoons", ClientSaveFolders.ToClientPath("D:\\TV\\Kids\\Cartoons", mappings));
        Assert.Equal("/database", ClientSaveFolders.ToClientPath("/database", mappings));
        Assert.Equal("/off/x", ClientSaveFolders.ToClientPath("/off/x", mappings));
        Assert.Equal("/nolocal/x", ClientSaveFolders.ToClientPath("/nolocal/x", mappings));
        Assert.Equal("/elsewhere", ClientSaveFolders.ToClientPath("/elsewhere", mappings));
    }

    private static LibraryItem Library(string id, string mediaType, string? downloads)
        => ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, MediaType = mediaType, DownloadsPath = downloads! };

    private static Mock<ILibrariesRepository> Repo(IEnumerable<(LibraryItem Library, string[] Links)> libraries)
    {
        var repo = new Mock<ILibrariesRepository>();
        repo.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries.Select(l => l.Library).ToArray());
        foreach (var (library, links) in libraries)
        {
            var snapshot = new LibraryRoutingSnapshot(library.Id, "name", [],
                links.Select((id, i) => new LibraryDownloadClientLinkItem("l" + i, library.Id, id.Split('|')[0], "n", 1, Now, Now, id.Contains('|') ? id.Split('|')[1] : null)).ToArray());
            repo.Setup(r => r.GetLibraryRoutingAsync(library.Id, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        }

        return repo;
    }

    [Fact]
    public async Task The_folder_the_routed_libraries_agree_on_is_resolved_through_a_mapping()
    {
        var repo = Repo([(Library("l1", "movies", "/data/movies"), ["c1"]), (Library("l2", "MOVIES", " /DATA/movies "), ["C1"]), (Library("l3", "tv", "/data/tv"), ["c1"])]);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [Mapping("/data", "/downloads")], CancellationToken.None);

        Assert.Equal(new ClientSaveFolders.Resolved("/data/movies", "/downloads/movies"), resolved);
    }

    [Fact]
    public async Task A_folder_a_mapping_covers_is_resolved_even_when_the_client_writes_it_the_same_way()
    {
        var repo = Repo([(Library("l1", "movies", "/data/movies"), ["c1"])]);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [Mapping("/data/movies", "/data/movies")], CancellationToken.None);

        Assert.Equal(new ClientSaveFolders.Resolved("/data/movies", "/data/movies"), resolved);
    }

    [Theory]
    [InlineData("two")]
    [InlineData("nopath")]
    [InlineData("none")]
    [InlineData("blank")]
    [InlineData("relative")]
    [InlineData("wrongcategory")]
    [InlineData("otherclient")]
    public async Task No_folder_is_resolved_when_the_libraries_disagree_or_there_is_nothing_usable(string case_)
    {
        var libs = case_ switch
        {
            "two" => new[] { (Library("l1", "movies", "/data/a"), new[] { "c1" }), (Library("l2", "movies", "/data/b"), new[] { "c1" }) },
            "nopath" => new[] { (Library("l1", "movies", null), new[] { "c1" }) },
            "none" => Array.Empty<(LibraryItem, string[])>(),
            "blank" => new[] { (Library("l1", "movies", "  "), new[] { "c1" }) },
            "relative" => new[] { (Library("l1", "movies", "downloads"), new[] { "c1" }) },
            "wrongcategory" => new[] { (Library("l1", "movies", "/data/a"), new[] { "c1|other" }) },
            _ => new[] { (Library("l1", "movies", "/data/a"), new[] { "c2" }) }
        };

        Assert.Null(await ClientSaveFolders.ResolveAsync(Repo(libs).Object, Client(), "movies", "movies", [], CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_below_a_mapped_one_that_the_client_writes_the_same_way_is_covered_too()
    {
        var repo = Repo([(Library("l1", "movies", "/data/movies"), ["c1"])]);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client("example.invalid"), "movies", "movies", [Mapping("/data", "/data")], CancellationToken.None);

        Assert.Equal(new ClientSaveFolders.Resolved("/data/movies", "/data/movies"), resolved);
    }

    [Fact]
    public async Task A_mapping_that_is_off_or_has_no_remote_side_does_not_cover_the_folder()
    {
        var repo = Repo([(Library("l1", "movies", "/data/a"), ["c1"])]);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client("example.invalid"), "movies", "movies", [Mapping("/data/a", "/x", enabled: false), Mapping("/data/a", ""), Mapping("", "/y")], CancellationToken.None);

        Assert.Null(resolved!.ClientFolder);
    }

    [Fact]
    public async Task A_library_with_no_routing_at_all_is_not_routed_to_the_client()
    {
        var repo = new Mock<ILibrariesRepository>();
        repo.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("l1", "movies", "/data/a")]);
        repo.Setup(r => r.GetLibraryRoutingAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync((LibraryRoutingSnapshot?)null);

        Assert.Null(await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [], CancellationToken.None));
    }

    [Fact]
    public async Task A_link_with_its_own_category_is_routed_by_that_category()
    {
        var repo = Repo([(Library("l1", "movies", "/data/a"), ["c1|Kids"])]);

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "kids", [Mapping("/data", "/dl")], CancellationToken.None);

        Assert.Equal("/dl/a", resolved!.ClientFolder);
    }

    [Fact]
    public async Task An_unmapped_folder_is_named_to_a_client_on_another_machine_as_none_and_no_question_is_asked()
    {
        var repo = Repo([(Library("l1", "movies", "/data/a"), ["c1"])]);
        var asked = 0;

        var resolved = await ClientSaveFolders.ResolveAsync(repo.Object, Client("example.invalid"), "movies", "movies", [], CancellationToken.None, _ => { asked++; return Task.FromResult<string?>("/data/a"); });

        Assert.Equal(new ClientSaveFolders.Resolved("/data/a", null), resolved);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task An_unmapped_folder_on_this_machine_is_named_only_when_the_client_reports_a_folder_that_exists_here()
    {
        var repo = Repo([(Library("l1", "movies", "/data/a"), ["c1"])]);
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        try
        {
            var shared = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [], CancellationToken.None, _ => Task.FromResult<string?>("/data/a"));
            var silent = await ClientSaveFolders.ResolveAsync(repo.Object, Client(), "movies", "movies", [], CancellationToken.None);
            var inContainer = ClientSaveFolders.RunningInContainer();

            Assert.Equal(inContainer ? null : "/data/a", shared!.ClientFolder);
            Assert.Null(silent!.ClientFolder);
        }
        finally
        {
            ClientSaveFolders.DirectoryExistsOverride.Value = null;
        }
    }

    [Fact]
    public void The_needs_mapping_sentence_tells_the_person_what_to_add()
    {
        Assert.EndsWith("Add a path mapping for the library's downloads folder, and Deluno will send each download there.", ClientSaveFolders.NeedsMappingMessage);
    }
}
