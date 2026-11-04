using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1470, next door: the follow-up calls Deluge and uTorrent make after a grab. Transmission, SABnzbd and NZBGet make none: their
/// add call carries the category, folder and priority itself, so a refusal of those is the add's own refusal, which is already read.
/// Deluge's refusals are Deluge 2.2.0's recorded ones (<c>label-unknown</c>, <c>label-plugin-off</c>). Not yet recorded, so not
/// tested: a refused <c>core.set_torrent_options</c> (Deluge) and a refused <c>setprops</c> (uTorrent answers 200 for a hash it does not hold).
/// </summary>
public sealed class DelugeAndUTorrentSayWhatTheyRefusedTests
{
    private const string Hash = "d8185091144732e990e3595f7db0e4410b1d9424";

    private static DownloadClientGrabRequest Request(string? saveFolder = null)
        => new("Example.Movie.2024.1080p", $"magnet:?xt=urn:btih:{Hash}&dn=Example", "movies", "movies", "Fixture indexer") { SaveFolder = saveFolder };

    private static async Task<DownloadClientGrabResult> Grab(Func<HttpRequestMessage, string, HttpResponseMessage?> label, string? saveFolder = null)
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")], (request, body) =>
        {
            if (body.Contains("core.add_torrent_", StringComparison.Ordinal))
                return RecordedReplies.Json($"{{\"id\":2,\"result\":\"{Hash}\",\"error\":null}}");
            if (body.Contains("core.get_session_state", StringComparison.Ordinal))
                return RecordedReplies.Json($"{{\"id\":2,\"result\":[],\"error\":null}}");
            return label(request, body);
        });
        var handler = calls.Build();
        return await new DelugeDownloadClient(new Factory(handler)).GrabAsync(Client("deluge"), Request(saveFolder), CancellationToken.None);
    }

    [Fact]
    public async Task A_label_deluge_does_not_have_is_handed_back_in_its_own_words_and_the_grab_stands()
    {
        var result = await Grab((_, body) => body.Contains("label.set_torrent", StringComparison.Ordinal)
            ? RecordedReplies.Deluge("label-unknown").Find("label.set_torrent").Reply.ToResponse()
            : null);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("label.set_torrent", refusal.Call);
        Assert.Equal("the movies label", refusal.What);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("Unknown Label", refusal.Reason);
    }

    [Fact]
    public async Task A_deluge_with_the_Label_plugin_off_says_so_instead_of_leaving_the_torrent_unlabelled_in_silence()
    {
        var result = await Grab((_, body) => body.Contains("label.get_labels", StringComparison.Ordinal)
            ? RecordedReplies.Deluge("label-plugin-off").Response("label.get_labels")
            : null);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("label.set_torrent", refusal.Call);
        Assert.Equal("Unknown method", refusal.Reason);
        Assert.False(refusal.CanStopImport);
    }

    [Fact]
    public async Task A_label_deluge_takes_hands_back_no_refusal()
    {
        var result = await Grab((_, _) => null);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(["label.set_torrent"], result.FollowUpCallsSent);
    }

    [Fact]
    public void Deluges_traceback_is_cut_down_to_what_it_said()
    {
        Assert.Equal("Unknown Label", DelugeDownloadClient.ReasonOf("Failure: [Failure instance]\nTraceback\n  File x\nException: Unknown Label\n\n]"));
        Assert.Equal("Unknown method", DelugeDownloadClient.ReasonOf("Unknown method"));
        Assert.Equal(string.Empty, DelugeDownloadClient.ReasonOf(null));
        Assert.Equal(200, DelugeDownloadClient.ReasonOf(new string('x', 500)).Length);
    }
}
