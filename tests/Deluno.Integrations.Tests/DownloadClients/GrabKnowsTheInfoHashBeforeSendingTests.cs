using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062: Deluno knows every torrent grab's id at send time, without asking
/// the client for names: a magnet names its info-hash, and a .torrent link is
/// fetched by Deluno itself and hashed, then sent to the client as the file.
/// </summary>
public sealed class GrabKnowsTheInfoHashBeforeSendingTests
{
    private const string Hash = "de4158016fbc49ebf35641903cec65a0ad0d30b5";

    private static readonly byte[] Pieces = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();

    // The same v1 torrent TorrentInfoHashTests hashes: its SHA-1 is Hash.
    private static byte[] TorrentFile() => Encoding.Latin1.GetBytes("d8:announce21:http://t.example/ann.4:infod6:lengthi5e4:name1:a12:piece lengthi16384e6:pieces20:")
        .Concat(Pieces).Concat(Encoding.Latin1.GetBytes("ee")).ToArray();

    [Fact]
    public async Task A_magnet_grab_is_known_by_the_hash_in_the_link_before_the_client_is_asked_anything()
    {
        var harness = new Harness(externalId: null);

        var result = await harness.GrabAsync(Request($"magnet:?xt=urn:btih:{Hash.ToUpperInvariant()}&dn=x"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, harness.SentToClient!.KnownInfoHash);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task A_base32_magnet_is_known_by_its_hex_hash()
    {
        var harness = new Harness(externalId: null);

        var result = await harness.GrabAsync(Request("magnet:?xt=urn:btih:32Z3M4OVZIWXKQTNCJRR5F33MPPS7YZM"));

        Assert.Equal(40, harness.SentToClient!.KnownInfoHash!.Length);
        Assert.Equal(harness.SentToClient.KnownInfoHash, result.ExternalId);
    }

    [Fact]
    public async Task A_torrent_link_is_fetched_by_Deluno_hashed_and_sent_as_the_file()
    {
        var file = TorrentFile();
        var fetcher = new Mock<IIndexerTorrentFetcher>();
        fetcher.Setup(instance => instance.FetchAsync(It.IsAny<IndexerItem?>(), "https://indexer.example/dl/1.torrent?apikey=k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexerTorrentFetch(null, file));
        var harness = new Harness(externalId: null, fetcher: fetcher.Object);

        var result = await harness.GrabAsync(Request("https://indexer.example/dl/1.torrent?apikey=k"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(file, harness.SentToClient!.TorrentFile);
        Assert.Equal(Hash, harness.SentToClient.KnownInfoHash);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task A_link_that_redirects_to_a_magnet_is_sent_as_that_magnet()
    {
        var magnet = $"magnet:?xt=urn:btih:{Hash}&dn=x";
        var fetcher = new Mock<IIndexerTorrentFetcher>();
        fetcher.Setup(instance => instance.FetchAsync(It.IsAny<IndexerItem?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexerTorrentFetch(magnet, null));
        var harness = new Harness(externalId: null, fetcher: fetcher.Object);

        var result = await harness.GrabAsync(Request("https://indexer.example/dl/1"));

        Assert.Equal(magnet, harness.SentToClient!.DownloadUrl);
        Assert.Null(harness.SentToClient.TorrentFile);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task The_indexers_own_infohash_is_used_when_it_agrees_and_the_files_own_hash_wins_when_it_does_not()
    {
        var fetcher = new Mock<IIndexerTorrentFetcher>();
        fetcher.Setup(instance => instance.FetchAsync(It.IsAny<IndexerItem?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexerTorrentFetch(null, TorrentFile()));

        var agreeing = new Harness(externalId: null, fetcher: fetcher.Object);
        await agreeing.GrabAsync(Request("https://indexer.example/dl/1") with { ReleaseHash = Hash.ToUpperInvariant() });
        Assert.Equal(Hash, agreeing.SentToClient!.KnownInfoHash);

        var disagreeing = new Harness(externalId: null, fetcher: fetcher.Object);
        await disagreeing.GrabAsync(Request("https://indexer.example/dl/1") with { ReleaseHash = new string('a', 40) });
        Assert.Equal(Hash, disagreeing.SentToClient!.KnownInfoHash);
    }

    [Fact]
    public async Task With_nothing_else_to_go_on_the_indexers_infohash_is_the_id_and_a_usenet_guid_is_not()
    {
        var torrent = new Harness(externalId: null);
        var result = await torrent.GrabAsync(Request("https://indexer.example/dl/1") with { ReleaseHash = Hash });
        Assert.Equal(Hash, result.ExternalId);

        var notAHash = new Harness(externalId: null);
        var second = await notAHash.GrabAsync(Request("https://indexer.example/dl/1") with { ReleaseHash = "3f2a9c1e-77aa-4b1c-9d3e-000000000001" });
        Assert.Null(second.ExternalId);
        Assert.Null(notAHash.SentToClient!.KnownInfoHash);
    }

    [Fact]
    public async Task An_indexer_that_would_not_answer_fails_the_grab_without_blaming_the_release_and_sends_nothing()
    {
        var fetcher = new Mock<IIndexerTorrentFetcher>();
        fetcher.Setup(instance => instance.FetchAsync(It.IsAny<IndexerItem?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexerTorrentFetch(null, null, "Example did not answer."));
        var harness = new Harness(externalId: null, fetcher: fetcher.Object);

        var result = await harness.GrabAsync(Request("https://indexer.example/dl/1"));

        Assert.False(result.Succeeded);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Null(harness.SentToClient);
        Assert.Empty(harness.Reserved);
    }

    [Fact]
    public async Task A_release_the_indexer_no_longer_has_is_a_dead_release()
    {
        var fetcher = new Mock<IIndexerTorrentFetcher>();
        fetcher.Setup(instance => instance.FetchAsync(It.IsAny<IndexerItem?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexerTorrentFetch(null, null, "Example no longer has this release.", ReleaseIsGone: true));
        var harness = new Harness(externalId: null, fetcher: fetcher.Object);

        var result = await harness.GrabAsync(Request("https://indexer.example/dl/1"));

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Null(harness.SentToClient);
    }

    [Fact]
    public async Task A_torrent_file_from_a_tracker_that_is_not_a_torrent_is_a_dead_release()
    {
        var harness = new Harness(externalId: null);

        var result = await harness.GrabAsync(Request("https://tracker.example/dl/1") with { TorrentFile = Encoding.ASCII.GetBytes("<html>login</html>") });

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Null(harness.SentToClient);
    }

    [Fact]
    public async Task A_usenet_grab_is_not_fetched_or_hashed()
    {
        var fetcher = new Mock<IIndexerTorrentFetcher>(MockBehavior.Strict);
        var harness = new Harness(externalId: "SABnzbd_nzo_1", protocol: "sabnzbd", fetcher: fetcher.Object);

        var result = await harness.GrabAsync(Request("https://indexer.example/getnzb/1.nzb"));

        Assert.True(result.Succeeded);
        Assert.Null(harness.SentToClient!.KnownInfoHash);
        Assert.Empty(harness.Reserved);
    }

    [Fact]
    public async Task The_grab_is_reserved_with_its_hash_before_it_is_sent_and_kept_after_a_good_send()
    {
        var harness = new Harness(externalId: null);

        await harness.GrabAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.Equal([$"reserve:{Hash}", "send"], harness.Calls);
        Assert.Empty(harness.Released);
    }

    [Fact]
    public async Task A_failed_send_removes_its_reservation()
    {
        var harness = new Harness(externalId: null, sendSucceeds: false);

        var result = await harness.GrabAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.False(result.Succeeded);
        Assert.Equal(harness.Reserved, harness.Released);
        Assert.Single(harness.Released);
    }

    [Fact]
    public async Task A_second_grab_of_a_release_Deluno_is_already_sending_follows_it_instead_of_blocklisting_it()
    {
        // A film in two libraries, or two search jobs at once: the client says
        // it already has the release, and the other grab's reservation is
        // Deluno's own record of it.
        var harness = new Harness(externalId: Hash, duplicate: true, otherReservationExists: true);

        var result = await harness.GrabAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.True(result.Succeeded, result.Message);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.True(result.IsDuplicateFollow);
    }

    [Fact]
    public async Task A_release_the_client_has_from_elsewhere_with_no_record_of_Delunos_fails_with_the_plain_reason()
    {
        var harness = new Harness(externalId: Hash, duplicate: true, otherReservationExists: false);

        var result = await harness.GrabAsync(Request($"magnet:?xt=urn:btih:{Hash}"));

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("already has this one from elsewhere", result.Message, StringComparison.Ordinal);
        Assert.Single(harness.Released);
    }

    private static DownloadClientGrabRequest Request(string url)
        => new("Example.Movie.2026.1080p", url, "movies", "movies", "Example Indexer");

    private sealed class Harness
    {
        private readonly DownloadClientGrabService service;
        private readonly Mock<IDownloadDispatchesRepository> dispatches = new();

        public Harness(
            string? externalId,
            string protocol = "qbittorrent",
            IIndexerTorrentFetcher? fetcher = null,
            bool sendSucceeds = true,
            bool duplicate = false,
            bool otherReservationExists = false)
        {
            var now = DateTimeOffset.UnixEpoch;
            var client = new DownloadClientItem(
                "client-1", "Test client", protocol, "localhost", 8080, null, null, null, false, null,
                "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);

            var connections = new Mock<IConnectionsRepository>();
            connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
            connections.Setup(instance => instance.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var implementation = new Mock<IDownloadClient>();
            implementation.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) =>
                {
                    SentToClient = request;
                    Calls.Add("send");
                    return sendSucceeds
                        ? new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.") { ExternalId = externalId, IsDuplicateFollow = duplicate }
                        : new DownloadClientGrabResult(client.Id, request.ReleaseName, false, "failed", "Refused.");
                });
            var registry = new Mock<IDownloadClientRegistry>();
            var captured = implementation.Object;
            registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

            dispatches.Setup(instance => instance.ReserveGrabAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string itemId, string _, CancellationToken _) =>
                {
                    var id = "reservation-" + (Reserved.Count + 1);
                    Reserved.Add(id);
                    Calls.Add($"reserve:{itemId}");
                    return id;
                });
            dispatches.Setup(instance => instance.ReleaseGrabReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) =>
                {
                    Released.Add(id);
                    return Task.CompletedTask;
                });
            dispatches.Setup(instance => instance.HasOtherGrabReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(otherReservationExists);
            dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            service = new DownloadClientGrabService(
                connections.Object,
                registry.Object,
                new PassThrough(),
                dispatches.Object,
                Mock.Of<IRealtimeEventPublisher>(),
                torrentFetcher: fetcher);
        }

        public DownloadClientGrabRequest? SentToClient { get; private set; }

        public List<string> Calls { get; } = [];

        public List<string> Reserved { get; } = [];

        public List<string> Released { get; } = [];

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientGrabRequest request)
            => service.GrabAsync("client-1", request, CancellationToken.None);
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
