using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1251, from recording SABnzbd 5.1.3's real replies (#1237): SABnzbd answers <c>{"status":true}</c> to a pause or a
/// resume of an id it has never heard of, so Deluno said "done" for an action that did nothing. It is the same shape as
/// #1212's removal, and the same rule applies to every client: a pause or a resume is done only when the client had the
/// job before the call and shows it in the asked state after it. The client is looked at, not believed.
///
/// <para>Every reply below is a recording of the real client: the list call is answered from the recording of a paused
/// job (SABnzbd 5.1.3, NZBGet 26.3, qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0), changed in the one value that
/// says whether it is paused (see <see cref="RecordedReply.BodyWith"/>), and from the recording of a job the client never
/// had. The pause call's and the resume call's own replies are the recorded ones. Deluno never reads either reply as the answer.</para>
/// </summary>
public sealed class EveryClientPausesAndResumesOnlyWhatItHasTests
{
    private static readonly string[] Apps = ["sabnzbd", "nzbget", "qbittorrent", "transmission", "deluge"];

    private static readonly string[] Actions = [DownloadClientActions.Pause, DownloadClientActions.Resume];

    public static IEnumerable<object[]> EveryClientAndAction()
        => Apps.SelectMany(app => Actions.Select(action => new object[] { app, action }));

    private const string SabId = "7690dfab-c8ad-4a5e-92cd-427a14ce4ea6";
    private const string NzbGetId = "26";
    private const string QbitHash = "8d878a21c3d4180ae08b4e044904667d9f30bec5";
    private const string TransmissionHash = "ff7447384425ba526a3efb9efb792f7ba67a77dd";
    private const string DelugeHash = "b7b95339b09b0e7249d2b51ecc500b1650a18592";

    private static string IdOf(string app) => app switch
    {
        "sabnzbd" => SabId,
        "nzbget" => NzbGetId,
        "qbittorrent" => QbitHash,
        "transmission" => TransmissionHash,
        _ => DelugeHash
    };

    /// <summary>A recorded client that holds one job, in one state, and obeys (or does not obey) a pause or resume call.</summary>
    private sealed class Rig
    {
        private readonly string _app;
        private readonly bool _obeys;
        private readonly bool _listingFails;

        public Rig(string app, bool has, bool paused, bool obeys = true, bool listingFails = false)
        {
            _app = app;
            Has = has;
            Paused = paused;
            _obeys = obeys;
            _listingFails = listingFails;
        }

        public bool Has { get; }

        public bool Paused { get; private set; }

        /// <summary>Pause and resume calls that reached the client.</summary>
        public int Changes { get; private set; }

        /// <summary>The last pause or resume call, path and body, to show it names the download and not the whole client.</summary>
        public string? LastChange { get; private set; }

        public HttpMessageHandler Handler => RecordedReplies.Handler(_app, Recordings(), Intercept);

        private Recording[] Recordings() => _app switch
        {
            "sabnzbd" => [RecordedReplies.Sabnzbd("paused"), RecordedReplies.Sabnzbd("unknown-id")],
            "nzbget" => [RecordedReplies.Nzbget("paused")],
            "qbittorrent" => [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("paused")],
            "transmission" => [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("paused")],
            _ => [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("paused")]
        };

        private HttpResponseMessage? Intercept(HttpRequestMessage request, string body)
        {
            var url = request.RequestUri!.PathAndQuery;
            var call = url + body;

            // A pause or a resume call. The reply is the recorded one of that call.
            var pause = call.Contains("name=pause", StringComparison.Ordinal) || call.Contains("GroupPause", StringComparison.Ordinal)
                || call.Contains("torrents/stop", StringComparison.Ordinal) || call.Contains("torrent-stop", StringComparison.Ordinal)
                || call.Contains("core.pause_torrent", StringComparison.Ordinal);
            var resume = call.Contains("name=resume", StringComparison.Ordinal) || call.Contains("GroupResume", StringComparison.Ordinal)
                || call.Contains("torrents/start", StringComparison.Ordinal) || call.Contains("torrent-start", StringComparison.Ordinal)
                || call.Contains("core.resume_torrent", StringComparison.Ordinal);
            if (pause || resume)
            {
                Changes++;
                LastChange = Uri.UnescapeDataString(call);
                if (_obeys)
                {
                    Paused = pause;
                }

                return PauseReply(pause);
            }

            var isList = _app switch
            {
                "sabnzbd" => url.Contains("mode=queue", StringComparison.Ordinal) && !url.Contains("name=", StringComparison.Ordinal),
                "nzbget" => body.Contains("\"listgroups\"", StringComparison.Ordinal),
                "qbittorrent" => url.Contains("torrents/info", StringComparison.Ordinal),
                "transmission" => body.Contains("torrent-get", StringComparison.Ordinal),
                _ => body.Contains("web.update_ui", StringComparison.Ordinal)
            };
            if (!isList)
            {
                return null;
            }

            return _listingFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : RecordedReplies.Json(ListBody());
        }

        private HttpResponseMessage PauseReply(bool pause)
            => (_app switch
            {
                "sabnzbd" => pause ? RecordedReplies.Sabnzbd("paused").Find("name=pause") : RecordedReplies.Sabnzbd("resumed").Find("name=resume"),
                "nzbget" => pause ? RecordedReplies.Nzbget("paused").Find("GroupPause") : RecordedReplies.Nzbget("resumed").Find("GroupResume"),
                "qbittorrent" => pause ? RecordedReplies.Qbittorrent("paused").Find("torrents/stop") : RecordedReplies.Qbittorrent("resumed").Find("torrents/start"),
                "transmission" => pause ? RecordedReplies.Transmission("paused").Find("torrent-stop") : RecordedReplies.Transmission("resumed").Find("torrent-start"),
                _ => pause ? RecordedReplies.Deluge("paused").Find("core.pause_torrent") : RecordedReplies.Deluge("resumed").Find("core.resume_torrent")
            }).Reply.ToResponse();

        /// <summary>The recorded list, the paused one changed in the value that says whether it is paused, or the one for a job the client never had.</summary>
        private string ListBody()
        {
            switch (_app)
            {
                case "sabnzbd":
                    var queue = RecordedReplies.Sabnzbd("paused").Find("mode=queue&output=json").Reply;
                    return Has
                        ? queue.BodyWith(("queue.slots.0.status", Paused ? "Paused" : "Downloading"))
                        : RecordedReplies.Sabnzbd("idle").Find("mode=queue&output=json").Reply.Body!;
                case "nzbget":
                    var groups = RecordedReplies.Nzbget("paused").Find("listgroups").Reply;
                    // NZBGet 26.3 still says DOWNLOADING right after a pause (recorded); what it shows is that all the remaining size is paused.
                    return Has
                        ? groups.BodyWith(("result.1.PausedSizeMB", Paused ? 1610 : 0), ("result.1.PausedSizeLo", Paused ? 1688311206L : 0L), ("result.1.RemainingSizeMB", Paused ? 1610 : 1611))
                        : RecordedReplies.Nzbget("idle").Find("listgroups").Reply.Body!;
                case "qbittorrent":
                    var info = RecordedReplies.Qbittorrent("paused").Find("torrents/info").Reply;
                    return Has
                        ? Paused ? info.Body! : info.BodyWith(("0.state", "downloading"))
                        : RecordedReplies.Qbittorrent("removed-twice").Find("torrents/info").Reply.Body!;
                case "transmission":
                    var get = RecordedReplies.Transmission("paused").Find("torrent-get").Reply;
                    return Has
                        ? Paused ? get.Body! : get.BodyWith(("arguments.torrents.0.status", 4))
                        : RecordedReplies.Transmission("removed-twice").Find("torrent-get").Reply.Body!;
                default:
                    var update = RecordedReplies.Deluge("paused").Find("web.update_ui").Reply;
                    return Has
                        ? Paused ? update.Body! : update.BodyWith(("result.torrents.*.state", "Downloading"))
                        : RecordedReplies.Deluge("removed-twice").Find("web.update_ui").Reply.Body!;
            }
        }
    }

    private static Task<DownloadClientActionResult> Run(string app, Rig rig, string action)
    {
        var item = Client(app);
        var id = IdOf(app);
        return app switch
        {
            "sabnzbd" => new SabnzbdDownloadClient(new Factory(rig.Handler)).ExecuteActionAsync(item, action, id, CancellationToken.None),
            "nzbget" => new NzbGetDownloadClient(new Factory(rig.Handler), removalSettle: TimeSpan.Zero).ExecuteActionAsync(item, action, id, CancellationToken.None),
            "qbittorrent" => new QbittorrentDownloadClient(() => rig.Handler).ExecuteActionAsync(item, action, id, CancellationToken.None),
            "transmission" => new TransmissionDownloadClient(new Factory(rig.Handler)).ExecuteActionAsync(item, action, id, CancellationToken.None),
            _ => new DelugeDownloadClient(new Factory(rig.Handler)).ExecuteActionAsync(item, action, id, CancellationToken.None)
        };
    }

    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task A_job_the_client_has_is_paused_or_resumed_once_the_client_shows_it_so(string app, string action)
    {
        var pausing = action == DownloadClientActions.Pause;
        var rig = new Rig(app, has: true, paused: !pausing);

        var result = await Run(app, rig, action);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, rig.Changes);
        Assert.Equal(pausing, rig.Paused);
    }

    /// <summary>The issue's first line: an id the client does not have is a plain failure, and nothing is sent for it.</summary>
    /// <summary>And pausing one download is not pausing the client: the call names the job (this replaces the old per-client check that ran on made-up replies).</summary>
    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task The_call_names_the_download_it_changes(string app, string action)
    {
        var pausing = action == DownloadClientActions.Pause;
        var rig = new Rig(app, has: true, paused: !pausing);

        await Run(app, rig, action);

        Assert.NotNull(rig.LastChange);
        Assert.Contains(IdOf(app), rig.LastChange, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task A_job_the_client_never_had_is_a_plain_failure_and_nothing_is_sent(string app, string action)
    {
        var rig = new Rig(app, has: false, paused: false);

        var result = await Run(app, rig, action);

        Assert.False(result.Succeeded, "an id the client does not have read as done");
        Assert.Contains("has no download with that id", result.Message);
        Assert.Equal(0, rig.Changes);
    }

    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task A_client_that_says_yes_and_changes_nothing_is_a_failure(string app, string action)
    {
        var pausing = action == DownloadClientActions.Pause;
        var rig = new Rig(app, has: true, paused: !pausing, obeys: false);

        var result = await Run(app, rig, action);

        Assert.False(result.Succeeded, "the client said yes and the job is not in the asked state");
        Assert.Contains(pausing ? "still has this download running" : "still has this download paused", result.Message);
        Assert.Equal(1, rig.Changes);
    }

    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task A_client_that_cannot_be_asked_changes_nothing(string app, string action)
    {
        var rig = new Rig(app, has: true, paused: action == DownloadClientActions.Resume, listingFails: true);

        var result = await Run(app, rig, action);

        Assert.False(result.Succeeded);
        Assert.Equal(0, rig.Changes);
    }

    /// <summary>A job already in the asked state is done, and nothing is sent to change it.</summary>
    [Theory]
    [MemberData(nameof(EveryClientAndAction))]
    public async Task A_job_already_in_the_asked_state_is_done_without_a_call(string app, string action)
    {
        var rig = new Rig(app, has: true, paused: action == DownloadClientActions.Pause);

        var result = await Run(app, rig, action);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, rig.Changes);
    }

    /// <summary>
    /// The recorded SABnzbd 5.1.3 reply to a pause and to a resume of an id it never had is <c>status:true</c>. Replaying exactly
    /// that, the unknown id still reads as a failure.
    /// </summary>
    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    public async Task SABnzbds_yes_to_an_id_it_never_had_is_not_believed(string action)
    {
        var unknown = RecordedReplies.Sabnzbd("unknown-id");
        var queueRead = unknown.Find("mode=queue&output=json").Reply;
        var calls = 0;
        var handler = RecordedReplies.Handler("sabnzbd", [unknown], (request, _) =>
        {
            var url = request.RequestUri!.PathAndQuery;
            if (url.Contains("name=pause", StringComparison.Ordinal) || url.Contains("name=resume", StringComparison.Ordinal))
            {
                calls++;
                return null;
            }

            return url.Contains("mode=queue", StringComparison.Ordinal) ? queueRead.ToResponse() : null;
        });

        var result = await new SabnzbdDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("sabnzbd"), action, "SABnzbd_nzo_doesnotexist", CancellationToken.None);

        Assert.False(result.Succeeded, result.Message);
        Assert.NotEqual("SABnzbd action sent.", result.Message);
        Assert.Equal(0, calls);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 8080, null, "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
