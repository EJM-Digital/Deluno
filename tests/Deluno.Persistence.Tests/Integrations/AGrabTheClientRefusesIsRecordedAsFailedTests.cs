using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// A grab the download client refuses is recorded as a failed dispatch with the client's own words and is not an exception.
/// The file Deluno sends is not a torrent, and each client answers as the real one did when it was sent just that: qBittorrent,
/// Transmission and Deluge, from their recordings, through the real grab service and a real SQLite dispatch store.
/// </summary>
public sealed class AGrabTheClientRefusesIsRecordedAsFailedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T08:00:00Z");

    [Theory]
    [InlineData("deluge", "Deluge refused Deluno's password")]
    [InlineData("qbittorrent", "qBittorrent refused Deluno's username and password", Skip = "DEFECT: a grab to qBittorrent whose login is refused stores the raw 'qBittorrent answered the sign-in with 401' line, not the plain refusal the connection test and the actions give.")]
    [InlineData("transmission", "Transmission refused Deluno's username and password", Skip = "DEFECT: a grab to Transmission whose login is refused stores 'Response status code does not indicate success: 401', not the plain refusal the connection test and the actions give.")]
    [InlineData("sabnzbd", "SABnzbd refused Deluno's API key", Skip = "DEFECT: a grab to SABnzbd whose API key is refused stores 'Response status code does not indicate success: 403', not the plain refusal the connection test and the actions give.")]
    [InlineData("nzbget", "NZBGet refused Deluno's username and password", Skip = "DEFECT: a grab to NZBGet whose login is refused stores 'Response status code does not indicate success: 401', not the plain refusal the connection test and the actions give.")]
    public async Task A_client_that_refuses_Delunos_login_is_stored_on_the_dispatch_in_plain_words_and_the_grab_answers_failed(string protocol, string clientsWords)
    {
        var (result, stored) = await GrabAsync(protocol, RealClientAnswering(protocol, refusesLogin: true), withTorrentFile: false);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Contains(clientsWords, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("failed", stored.GrabStatus);
        Assert.Contains(clientsWords, stored.GrabMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(stored.TorrentHashOrItemId);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_client_that_refuses_the_torrent_file_itself_is_stored_as_a_dead_release_in_plain_words(string protocol)
    {
        var (result, stored) = await GrabAsync(protocol, RealClientAnswering(protocol, refusesLogin: false), withTorrentFile: true);

        Assert.False(result.Succeeded);
        Assert.Equal("deadRelease", result.Status);
        Assert.Contains("torrent file could not be", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("deadRelease", stored.GrabStatus);
        Assert.Contains("torrent file could not be", stored.GrabMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(DownloadClientGrabResult Result, DownloadDispatchItem Stored)> GrabAsync(string protocol, IDownloadClient implementation, bool withTorrentFile)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            protocol, protocol, "localhost", 8112, "user", "secret", null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
        var library = await new SqliteLibrariesRepository(storage.Factory, time).CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "planned", null,
            cancellationToken: CancellationToken.None);
        var service = new DownloadClientGrabService(
            connections,
            new DownloadClientRegistry([implementation]),
            new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance),
            dispatches,
            new NullRealtimeEventPublisher());

        var request = new DownloadClientGrabRequest("Arrival.2016.1080p.WEB", "https://indexer.invalid/get", "movies", "movies", "Fixture", DispatchId: dispatchId);
        if (withTorrentFile)
        {
            request = request with { TorrentFile = Encoding.UTF8.GetBytes("this is not a torrent") };
        }

        var result = await service.GrabAsync(client.Id, request, CancellationToken.None);
        var stored = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotNull(stored);
        return (result, stored!);
    }

    private static IDownloadClient RealClientAnswering(string protocol, bool refusesLogin)
    {
        // A wrong login is answered alike by every call; a refused file is answered by the add after a good sign-in.
        HttpMessageHandler Handler(string app, params Recording[] recordings) => RecordedReplies.Handler(app, recordings);
        HttpMessageHandler Refusing(string app, RecordedReply reply) => RecordedReplies.Handler(app, [], (_, _) => reply.ToResponse());
        switch (protocol)
        {
            case "qbittorrent":
                return new QbittorrentDownloadClient(() => refusesLogin
                    ? Refusing("qbittorrent", RecordedReplies.Qbittorrent("login-refused").Single.Reply)
                    : Handler("qbittorrent", RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("add-refused")));
            case "transmission":
                return new TransmissionDownloadClient(new Factory(refusesLogin
                    ? Refusing("transmission", RecordedReplies.Transmission("login-refused").Exchanges[0].Reply)
                    : Handler("transmission", RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("add-refused"))));
            case "deluge":
                return new DelugeDownloadClient(new Factory(refusesLogin
                    ? Refusing("deluge", RecordedReplies.Deluge("login-refused").Single.Reply)
                    : Handler("deluge", RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("add-refused"))));
            case "sabnzbd":
                return new SabnzbdDownloadClient(new Factory(Refusing("sabnzbd", RecordedReplies.Sabnzbd("api-key-refused").Find("mode=queue&output=json").Reply)));
            case "nzbget":
                return new NzbGetDownloadClient(new Factory(Refusing("nzbget", RecordedReplies.Nzbget("login-refused").Exchanges[0].Reply)));
            default:
                throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
