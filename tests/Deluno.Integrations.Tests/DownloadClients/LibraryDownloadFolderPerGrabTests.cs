using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
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
    public async Task Deluge_adds_the_torrent_with_the_libraries_folder_as_where_it_downloads_and_where_it_ends_up()
    {
        var sent = new List<string>();
        await new DelugeDownloadClient(new StubFactory(new DelugeHandler(sent)))
            .GrabAsync(Client("deluge"), Request() with { SaveFolder = LibraryFolder }, CancellationToken.None);

        using var add = JsonDocument.Parse(sent.First(body => body.Contains("core.add_torrent_magnet", StringComparison.Ordinal)));
        var options = add.RootElement.GetProperty("params")[1];
        Assert.Equal(LibraryFolder, options.GetProperty("download_location").GetString());
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
        Assert.Equal("abcd1234abcd1234abcd1234abcd1234abcd1234", Assert.Single(parameters[0].EnumerateArray()).GetString());
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
        Assert.Contains("Deluno sends", folder.Message);
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
            [ProcMapping("m1", @"C:DownloadsCompleted", "/downloads", true, 1), ProcMapping("m2", @"E:WeirReady", "/ready", false, 2)]);
        var service = new FolderChainService(Libraries([library]), Connections("deluge", []), Registry(FakeClient(true, null).Object), processors);

        var listed = Assert.Single((await service.ListDestinationsAsync(null, CancellationToken.None))!);

        Assert.Equal("proc-1", listed.ProcessorConnection!.Id);
        Assert.Equal("Weir", listed.ProcessorConnection.Name);
        var mapping = Assert.Single(listed.ProcessorConnection.PathMappings);
        Assert.Equal(@"C:DownloadsCompleted", mapping.DelunoPath);
        Assert.Equal("/downloads", mapping.ProcessorPath);
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

    private static IProcessorRepository Processors(ProcessorConnectionItem connection, IReadOnlyList<ProcessorPathMappingItem> mappings)
    {
        var repository = new Mock<IProcessorRepository>();
        repository.Setup(instance => instance.FindProcessorConnectionByNameAsync(connection.Name, It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        repository.Setup(instance => instance.ListProcessorPathMappingsAsync(connection.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mappings);
        return repository.Object;
    }

    private static ProcessorPathMappingItem ProcMapping(string id, string deluno, string processor, bool enabled, int priority)
        => new(id, "proc-1", deluno, processor, enabled, priority, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------ fixtures

    private static DownloadClientGrabRequest Request()
        => new("Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem Client(string protocol)
        => new(
            "client-1", protocol == "deluge" ? "Deluge" : "Transmission", protocol, "localhost", 8112, null, null, "http://localhost:8112/", false, null,
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
                    [new LibraryDownloadClientLinkItem($"link-{library.Id}", library.Id, "client-1", "Deluge", 1, now, now, "movies")]));
        }

        return repository.Object;
    }

    private static IConnectionsRepository Connections(string protocol, IReadOnlyList<DownloadClientPathMappingItem> mappings)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Client(protocol)]);
        repository.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(mappings);
        return repository.Object;
    }

    private static Mock<IDownloadClient> FakeClient(bool perGrab, string? reportedFolder)
    {
        var client = new Mock<IDownloadClient>();
        client.SetupGet(instance => instance.SetsSaveFolderPerGrab).Returns(perGrab);
        client.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Big.Buck.Bunny", true, "sent", "Sent.") { ExternalId = "abc" });
        client.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Deluge", "movies", DownloadClientCategoryStatuses.Ready, "Ready.", true, true, reportedFolder));
        return client;
    }

    private static IDownloadClientRegistry Registry(IDownloadClient client)
    {
        var registry = new Mock<IDownloadClientRegistry>();
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out client)).Returns(true);
        return registry.Object;
    }

    private static async Task<(DownloadClientGrabRequest? Request, Mock<IDownloadClient> Client)> GrabThroughService(
        IReadOnlyList<LibraryItem> libraries, bool perGrabClient, IReadOnlyList<DownloadClientPathMappingItem> mappings)
    {
        var fake = FakeClient(perGrabClient, reportedFolder: null);
        DownloadClientGrabRequest? seen = null;
        fake.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) => seen = request)
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Big.Buck.Bunny", true, "sent", "Sent.") { ExternalId = "abc" });

        var service = new DownloadClientGrabService(
            Connections("deluge", mappings),
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
            "Big.Buck.Bunny", "magnet:?xt=urn:btih:abc", "movies", "movies", null), CancellationToken.None);
        return (seen, fake);
    }

    private static FolderChainService ChainService(IReadOnlyList<LibraryItem> libraries, bool perGrabClient, string? clientReportsFolder)
        => new(Libraries(libraries), Connections("deluge", []), Registry(FakeClient(perGrabClient, clientReportsFolder).Object));

    private static Task<FolderChainReport?> Chain(IReadOnlyList<LibraryItem> libraries, bool perGrabClient, string? clientReportsFolder)
        => ChainService(libraries, perGrabClient, clientReportsFolder).CheckAsync(libraries[0].Id, CancellationToken.None);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TransmissionHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            var reply = body.Contains("\"torrent-add\"", StringComparison.Ordinal)
                ? """{"result":"success","arguments":{"torrent-added":{"id":7,"hashString":"abc"}}}"""
                : body.Contains("\"session-get\"", StringComparison.Ordinal)
                    ? """{"result":"success","arguments":{"download-dir":"C:\\Downloads"}}"""
                    : """{"result":"success","arguments":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>A Deluge whose Label plugin is on and already holds a "movies" label.</summary>
    private sealed class DelugeHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            var answer = body.Contains("core.add_torrent_", StringComparison.Ordinal)
                ? """{"result":"abcd1234abcd1234abcd1234abcd1234abcd1234","error":null,"id":2}"""
                : body.Contains("label.get_labels", StringComparison.Ordinal)
                    ? """{"result":["movies"],"error":null,"id":5}"""
                    : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
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
