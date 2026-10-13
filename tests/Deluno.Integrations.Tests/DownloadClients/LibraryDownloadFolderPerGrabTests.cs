using System.Net;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1073: Transmission has labels but no folder per label, and Deluge's label
/// has a folder only when someone set one by hand. Deluno names the library's
/// downloads folder on every grab instead of trusting the client's own setup.
/// </summary>
public sealed class LibraryDownloadFolderPerGrabTests
{
    private const string LibraryFolder = @"C:\Downloads\Completed\Movies";

    // The client answers are kept for a minute; every test starts with none kept.
    public LibraryDownloadFolderPerGrabTests() => FolderChainService.ClearCategoryCache();

    // ------------------------------------------------------------ adapters

    [Fact]
    public async Task Transmission_adds_the_torrent_into_the_libraries_folder_without_asking_for_its_own()
    {
        var sent = new List<string>();
        await new TransmissionDownloadClient(new StubFactory(new TransmissionHandler(sent)))
            .GrabAsync(Client("transmission"), Request() with { SaveFolder = LibraryFolder }, CancellationToken.None);

        var add = Assert.Single(sent);
        using var document = JsonDocument.Parse(add);
        var arguments = document.RootElement.GetProperty("arguments");
        Assert.Equal("torrent-add", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(LibraryFolder, arguments.GetProperty("download-dir").GetString());
        Assert.Equal("movies", Assert.Single(arguments.GetProperty("labels").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Transmission_with_no_library_folder_still_uses_its_download_folder_plus_the_category()
    {
        var sent = new List<string>();
        await new TransmissionDownloadClient(new StubFactory(new TransmissionHandler(sent)))
            .GrabAsync(Client("transmission"), Request(), CancellationToken.None);

        using var document = JsonDocument.Parse(sent.Single(body => body.Contains("\"torrent-add\"", StringComparison.Ordinal)));
        Assert.Equal(@"C:\Downloads\movies", document.RootElement.GetProperty("arguments").GetProperty("download-dir").GetString());
    }

    [Fact]
    public async Task Deluge_adds_the_torrent_with_the_libraries_folder_as_where_it_ends_up_and_leaves_where_it_downloads_to_the_client()
    {
        var sent = new List<string>();
        await new DelugeDownloadClient(new StubFactory(new DelugeHandler(sent)))
            .GrabAsync(Client("deluge"), Request() with { SaveFolder = LibraryFolder }, CancellationToken.None);

        using var add = JsonDocument.Parse(sent.First(body => body.Contains("core.add_torrent_magnet", StringComparison.Ordinal)));
        var options = add.RootElement.GetProperty("params")[1];
        // #1164: the in-progress folder (download_location) stays Deluge's own.
        Assert.False(options.TryGetProperty("download_location", out _));
        Assert.True(options.GetProperty("move_completed").GetBoolean());
        Assert.Equal(LibraryFolder, options.GetProperty("move_completed_path").GetString());
    }

    [Fact]
    public async Task Deluge_says_the_folder_again_after_the_label_so_the_labels_own_folder_cannot_override_it()
    {
        var sent = new List<string>();
        await new DelugeDownloadClient(new StubFactory(new DelugeHandler(sent)))
            .GrabAsync(Client("deluge"), Request() with { SaveFolder = LibraryFolder }, CancellationToken.None);

        var methods = sent.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("method").GetString()).ToArray();
        var labelIndex = Array.IndexOf(methods, "label.set_torrent");
        var pinIndex = Array.IndexOf(methods, "core.set_torrent_options");
        Assert.True(labelIndex >= 0 && pinIndex > labelIndex, string.Join(",", methods));

        using var pin = JsonDocument.Parse(sent[pinIndex]);
        var parameters = pin.RootElement.GetProperty("params");
        Assert.Equal("5422d19ec8580f2821a8ffa67236cf7ed86fc07d", Assert.Single(parameters[0].EnumerateArray()).GetString());
        Assert.True(parameters[1].GetProperty("move_completed").GetBoolean());
        Assert.Equal(LibraryFolder, parameters[1].GetProperty("move_completed_path").GetString());
    }

    [Fact]
    public async Task Deluge_with_no_library_folder_sends_no_folder_options_at_all()
    {
        var sent = new List<string>();
        await new DelugeDownloadClient(new StubFactory(new DelugeHandler(sent)))
            .GrabAsync(Client("deluge"), Request(), CancellationToken.None);

        using var add = JsonDocument.Parse(sent.First(body => body.Contains("core.add_torrent_magnet", StringComparison.Ordinal)));
        var options = add.RootElement.GetProperty("params")[1];
        Assert.False(options.TryGetProperty("download_location", out _));
        Assert.False(options.TryGetProperty("move_completed_path", out _));
        Assert.DoesNotContain(sent, body => body.Contains("core.set_torrent_options", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ mappings

    [Fact]
    public void A_folder_is_written_the_way_a_client_behind_a_path_mapping_sees_it()
    {
        var mapping = Mapping("/downloads", @"C:\Downloads\Completed");

        Assert.Equal("/downloads/Movies", ClientSaveFolders.ToClientPath(@"C:\Downloads\Completed\Movies", [mapping]));
        Assert.Equal("/downloads", ClientSaveFolders.ToClientPath(@"C:\Downloads\Completed", [mapping]));
        Assert.Equal(@"D:\Other\Movies", ClientSaveFolders.ToClientPath(@"D:\Other\Movies", [mapping]));
    }

    [Fact]
    public void The_longest_matching_mapping_wins_when_writing_a_folder_for_a_client()
    {
        var broad = Mapping("/downloads", @"C:\Downloads");
        var narrow = Mapping("/movies", @"C:\Downloads\Completed\Movies");

        Assert.Equal("/movies/Sub", ClientSaveFolders.ToClientPath(@"C:\Downloads\Completed\Movies\Sub", [broad, narrow]));
    }

    // ------------------------------------------------------------ the grab service names the folder

    [Fact]
    public async Task A_grab_to_a_client_without_categories_carries_the_routed_librarys_downloads_folder()
    {
        var (request, _) = await GrabThroughService(
            [Library("lib-1", LibraryFolder)], perGrabClient: true, mappings: []);

        Assert.Equal(LibraryFolder, request?.SaveFolder);
    }

    [Fact]
    public async Task The_folder_is_written_through_the_clients_path_mapping()
    {
        var (request, _) = await GrabThroughService(
            [Library("lib-1", LibraryFolder)], perGrabClient: true, mappings: [Mapping("/downloads", @"C:\Downloads\Completed")]);

        Assert.Equal("/downloads/Movies", request?.SaveFolder);
    }

    [Fact]
    public async Task Two_libraries_sharing_a_category_that_disagree_leave_the_clients_own_folder_alone()
    {
        var (request, _) = await GrabThroughService(
            [Library("lib-1", LibraryFolder), Library("lib-2", @"C:\Downloads\Completed\Films")], perGrabClient: true, mappings: []);

        Assert.Null(request?.SaveFolder);
    }

    [Fact]
    public async Task A_client_with_its_own_category_folders_is_not_given_one()
    {
        var (request, _) = await GrabThroughService(
            [Library("lib-1", LibraryFolder)], perGrabClient: false, mappings: []);

        Assert.Null(request?.SaveFolder);
    }

    // ------------------------------------------------------------ the folder check and the processor API

    [Fact]
    public async Task The_folder_check_says_Deluno_sends_the_grab_to_the_library_folder_whatever_the_client_is_set_to()
    {
        var report = await Chain([Library("lib-1", LibraryFolder)], perGrabClient: true, clientReportsFolder: @"C:\Somewhere\Else");

        var chain = Assert.Single(report!.Clients);
        Assert.Equal(FolderChainSavedBy.DelunoPerGrab, chain.SavedBy);
        Assert.Equal(LibraryFolder, chain.SaveFolder);
        var folder = chain.Links.Single(link => link.Step == "folder");
        Assert.Equal(FolderChainStatuses.Ok, folder.Status);
        Assert.Contains("Deluno asks", folder.Message);
    }

    [Fact]
    public async Task The_folder_check_still_flags_a_client_whose_own_folder_is_wrong_when_Deluno_cannot_name_one()
    {
        var report = await Chain(
            [Library("lib-1", LibraryFolder), Library("lib-2", @"C:\Downloads\Completed\Films")], perGrabClient: true, clientReportsFolder: @"C:\Somewhere\Else");

        var chain = Assert.Single(report!.Clients);
        Assert.Equal(FolderChainSavedBy.ClientCategory, chain.SavedBy);
        Assert.Equal(FolderChainStatuses.Problem, chain.Links.Single(link => link.Step == "folder").Status);
    }

    [Fact]
    public async Task The_processor_is_told_each_librarys_client_category_and_save_folder()
    {
        var service = ChainService([Library("lib-1", LibraryFolder)], perGrabClient: true, clientReportsFolder: @"C:\Somewhere\Else");

        var destinations = await service.ListDestinationsAsync(null, CancellationToken.None);

        var library = Assert.Single(destinations!);
        Assert.Equal("lib-1", library.LibraryId);
        Assert.Equal(LibraryFolder, library.DownloadsPath);
        var destination = Assert.Single(library.Destinations);
        Assert.Equal("client-1", destination.DownloadClientId);
        Assert.Equal("Deluge", destination.DownloadClientName);
        Assert.Equal("deluge", destination.Protocol);
        Assert.Equal("movies", destination.Category);
        Assert.Equal("label", destination.CategoryKind);
        Assert.Equal(LibraryFolder, destination.SaveFolder);
        Assert.Equal(LibraryFolder, destination.ClientSaveFolder);
        Assert.Equal(FolderChainSavedBy.DelunoPerGrab, destination.SavedBy);
        Assert.True(destination.MatchesDownloadsPath);
        Assert.Equal(FolderChainStatuses.Ok, destination.Status);
    }

    [Fact]
    public async Task An_unknown_library_has_no_destinations()
    {
        var service = ChainService([Library("lib-1", LibraryFolder)], perGrabClient: true, clientReportsFolder: null);

        Assert.Null(await service.ListDestinationsAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task A_library_using_a_processor_lists_that_connections_enabled_path_mappings_in_both_directions_named()
    {
        var library = Library("lib-1", LibraryFolder) with { ImportWorkflow = "refine-before-import", ProcessorName = "Weir" };
        var processors = Processors(
            new ProcessorConnectionItem("proc-1", "Weir", "generic", "http://weir.test/intake/webhook/deluno", "Authorization", null, true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [ProcMapping("m1", @"C:\Downloads\Completed", "/downloads", true, 1), ProcMapping("m2", @"E:\Weir\Ready", "/ready", false, 2)]);
        var service = new FolderChainService(Libraries([library]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors);

        var listed = Assert.Single((await service.ListDestinationsAsync(null, CancellationToken.None))!);

        Assert.Equal("proc-1", listed.ProcessorConnection!.Id);
        Assert.Equal("Weir", listed.ProcessorConnection.Name);
        var mapping = Assert.Single(listed.ProcessorConnection.PathMappings);
        Assert.Equal(@"C:\Downloads\Completed", mapping.DelunoPath);
        Assert.Equal("/downloads", mapping.ProcessorPath);
    }

    [Fact]
    public async Task The_processor_is_checked_in_two_lines_what_it_watches_against_downloads_and_what_it_writes_to_against_ready()
    {
        var library = Library("lib-1", LibraryFolder) with { ImportWorkflow = "refine-before-import", ProcessorName = "Weir", ProcessorOutputPath = @"E:\Weir\Ready\Movies" };
        var connection = new ProcessorConnectionItem("proc-1", "Weir", "generic", "http://weir.test/intake/webhook/deluno", "Authorization", null, true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var processors = Processors(connection, [ProcMapping("m1", @"C:\Downloads\Completed", "/downloads", true, 1), ProcMapping("m2", @"E:\Weir\Ready", "/ready", true, 2)]);
        var processorService = new Mock<IProcessorConnectionService>();
        processorService.Setup(instance => instance.GetStatusSupportAsync(connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { HasLibraryFolders = true });
        processorService.Setup(instance => instance.GetLibraryFoldersAsync(connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorLibraryFolder("lib-1", "/downloads/Movies", true, "/ready/Movies")]);
        var service = new FolderChainService(Libraries([library]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors, processorService: processorService.Object);

        var report = (await service.CheckAsync("lib-1", CancellationToken.None))!;

        var watches = Assert.Single(report.LibraryLinks, link => link.Step == "processor");
        Assert.Equal(FolderChainStatuses.Ok, watches.Status);
        Assert.Contains("downloads folder", watches.Message);
        var writes = Assert.Single(report.LibraryLinks, link => link.Step == "processor-output");
        Assert.Equal(FolderChainStatuses.Ok, writes.Status);
        Assert.Contains("ready folder", writes.Message);

        processorService.Setup(instance => instance.GetLibraryFoldersAsync(connection, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorLibraryFolder("lib-1", "/downloads/Movies", true, "/elsewhere")]);
        var wrong = (await new FolderChainService(Libraries([library]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors, processorService: processorService.Object)
            .CheckAsync("lib-1", CancellationToken.None))!;
        Assert.Equal(FolderChainStatuses.Ok, Assert.Single(wrong.LibraryLinks, link => link.Step == "processor").Status);
        Assert.Equal(FolderChainStatuses.Problem, Assert.Single(wrong.LibraryLinks, link => link.Step == "processor-output").Status);
    }

    [Fact]
    public async Task A_library_with_no_processor_has_no_processor_connection_in_the_list()
    {
        var processors = Processors(
            new ProcessorConnectionItem("proc-1", "Weir", "generic", "http://weir.test", "Authorization", null, true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [ProcMapping("m1", "/a", "/b", true, 1)]);
        var service = new FolderChainService(Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors);

        Assert.Null(Assert.Single((await service.ListDestinationsAsync(null, CancellationToken.None))!).ProcessorConnection);
    }

    [Fact]
    public async Task The_processor_connection_serialises_with_the_documented_field_names()
    {
        var library = Library("lib-1", LibraryFolder) with { ImportWorkflow = "refine-before-import", ProcessorName = "Weir" };
        var processors = Processors(
            new ProcessorConnectionItem("proc-1", "Weir", "generic", "http://weir.test", "Authorization", null, true, "healthy", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [ProcMapping("m1", "/d", "/p", true, 1)]);
        var service = new FolderChainService(Libraries([library]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors);

        var json = JsonSerializer.Serialize((await service.ListDestinationsAsync(null, CancellationToken.None))!, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var connection = document.RootElement[0].GetProperty("processorConnection");
        Assert.Equal("proc-1", connection.GetProperty("id").GetString());
        Assert.Equal("Weir", connection.GetProperty("name").GetString());
        var pair = connection.GetProperty("pathMappings")[0];
        Assert.Equal("/d", pair.GetProperty("delunoPath").GetString());
        Assert.Equal("/p", pair.GetProperty("processorPath").GetString());
    }

    // ------------------------------------------------------------ a client on another machine (#1073)

    [Fact]
    public async Task A_client_on_another_machine_with_no_path_mapping_is_told_no_folder()
    {
        var (request, _) = await GrabThroughService([Library("lib-1", LibraryFolder)], perGrabClient: true, mappings: [], host: "192.168.1.51");

        Assert.Null(request?.SaveFolder);
    }

    [Fact]
    public async Task A_client_on_another_machine_with_a_path_mapping_is_told_the_mapped_folder()
    {
        var (request, _) = await GrabThroughService(
            [Library("lib-1", LibraryFolder)], perGrabClient: true, mappings: [Mapping("/downloads", @"C:\Downloads\Completed")], host: "192.168.1.51");

        Assert.Equal("/downloads/Movies", request?.SaveFolder);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void The_same_machine_is_recognised(string host)
        => Assert.True(ClientSaveFolders.IsSameMachine(Client("deluge", host)));

    [Theory]
    [InlineData(@"C:\Downloads\Movies", "/downloads/movies", true, false)] // Linux client in Docker Desktop / WSL2 on localhost
    [InlineData("/data/Movies", @"D:\torrents", true, false)] // Windows client
    [InlineData(@"C:\Downloads\Movies", @"D:\torrents\movies", true, true)]
    [InlineData("/data/Movies", "/var/lib/transmission/movies", true, true)]
    [InlineData("/mnt/media/downloads/Movies", "/downloads", false, false)] // a container's own /downloads is not a folder on this host
    [InlineData("/mnt/media/downloads/Movies", "/downloads", true, true)] // unless the same path really exists here (a bind mount)
    [InlineData(@"C:\Downloads\Movies", null, true, false)] // the client did not say: nothing is named
    [InlineData(@"C:\Downloads\Movies", "Downloads", true, false)] // a relative name says nothing about the file system
    public void A_same_machine_client_is_trusted_only_with_evidence_it_shares_this_hosts_folders(string host, string? reported, bool existsOnHost, bool expected)
    {
        // The "exists on this host" answer is only asked when both sides use the same path style.
        Assert.Equal(expected, ClientSaveFolders.CanShareHostFolder(true, false, host, reported, _ => existsOnHost));
    }

    [Fact]
    public void A_client_on_another_machine_or_a_Deluno_in_a_container_needs_a_mapping()
    {
        Assert.False(ClientSaveFolders.CanShareHostFolder(false, false, "/data", "/data"));
        Assert.False(ClientSaveFolders.CanShareHostFolder(true, true, "/data", "/data"));
    }

    [Fact]
    public async Task A_localhost_client_that_reports_a_posix_folder_is_told_no_windows_folder()
    {
        var (request, _) = await GrabThroughService([Library("lib-1", @"C:\Downloads\Completed\Movies")], perGrabClient: true, mappings: [], reportedFolder: "/downloads/movies");
        Assert.Null(request?.SaveFolder);

        var (mapped, _) = await GrabThroughService(
            [Library("lib-1", @"C:\Downloads\Completed\Movies")], perGrabClient: true, mappings: [Mapping("/downloads", @"C:\Downloads\Completed")], reportedFolder: "/downloads/movies");
        Assert.Equal("/downloads/Movies", mapped?.SaveFolder);
    }

    [Fact]
    public async Task A_localhost_client_that_does_not_answer_the_check_is_sent_no_folder()
    {
        var (request, _) = await GrabThroughService([Library("lib-1", @"C:\Downloads\Completed\Movies")], perGrabClient: true, mappings: [], reportedFolder: "");

        Assert.Null(request?.SaveFolder);
    }

    [Fact]
    public async Task A_posix_client_in_a_container_on_localhost_is_sent_no_host_folder_but_is_when_the_folder_really_exists_here()
    {
        var library = Library("lib-1", "/mnt/media/downloads/Movies");
        var (notShared, _) = await GrabThroughService([library], perGrabClient: true, mappings: [], reportedFolder: "/downloads", existsOnHost: _ => false);
        Assert.Null(notShared?.SaveFolder);

        var (shared, _) = await GrabThroughService([library], perGrabClient: true, mappings: [], reportedFolder: "/downloads", existsOnHost: _ => true);
        Assert.Equal("/mnt/media/downloads/Movies", shared?.SaveFolder);

        // A path mapping is the way to say the folders are shared.
        var (mapped, _) = await GrabThroughService(
            [library], perGrabClient: true, mappings: [Mapping("/downloads", "/mnt/media/downloads")], reportedFolder: "/downloads", existsOnHost: _ => false);
        Assert.Equal("/downloads/Movies", mapped?.SaveFolder);
    }

    [Fact]
    public async Task The_upgrade_note_is_written_for_the_clients_that_answered_even_when_another_did_not_and_waits_for_the_silent_one_only_a_few_starts()
    {
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var marker = Path.Combine(Path.GetTempPath(), "deluno-notice-" + Guid.NewGuid().ToString("n"), "done");
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var activity = new Mock<IActivityFeedRepository>();
        var health = new Mock<IHealthCheckRepository>();
        health.Setup(repository => repository.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var client = FakeClient(true, @"C:\Somewhere\Old");
        var unreachable = new DownloadClientCategoryCheckResult("client-2", "Silent", "movies", DownloadClientCategoryStatuses.Unreachable, "Down.", true, false);
        var libraries = Libraries([Library("lib-1", LibraryFolder), Library("lib-b", LibraryFolder)]);
        var connections = TwoClients("client-2");
        client.Setup(instance => instance.CheckCategoryAsync(It.Is<DownloadClientItem>(item => item.Id == "client-2"), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unreachable);

        var first = await DownloadFolderChangeNoticeService.RunOnceAsync(libraries, connections, Registry(client.Object), activity.Object, health.Object, marker, CancellationToken.None);

        Assert.True(first);
        activity.Verify(repository => repository.RecordActivityAsync("downloads.folder-changed", It.IsAny<string>(), null, null, "download-client", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(File.Exists(marker));

        // The answered client is covered; the silent one is asked again, and the note is not repeated.
        await DownloadFolderChangeNoticeService.RunOnceAsync(libraries, connections, Registry(client.Object), activity.Object, health.Object, marker, CancellationToken.None);
        Assert.False(File.Exists(marker));
        await DownloadFolderChangeNoticeService.RunOnceAsync(libraries, connections, Registry(client.Object), activity.Object, health.Object, marker, CancellationToken.None);
        Assert.True(File.Exists(marker));
        activity.Verify(repository => repository.RecordActivityAsync("downloads.folder-changed", It.IsAny<string>(), null, null, "download-client", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_upgrade_note_waits_for_a_client_that_did_not_answer_and_leaves_no_marker()
    {
        var marker = Path.Combine(Path.GetTempPath(), "deluno-notice-" + Guid.NewGuid().ToString("n"), "done");
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var activity = new Mock<IActivityFeedRepository>();
        var health = new Mock<IHealthCheckRepository>();
        var client = FakeClient(true, null);
        client.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Deluge", "movies", DownloadClientCategoryStatuses.Unreachable, "Down.", true, false));
        var wrote = await DownloadFolderChangeNoticeService.RunOnceAsync(
            Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []),
            Registry(client.Object), activity.Object, health.Object, marker, CancellationToken.None);

        Assert.False(wrote);
        Assert.False(File.Exists(marker));
        activity.VerifyNoOtherCalls();
        health.VerifyNoOtherCalls();
    }

    [Fact]
    public void Another_machine_is_not_the_same_machine()
        => Assert.False(ClientSaveFolders.IsSameMachine(Client("deluge", "192.168.1.51")));

    [Fact]
    public async Task The_folder_check_says_plainly_why_no_folder_is_named_for_a_remote_client_without_a_mapping()
    {
        var service = ChainService([Library("lib-1", LibraryFolder)], perGrabClient: true, clientReportsFolder: @"C:\Somewhere\Else", host: "192.168.1.51");

        var report = await service.CheckAsync("lib-1", CancellationToken.None);

        var chain = Assert.Single(report!.Clients);
        Assert.Equal(FolderChainSavedBy.ClientCategory, chain.SavedBy);
        var folder = chain.Links.Single(link => link.Step == "folder");
        Assert.Equal(FolderChainStatuses.Problem, folder.Status);
        Assert.Contains("Add a path mapping", folder.Message);
    }

    // ------------------------------------------------------------ the destinations list stays bounded (#1073)

    [Fact]
    public async Task The_destinations_list_asks_each_client_once_a_minute_however_often_it_is_polled()
    {
        FolderChainService.ClearCategoryCache();
        var fake = FakeClient(perGrab: true, reportedFolder: LibraryFolder);
        var service = ChainService([Library("lib-1", LibraryFolder), Library("lib-2", LibraryFolder)], true, null, fake: fake);

        await service.ListDestinationsAsync(null, CancellationToken.None);
        await service.ListDestinationsAsync(null, CancellationToken.None);

        fake.Verify(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtMost(2));
        fake.Verify(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task A_client_that_never_answers_is_given_up_on_and_reported_as_unknown()
    {
        var fake = FakeClient(perGrab: true, reportedFolder: null);
        fake.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (DownloadClientItem _, string _, CancellationToken token) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return null!;
            });
        var service = new FolderChainService(
            Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []), Registry(fake.Object), null, TimeSpan.FromMilliseconds(200));

        var started = DateTimeOffset.UtcNow;
        var listed = await service.ListDestinationsAsync(null, CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        var destination = Assert.Single(Assert.Single(listed!).Destinations);
        Assert.Equal(FolderChainStatuses.Unknown, destination.Status);
    }

    [Fact]
    public async Task Every_routed_client_is_checked_at_once_and_the_links_keep_their_order()
    {
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var now = DateTimeOffset.UnixEpoch;
        var library = Library("lib-1", LibraryFolder);
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        repository.Setup(instance => instance.GetLibraryRoutingAsync("lib-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryRoutingSnapshot("lib-1", library.Name, [],
                [.. new[] { "client-a", "client-b", "client-c" }.Select((id, index) => new LibraryDownloadClientLinkItem($"link-{id}", "lib-1", id, id, index + 1, now, now, "movies"))]));
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. new[] { "client-a", "client-b", "client-c" }.Select(id => Client("deluge") with { Id = id, Name = id })]);
        connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var fake = FakeClient(perGrab: false, reportedFolder: LibraryFolder);
        fake.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (DownloadClientItem item, string category, CancellationToken _) =>
            {
                await Task.Delay(500);
                return new DownloadClientCategoryCheckResult(item.Id, item.Name, category, DownloadClientCategoryStatuses.Ready, "Ready.", true, true, LibraryFolder);
            });
        var service = new FolderChainService(repository.Object, connections.Object, Registry(fake.Object));

        var started = DateTimeOffset.UtcNow;
        var report = await service.CheckAsync("lib-1", CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(1300), "three 500 ms clients took as long as one after another");
        Assert.Equal(["client-a", "client-b", "client-c"], report!.Clients.Select(chain => chain.ClientId).ToArray());
    }

    // ------------------------------------------------------------ the upgrade note (#1073)

    [Fact]
    public async Task After_an_upgrade_a_library_whose_client_used_to_save_elsewhere_gets_one_Activity_line_and_a_Health_note()
    {
        var marker = Path.Combine(Path.GetTempPath(), "deluno-notice-" + Guid.NewGuid().ToString("n"), "done");
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var activity = new Mock<IActivityFeedRepository>();
        var health = new Mock<IHealthCheckRepository>();
        try
        {
            var wrote = await DownloadFolderChangeNoticeService.RunOnceAsync(
                Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []),
                Registry(FakeClient(true, @"C:\Somewhere\Else").Object), activity.Object, health.Object, marker, CancellationToken.None);

            Assert.True(wrote);
            activity.Verify(instance => instance.RecordActivityAsync(
                "downloads.folder-changed", It.Is<string>(text => text.Contains(@"C:\Somewhere\Else") && text.Contains(LibraryFolder) && text.Contains("point it at")),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            health.Verify(instance => instance.UpsertResultsAsync(
                It.Is<IReadOnlyList<HealthCheckOutcome>>(items => items.Count == 1 && items[0].Id == DownloadFolderChangeNoticeService.HealthId
                    && items[0].Status == HealthCheckStatuses.Problem && items[0].Category == HealthCheckCategories.Notice),
                It.IsAny<CancellationToken>()), Times.Once);

            // Once only: the marker stops a second start saying it again.
            Assert.False(await DownloadFolderChangeNoticeService.RunOnceAsync(
                Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []),
                Registry(FakeClient(true, @"C:\Somewhere\Else").Object), activity.Object, health.Object, marker, CancellationToken.None));
            activity.Verify(instance => instance.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(marker)!, recursive: true);
        }
    }

    [Fact]
    public async Task No_note_is_written_when_the_client_already_saved_into_the_librarys_folder()
    {
        var marker = Path.Combine(Path.GetTempPath(), "deluno-notice-" + Guid.NewGuid().ToString("n"), "done");
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        var activity = new Mock<IActivityFeedRepository>();
        var health = new Mock<IHealthCheckRepository>();
        try
        {
            var wrote = await DownloadFolderChangeNoticeService.RunOnceAsync(
                Libraries([Library("lib-1", LibraryFolder)]), Connections("deluge", []),
                Registry(FakeClient(true, LibraryFolder).Object), activity.Object, health.Object, marker, CancellationToken.None);

            Assert.False(wrote);
            activity.VerifyNoOtherCalls();
            health.VerifyNoOtherCalls();
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(marker)!, recursive: true);
        }
    }

    private static IProcessorRepository Processors(ProcessorConnectionItem connection, IReadOnlyList<ProcessorPathMappingItem> mappings)
    {
        var repository = new Mock<IProcessorRepository>();
        repository.Setup(instance => instance.FindProcessorConnectionAsync(It.IsAny<string?>(), connection.Name, It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        repository.Setup(instance => instance.ListProcessorPathMappingsAsync(connection.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mappings);
        return repository.Object;
    }

    private static ProcessorPathMappingItem ProcMapping(string id, string deluno, string processor, bool enabled, int priority)
        => new(id, "proc-1", deluno, processor, enabled, priority, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------ fixtures

    private static DownloadClientGrabRequest Request()
        => new("Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem Client(string protocol, string host = "localhost")
        => new(
            "client-1", protocol == "deluge" ? "Deluge" : "Transmission", protocol, host, 8112, null, null, "http://" + host + ":8112/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DownloadClientPathMappingItem Mapping(string remote, string local)
        => new("map-1", "client-1", remote, local, true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static LibraryItem Library(string id, string downloadsPath)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, "Movies", "movies", "main", "/data/library/movies", downloadsPath, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static ILibrariesRepository Libraries(IReadOnlyList<LibraryItem> libraries)
    {
        var now = DateTimeOffset.UnixEpoch;
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        foreach (var library in libraries)
        {
            repository.Setup(instance => instance.GetLibraryRoutingAsync(library.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryRoutingSnapshot(
                    library.Id, library.Name, [],
                    [new LibraryDownloadClientLinkItem($"link-{library.Id}", library.Id, library.Id.StartsWith("lib-b", StringComparison.Ordinal) ? "client-2" : "client-1", "Deluge", 1, now, now, "movies")]));
        }

        return repository.Object;
    }

    private static IConnectionsRepository Connections(string protocol, IReadOnlyList<DownloadClientPathMappingItem> mappings, string host = "localhost")
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Client(protocol, host)]);
        repository.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(mappings);
        return repository.Object;
    }

    private static IConnectionsRepository TwoClients(string secondId)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Client("deluge"), Client("deluge") with { Id = secondId, Name = "Silent" }]);
        repository.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return repository.Object;
    }

    private static Mock<IDownloadClient> FakeClient(bool perGrab, string? reportedFolder)
    {
        var client = new Mock<IDownloadClient>();
        client.SetupGet(instance => instance.SetsSaveFolderPerGrab).Returns(perGrab);
        client.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Big.Buck.Bunny", true, "sent", "Sent.") { ExternalId = "abc" });
        client.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Deluge", "movies", DownloadClientCategoryStatuses.Ready, "Ready.", true, true, reportedFolder, IncompletePath: "/incomplete/deluge"));
        return client;
    }

    private static IDownloadClientRegistry Registry(IDownloadClient client)
    {
        var registry = new Mock<IDownloadClientRegistry>();
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out client)).Returns(true);
        return registry.Object;
    }

    private static async Task<(DownloadClientGrabRequest? Request, Mock<IDownloadClient> Client)> GrabThroughService(
        IReadOnlyList<LibraryItem> libraries, bool perGrabClient, IReadOnlyList<DownloadClientPathMappingItem> mappings, string host = "localhost", string? reportedFolder = null, Func<string, bool>? existsOnHost = null)
    {
        // A client that shares this machine's folders reports one that exists here; a test says otherwise.
        ClientSaveFolders.DirectoryExistsOverride.Value = existsOnHost ?? (_ => true);
        var fake = FakeClient(perGrabClient, reportedFolder ?? @"C:\Downloads\ClientOwn");
        DownloadClientGrabRequest? seen = null;
        fake.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) => seen = request)
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Big.Buck.Bunny", true, "sent", "Sent.") { ExternalId = "abc" });

        var service = new DownloadClientGrabService(
            Connections("deluge", mappings, host),
            Registry(fake.Object),
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            trackerIndexers: null,
            librariesRepository: Libraries(libraries),
            healthCheckRepository: null,
            activityFeedRepository: Mock.Of<IActivityFeedRepository>(),
            notificationService: Mock.Of<IOutboundNotificationService>());

        await service.GrabAsync("client-1", new DownloadClientGrabRequest(
            "Big.Buck.Bunny", "magnet:?xt=urn:btih:de4158016fbc49ebf35641903cec65a0ad0d30b5", "movies", "movies", null), CancellationToken.None);
        return (seen, fake);
    }

    private static FolderChainService ChainService(IReadOnlyList<LibraryItem> libraries, bool perGrabClient, string? clientReportsFolder, string host = "localhost", IReadOnlyList<DownloadClientPathMappingItem>? mappings = null, Mock<IDownloadClient>? fake = null)
    {
        ClientSaveFolders.DirectoryExistsOverride.Value = _ => true;
        return new(Libraries(libraries), Connections("deluge", mappings ?? [], host), Registry((fake ?? FakeClient(perGrabClient, clientReportsFolder)).Object));
    }

    private static Task<FolderChainReport?> Chain(IReadOnlyList<LibraryItem> libraries, bool perGrabClient, string? clientReportsFolder)
        => ChainService(libraries, perGrabClient, clientReportsFolder).CheckAsync(libraries[0].Id, CancellationToken.None);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Transmission 4.1.3 as recorded: the add (answered as it answered the recorded magnet) and its session settings, whose download
    /// folder is the one these tests expect when the library names none.
    /// </summary>
    private sealed class TransmissionHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            return body.Contains("\"torrent-add\"", StringComparison.Ordinal) ? RecordedReplies.Transmission("magnet-no-metadata").Response("torrent-add")
                : body.Contains("\"session-get\"", StringComparison.Ordinal)
                    ? RecordedReplies.Json(RecordedReplies.Transmission("settings").Body("session-get", ("arguments.download-dir", @"C:\Downloads")))
                    : throw new InvalidOperationException("Transmission was asked something no recording answers: " + body);
        }
    }

    /// <summary>Deluge 2.2.0 as recorded, whose Label plugin is on and already holds a "movies" label.</summary>
    private sealed class DelugeHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            if (body.Contains("core.add_torrent_", StringComparison.Ordinal)) return RecordedReplies.Deluge("magnet-no-metadata").Response("core.add_torrent_magnet");
            if (body.Contains("label.get_labels", StringComparison.Ordinal)) return RecordedReplies.Json(RecordedDelugeRig.Labels());
            if (body.Contains("label.set_torrent", StringComparison.Ordinal)) return RecordedReplies.Deluge("label-set").Response("label.set_torrent");
            if (body.Contains("core.set_torrent_options", StringComparison.Ordinal)) return RecordedReplies.Deluge("torrent-options").Response("core.set_torrent_options");
            if (body.Contains("auth.login", StringComparison.Ordinal)) return RecordedReplies.Deluge("sign-in").Response("auth.login");
            if (body.Contains("web.connected", StringComparison.Ordinal)) return RecordedReplies.Deluge("sign-in").Response("web.connected");
            throw new InvalidOperationException("Deluge was asked something no recording answers: " + body);
        }
    }

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
