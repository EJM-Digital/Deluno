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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_folder_on_a_missing_drive_or_values_of_the_wrong_type_are_accepted_by_deluge_so_nothing_is_said(int exchange)
    {
        var recorded = RecordedReplies.Deluge("torrent-options-refused");
        var result = await Grab((_, body) => body.Contains("core.set_torrent_options", StringComparison.Ordinal) ? recorded.Exchanges[exchange].Reply.ToResponse() : null, saveFolder: "/golden/recorded/deluge/save");

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(["label.set_torrent", "core.set_torrent_options"], result.FollowUpCallsSent);
    }

    [Fact]
    public async Task A_torrent_id_deluge_never_had_is_handed_back_with_the_error_line_of_its_traceback()
    {
        var recorded = RecordedReplies.Deluge("torrent-options-refused");
        var result = await Grab((_, body) => body.Contains("core.set_torrent_options", StringComparison.Ordinal) ? recorded.Exchanges[2].Reply.ToResponse() : null, saveFolder: "/golden/recorded/deluge/save");

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("core.set_torrent_options", refusal.Call);
        Assert.Equal("the save folder /golden/recorded/deluge/save", refusal.What);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("KeyError: '0000000000000000000000000000000000000000'", refusal.Reason);
    }

    // ---- uTorrent's label, from label-refused ----

    private static bool SetProps(HttpRequestMessage request) => request.RequestUri!.Query.Contains("action=setprops", StringComparison.Ordinal);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task An_unknown_property_a_missing_hash_or_a_400_character_label_are_answered_200_by_uTorrent_so_nothing_is_said(int exchange)
    {
        var recorded = RecordedReplies.Utorrent("label-refused");
        var result = await new UTorrentDownloadClient(() => UTorrentEveryBranchTests.Adding((request, _) => SetProps(request) ? recorded.Exchanges[exchange].Reply.ToResponse() : null))
            .GrabAsync(UTorrentEveryBranchTests.Item(), UTorrentEveryBranchTests.Magnet(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(["setprops"], result.FollowUpCallsSent);
    }

    [Fact]
    public async Task A_label_call_uTorrent_refuses_for_want_of_a_token_is_handed_back_in_its_own_words()
    {
        var recorded = RecordedReplies.Utorrent("label-refused");
        var result = await new UTorrentDownloadClient(() => UTorrentEveryBranchTests.Adding((request, _) => SetProps(request) ? recorded.Exchanges[3].Reply.ToResponse() : null))
            .GrabAsync(UTorrentEveryBranchTests.Item(), UTorrentEveryBranchTests.Magnet(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setprops", refusal.Call);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("invalid request", refusal.Reason);
    }

    [Fact]
    public async Task A_release_whose_hash_Deluno_could_not_know_is_followed_by_the_new_torrent_and_its_refused_label_is_still_said()
    {
        var recorded = RecordedReplies.Utorrent("label-refused");
        var result = await new UTorrentDownloadClient(() => UTorrentEveryBranchTests.Adding((request, _) => SetProps(request) ? recorded.Exchanges[3].Reply.ToResponse() : null))
            .GrabAsync(UTorrentEveryBranchTests.Item(), UTorrentEveryBranchTests.Magnet() with { DownloadUrl = "http://indexer.test/download/1" }, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("invalid request", refusal.Reason);
        Assert.Equal(["setprops"], result.FollowUpCallsSent);
    }

    [Fact]
    public void Deluges_traceback_is_cut_down_to_what_it_said()
    {
        Assert.Equal("Unknown Label", DelugeDownloadClient.ReasonOf("Failure: [Failure instance]\nTraceback\n  File x\nException: Unknown Label\n\n]"));
        Assert.Equal("Unknown method", DelugeDownloadClient.ReasonOf("Unknown method"));
        Assert.Equal(string.Empty, DelugeDownloadClient.ReasonOf(null));
        Assert.Equal(200, DelugeDownloadClient.ReasonOf(new string('x', 500)).Length);
        // A reply of nothing but closing brackets has no error line to give.
        Assert.Equal(string.Empty, DelugeDownloadClient.ReasonOf("]\n]"));
    }
}
