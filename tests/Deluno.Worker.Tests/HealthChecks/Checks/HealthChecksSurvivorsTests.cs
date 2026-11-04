using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications.Contracts;
using Deluno.Notifications.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>Words and rules of health checks that a first round of tests let slip: each of these fails when the matching line is changed.</summary>
public sealed class HealthChecksSurvivorsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    // ---- Cloudflare helper

    [Fact]
    public async Task A_helper_that_does_not_answer_says_why_indexers_behind_a_check_are_stuck()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Deluno.Persistence.Tests.Support.FakePlatformSettingsRepository.Default() with { CloudflareHelperUrl = "http://127.0.0.1:8191" });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new Refusing()));

        var outcome = Assert.Single(await new CloudflareHelperHealthCheck(settings.Object, factory.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Indexers behind a Cloudflare check cannot be searched until it answers.", outcome.Cause);
        Assert.Equal("Start the helper (FlareSolverr or Byparr), or fix its address in Preferences, General.", outcome.Fix);
        Assert.Equal("Cloudflare helper", outcome.Name);
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    // ---- Proxy reachability

    private static ProxyItem Proxy(string type, string address)
        => new("p1", "Test proxy", type, address, null, null, true, Now, Now);

    private static HttpProxyReachabilityTester Tester()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HealthCheckHttpClients.Proxy)).Returns(new HttpClient());
        return new HttpProxyReachabilityTester(factory.Object);
    }

    [Fact]
    public async Task An_address_that_is_not_a_host_says_what_was_typed_and_what_was_expected()
    {
        var result = await Tester().TestAsync(Proxy(ProxyTypes.Http, "://"), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal(":// is not a usable proxy address (expected host:port).", result.Message);
    }

    [Fact]
    public async Task A_person_who_stops_the_test_stops_it_and_does_not_read_a_timeout()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tester().TestAsync(Proxy(ProxyTypes.Socks5, "socks5://127.0.0.1:1080"), stop.Token));
    }

    [Theory]
    [InlineData(ProxyTypes.Http, "127.0.0.1:80", "127.0.0.1:80")]
    [InlineData(ProxyTypes.Socks5, "127.0.0.1:80", "127.0.0.1:80")]
    [InlineData(ProxyTypes.Http, "127.0.0.1", "127.0.0.1:8080")]
    [InlineData(ProxyTypes.Socks5, "127.0.0.1", "127.0.0.1:1080")]
    [InlineData(ProxyTypes.Http, "http://127.0.0.1", "127.0.0.1:8080")]
    public async Task A_port_typed_into_a_bare_address_is_the_port_tried_and_a_missing_one_is_the_usual_one(string type, string address, string tried)
    {
        var result = await Tester().TestAsync(Proxy(type, address), CancellationToken.None);

        Assert.Contains(tried, result.Message, StringComparison.Ordinal);
    }

    // ---- Folder chain

    [Fact]
    public async Task A_library_whose_every_link_matches_says_so_in_one_sentence()
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "lib0", Name = "Films" };
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var chain = new Mock<IFolderChainService>();
        chain.Setup(s => s.CheckAsync("lib0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderChainReport("lib0", "Films", "movies", false, null, null, null, "/media/films", [], []));

        var outcome = Assert.Single(await new LibraryFolderChainHealthCheck(libraries.Object, chain.Object).RunAsync(CancellationToken.None));

        Assert.Equal("Every routed client's category and save path match this library's folders.", outcome.Message);
    }

    // ---- Processors

    private static ProcessorConnectionItem Connection()
        => new("connection-1", "Weir", "generic-webhook", "https://weir.example.test/webhook", "Authorization", null, true, "healthy", null, Now, Now, Now);

    private static LibraryItem Library(string workflow)
        => (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with
        {
            Id = "library-1", Name = "Films", ImportWorkflow = workflow, ProcessorName = "Weir", DownloadsPath = "/data/films"
        };

    [Fact]
    public async Task With_no_processor_switched_on_nothing_else_is_asked_for()
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(p => p.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Connection() with { IsEnabled = false }]);

        var outcomes = await new ProcessorHealthCheck(processors.Object, new Mock<IProcessorConnectionService>(MockBehavior.Strict).Object, new Mock<ILibrariesRepository>(MockBehavior.Strict).Object)
            .RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_processor_that_serves_no_library_is_not_asked_what_it_watches()
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(p => p.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Connection()]);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("import-directly")]);

        var outcomes = await new ProcessorHealthCheck(processors.Object, new Mock<IProcessorConnectionService>(MockBehavior.Strict).Object, libraries.Object)
            .RunAsync(CancellationToken.None);

        Assert.Single(outcomes);
    }

    [Fact]
    public async Task The_folder_list_is_asked_for_only_when_the_processor_has_one_and_its_entry_wins_over_the_single_folder()
    {
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(p => p.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Connection()]);
        processors.Setup(p => p.ListProcessorPathMappingsAsync("connection-1", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library("refine-before-import")]);
        var service = new Mock<IProcessorConnectionService>();
        service.Setup(s => s.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "/data/single" });
        var check = new ProcessorHealthCheck(processors.Object, service.Object, libraries.Object);

        var single = await check.RunAsync(CancellationToken.None);

        service.Verify(s => s.GetLibraryFoldersAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("/data/single", single.Single(o => o.Id.Contains(":watching:")).Message);

        service.Setup(s => s.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null) { WatchedPath = "/data/single", HasLibraryFolders = true });
        service.Setup(s => s.GetLibraryFoldersAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorLibraryFolder("library-1", "/data/films", true)]);

        var perLibrary = await check.RunAsync(CancellationToken.None);

        Assert.Equal(HealthCheckStatuses.Ok, perLibrary.Single(o => o.Id.Contains(":watching:")).Status);
    }

    // ---- Tag routing

    private static IndexerItem Indexer(string id, string tags = "") => new(
        id, "Indexer " + id, "torznab", "public", "http://localhost", null, 1, "2000", tags, "both", true,
        "healthy", null, null, null, null, 0, null, null, Now, Now);

    private static NotificationWebhookItem Webhook(string id, string tags = "") => new(
        id, "Hook " + id, "http://localhost/hook", "", true, null, null, Now, Now, Tags: tags);

    private static TagRoutingHealthCheck TagCheck(IndexerItem[] indexers, NotificationWebhookItem[] webhooks, MediaTitleTags[] movies, MediaTitleTags[] shows, out Mock<IMediaTagStore> tags)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(r => r.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(indexers);
        connections.Setup(r => r.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var notifications = new Mock<INotificationRepository>();
        notifications.Setup(r => r.ListNotificationWebhooksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(webhooks);
        tags = new Mock<IMediaTagStore>();
        tags.Setup(s => s.ListAllTitleTagsAsync(MediaKind.Movie, It.IsAny<CancellationToken>())).ReturnsAsync(movies);
        tags.Setup(s => s.ListAllTitleTagsAsync(MediaKind.Series, It.IsAny<CancellationToken>())).ReturnsAsync(shows);
        return new TagRoutingHealthCheck(connections.Object, notifications.Object, tags.Object);
    }

    private static MediaTitleTags Title(string name, params string[] tags) => new(name, name, true, tags);

    [Fact]
    public async Task One_tagged_connection_among_untagged_ones_is_enough_to_look_and_a_setup_with_none_reads_nothing()
    {
        var onlyHook = TagCheck([Indexer("i1"), Indexer("i2")], [Webhook("w1"), Webhook("w2", "kidz")], [Title("Sintel", "adults")], [], out _);
        var onlyIndexer = TagCheck([Indexer("i1"), Indexer("i2", "kidz")], [Webhook("w1"), Webhook("w2")], [Title("Sintel", "adults")], [], out _);
        var none = TagCheck([Indexer("i1")], [Webhook("w1")], [Title("Sintel", "adults")], [], out var store);

        Assert.Contains(await onlyHook.RunAsync(CancellationToken.None), o => o.Id == "tags:unused:notification:w2");
        Assert.Contains(await onlyIndexer.RunAsync(CancellationToken.None), o => o.Id == "tags:unused:indexer:i2");
        Assert.Empty(await none.RunAsync(CancellationToken.None));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_tag_only_a_show_carries_counts_as_held_and_a_tag_held_by_one_of_two_is_still_held()
    {
        var onlyShows = TagCheck([Indexer("i1", "kidz")], [], [Title("Sintel", "adults")], [Title("Bluey", "kidz")], out _);
        var oneOfTwo = TagCheck([Indexer("i1", "kidz, family")], [], [Title("Sintel", "family")], [], out _);

        Assert.DoesNotContain(await onlyShows.RunAsync(CancellationToken.None), o => o.Id.StartsWith("tags:unused:", StringComparison.Ordinal));
        Assert.DoesNotContain(await oneOfTwo.RunAsync(CancellationToken.None), o => o.Id.StartsWith("tags:unused:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unused_indexer_says_what_to_do_with_its_own_name()
    {
        var check = TagCheck([Indexer("i1", "kidz")], [], [Title("Sintel", "adults")], [], out _);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None), o => o.Id == "tags:unused:indexer:i1");

        Assert.Equal("Tag a title with it, or remove the tag from Indexer i1.", outcome.Fix);
        Assert.Contains("\"link\":\"/indexers/indexers\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_title_is_stranded_only_when_no_indexer_at_all_can_serve_it()
    {
        var check = TagCheck([Indexer("i1", "kidz"), Indexer("i2", "adults")], [], [Title("Sintel", "adults")], [], out _);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, o => o.Id.StartsWith("tags:no-indexer", StringComparison.Ordinal));
    }

}
