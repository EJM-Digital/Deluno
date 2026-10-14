using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1312: a wrong password, key or login must never escape a client method as an exception where the interface promises a
/// value (null, false, empty, or a failed result). Every <see cref="IDownloadClient"/> method is replayed against each
/// client's own recorded refused-login reply: qBittorrent 5.2.1 (401), Transmission 4.1.3 (401 with WWW-Authenticate), Deluge
/// 2.2.0 (auth.login false), NZBGet 26.3 (401) and SABnzbd 5.1.3 (403 "API Key Incorrect"). The refusal itself is said once, by
/// the snapshot, which is the call that sets the client's health (<see cref="ARefusedKeyOrLoginIsSaidPlainlyTests"/>).
/// </summary>
public sealed class EveryClientMethodKeepsItsContractOnARefusedLoginTests
{
    private const string Id = "b7b95339b09b0e7249d2b51ecc500b1650a18592";

    public static IEnumerable<object[]> Clients() => [["qbittorrent"], ["transmission"], ["deluge"], ["nzbget"], ["sabnzbd"]];

    private static HttpMessageHandler Refusing(string protocol)
    {
        var reply = protocol switch
        {
            "qbittorrent" => RecordedReplies.Qbittorrent("login-refused").Single.Reply,
            "transmission" => RecordedReplies.Transmission("login-refused").Find("session-get").Reply,
            "deluge" => RecordedReplies.Deluge("login-refused").Single.Reply,
            "nzbget" => RecordedReplies.Nzbget("login-refused").Find("listgroups").Reply,
            "sabnzbd" => RecordedReplies.Sabnzbd("api-key-refused").Find("mode=queue&output=json").Reply,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        return RecordedReplies.Handler("none", [], (_, _) => reply.ToResponse());
    }

    private static (IDownloadClient Client, DownloadClientItem Item) Build(string protocol)
    {
        var handler = Refusing(protocol);
        var factory = new Factory(handler);
        IDownloadClient client = protocol switch
        {
            "qbittorrent" => new QbittorrentDownloadClient(() => handler),
            "transmission" => new TransmissionDownloadClient(factory),
            "deluge" => new DelugeDownloadClient(factory),
            "nzbget" => new NzbGetDownloadClient(factory, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)),
            _ => new SabnzbdDownloadClient(factory, TimeSpan.Zero)
        };
        var username = protocol is "qbittorrent" or "transmission" or "nzbget" ? "login" : null;
        return (client, Client(protocol, username, "wrong-secret"));
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task GetFiles_gives_null(string protocol)
    {
        var (client, item) = Build(protocol);
        Assert.Null(await client.GetFilesAsync(item, Id, CancellationToken.None));
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task GetHistory_gives_an_empty_list_or_no_throw(string protocol)
    {
        var (client, item) = Build(protocol);
        var history = await Record.ExceptionAsync(() => client.GetHistoryAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Null(history);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task Grab_gives_a_failed_result(string protocol)
    {
        var (client, item) = Build(protocol);
        var request = new DownloadClientGrabRequest("Some.Release.2020", protocol is "nzbget" or "sabnzbd" ? "http://localhost/x.nzb" : "magnet:?xt=urn:btih:" + Id, "movies", "movies", "Indexer");
        var result = await client.GrabAsync(item, request, CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task Every_action_gives_a_failed_result(string protocol)
    {
        var (client, item) = Build(protocol);
        foreach (var action in new[] { DownloadClientActions.Pause, DownloadClientActions.Resume, DownloadClientActions.Delete, DownloadClientActions.DeleteWithData })
        {
            var result = await client.ExecuteActionAsync(item, action, Id, CancellationToken.None);
            Assert.False(result.Succeeded, action);
        }
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task CheckCategory_does_not_throw_and_is_not_ready(string protocol)
    {
        var (client, item) = Build(protocol);
        var check = await client.CheckCategoryAsync(item, "movies", CancellationToken.None);
        Assert.NotEqual(DownloadClientCategoryStatuses.Ready, check.Status);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task SetCategory_gives_a_failed_result(string protocol)
    {
        var (client, item) = Build(protocol);
        var result = await client.SetCategoryAsync(item, Id, "keep", "movies", CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task CheckKeepSeedingCategory_does_not_throw_and_is_not_ready(string protocol)
    {
        var (client, item) = Build(protocol);
        var check = await client.CheckKeepSeedingCategoryAsync(item, "keep", CancellationToken.None);
        Assert.NotEqual(DownloadClientCategoryStatuses.Ready, check.Status);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task ClearWarnings_gives_false(string protocol)
    {
        var (client, item) = Build(protocol);
        Assert.False(await client.ClearWarningsAsync(item, CancellationToken.None));
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task ResumeClient_does_not_succeed_or_throw(string protocol)
    {
        var (client, item) = Build(protocol);
        Assert.False((await client.ResumeClientAsync(item, CancellationToken.None)).Succeeded);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task The_usenet_provider_is_unknown_not_a_throw(string protocol)
    {
        var (client, item) = Build(protocol);
        Assert.Null(await client.HasUsenetProviderAsync(item, CancellationToken.None));
        var written = await client.SetUsenetProviderAsync(item, new UsenetProviderRequest("news.example.com", 563, true, "u", "p", 8), CancellationToken.None);
        Assert.True(written is null || !written.Saved);
    }

    [Theory, MemberData(nameof(Clients))]
    public async Task The_snapshot_either_says_the_refusal_plainly_or_is_null(string protocol)
    {
        var (client, item) = Build(protocol);
        var thrown = await Record.ExceptionAsync(() => client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        // The snapshot is the one call allowed to throw: the telemetry service turns it into one "refused" health line.
        Assert.True(thrown is null || ClientCredentials.MessageFor(item, thrown) is not null, thrown?.ToString());
    }
}
