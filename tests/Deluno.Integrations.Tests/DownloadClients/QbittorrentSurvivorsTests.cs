using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1269: the qBittorrent mutants the branch tests let through, from qBittorrent 5.2.1's recorded replies: what it can do, that
/// every call signs in first, the words of the keep-seeding category check, and the estimate it gives for "no estimate".
/// </summary>
public sealed class QbittorrentSurvivorsTests
{
    private static readonly string Finished = RecordedReplies.Qbittorrent("category-moved").Hash();

    private static readonly string[] BaseStates =
    [
        "sign-in", "version", "settings", "idle", "trackers", "category-moved", "auto-management-set", "location-moved", "share-limits",
        "finished-single-file", "paused", "queued", "downloading"
    ];

    private static DownloadClientGrabRequest Request()
        => new DownloadClientGrabRequest("Example.Movie.2026.1080p", "magnet:?xt=urn:btih:" + Finished + "&dn=Example", "movies", "movies", "Example Indexer") with { KnownInfoHash = Finished };

    private static string FinishedInfo() => RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info");

    /// <summary>The client lists nothing before the add and the finished torrent from then on.</summary>
    private static (QbittorrentDownloadClient Client, Calls Calls) Arriving(Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null)
    {
        var infoCalls = 0;
        return Rig((request, body) => Is(request, "torrents/info") ? RecordedReplies.Json(++infoCalls == 1 ? "[]" : FinishedInfo()) : Is(request, "torrents/add") ? RecordedReplies.Json("Ok.") : more?.Invoke(request, body));
    }

    private static (QbittorrentDownloadClient Client, Calls Calls) Listing(Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null)
        => Rig((request, body) => Is(request, "torrents/info") ? RecordedReplies.Json(FinishedInfo()) : more?.Invoke(request, body));

    private static DownloadClientItem Item() => Client("qbittorrent", "deluno", "secret");

    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code) { Content = new StringContent(string.Empty) };

    private static bool Is(HttpRequestMessage request, string tail) => request.RequestUri!.AbsolutePath.EndsWith(tail, StringComparison.Ordinal);

    private static (QbittorrentDownloadClient Client, Calls Calls) Rig(Func<HttpRequestMessage, string, HttpResponseMessage?>? answer = null)
    {
        var calls = new Calls("qbittorrent", BaseStates.Select(RecordedReplies.Qbittorrent).ToArray(), answer);
        var handler = calls.Build();
        return (new QbittorrentDownloadClient(() => handler), calls);
    }

    [Fact]
    public void It_says_what_it_can_do_and_who_it_is()
    {
        var client = new QbittorrentDownloadClient();

        Assert.Equal("qbittorrent", client.Protocol);
        var capabilities = client.Capabilities;
        Assert.True(capabilities.SupportsQueue);
        Assert.False(capabilities.SupportsHistory);
        Assert.True(capabilities.SupportsPauseResume);
        Assert.True(capabilities.SupportsRemove);
        Assert.True(capabilities.SupportsRecheck);
        Assert.True(capabilities.SupportsImportPath);
        Assert.Equal("form", capabilities.AuthMode);
        Assert.True(capabilities.SupportsQueueReorder);
        Assert.True(capabilities.SupportsForceStart);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-5L, 0)]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(3600L, 3600)]
    [InlineData(8_639_999L, 8_639_999)]
    [InlineData(8_640_000L, 0)]
    [InlineData(9_000_000L, 0)]
    public void The_clients_no_estimate_reading_is_zero_and_a_real_one_is_kept(long? eta, int expected)
        => Assert.Equal(expected, QbittorrentDownloadClient.EtaSeconds(eta));

    [Fact]
    public async Task Every_call_that_talks_to_the_client_signs_in_first()
    {
        var (grab, grabCalls) = Rig((request, _) => Is(request, "torrents/add") ? RecordedReplies.Json("Ok.") : null);
        await grab.GrabAsync(Item(), Request(), CancellationToken.None);
        Assert.True(grabCalls.Sent("auth/login"), "grab");

        var (push, pushCalls) = Rig();
        await push.PushExcludedFileNamesAsync(Item(), ["*.exe"], CancellationToken.None);
        Assert.True(pushCalls.Sent("auth/login"), "excluded file names");

        var (move, moveCalls) = Listing();
        await move.SetCategoryAsync(Item(), Finished, "recorded-movies", null, CancellationToken.None);
        Assert.True(moveCalls.Sent("auth/login"), "set category");

        var (check, checkCalls) = Rig();
        await check.CheckKeepSeedingCategoryAsync(Item(), "recorded-movies", CancellationToken.None);
        Assert.True(checkCalls.Sent("auth/login"), "keep-seeding check");
    }

    [Fact]
    public async Task A_client_that_turns_the_add_down_with_a_server_error_fails_the_grab()
    {
        var (client, _) = Rig((request, _) => Is(request, "torrents/add") ? Status(500) : null);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GrabAsync(Item(), Request(), CancellationToken.None));
    }

    [Fact]
    public async Task A_grab_still_succeeds_when_each_follow_up_call_is_turned_down()
    {
        var (client, calls) = Arriving((request, _) =>
            Is(request, "setCategory") || Is(request, "setAutoManagement") || Is(request, "setLocation") || Is(request, "setShareLimits") ? Status(500) : null);

        var item = Item() with { Location = "/data/movies", ShareRatioLimit = 2 };
        var result = await client.GrabAsync(item, Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Release URL sent to qBittorrent.", result.Message);
        Assert.Equal(Finished, result.ExternalId);
        Assert.True(calls.Sent("setShareLimits"));
    }

    [Fact]
    public async Task Share_limits_not_set_are_sent_as_the_clients_own_default_and_a_set_one_as_given()
    {
        var (client, calls) = Arriving();
        var withLimits = Item() with { ShareRatioLimit = 1.5, ShareSeedingTimeLimitMinutes = 90, ShareInactiveSeedingTimeLimitMinutes = 30 };

        await client.GrabAsync(withLimits, Request(), CancellationToken.None);
        var set = calls.Log.Single(line => line.Contains("setShareLimits", StringComparison.Ordinal));
        Assert.Contains("ratioLimit=1.5", set);
        Assert.Contains("seedingTimeLimit=90", set);
        Assert.Contains("inactiveSeedingTimeLimit=30", set);
        Assert.Contains("shareLimitAction=Default", set);

        var (plain, plainCalls) = Arriving();
        await plain.GrabAsync(Item() with { ShareRatioLimit = 0 }, Request(), CancellationToken.None);
        var unset = plainCalls.Log.SingleOrDefault(line => line.Contains("setShareLimits", StringComparison.Ordinal));
        if (unset is not null)
        {
            Assert.Contains("seedingTimeLimit=-2", unset);
            Assert.Contains("inactiveSeedingTimeLimit=-2", unset);
        }
    }

    [Fact]
    public async Task The_keep_seeding_check_says_each_answer_in_its_own_words()
    {
        var blank = await Rig().Client.CheckKeepSeedingCategoryAsync(Item(), " ", CancellationToken.None);
        var noAddress = await Rig().Client.CheckKeepSeedingCategoryAsync(Item() with { Host = null, EndpointUrl = null }, "seeding", CancellationToken.None);
        var unsupported = await Rig((request, _) => Is(request, "torrents/categories") ? Status(404) : null).Client.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None);
        var failing = await Rig((request, _) => Is(request, "torrents/categories") ? Status(502) : null).Client.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None);
        var categories = RecordedReplies.Qbittorrent("settings").Body("torrents/categories", ("><scrubbed>-movies", "recorded-movies"));
        var listed = Rig((request, _) => Is(request, "torrents/categories") ? RecordedReplies.Json(categories) : null).Client;
        var ready = await listed.CheckKeepSeedingCategoryAsync(Item(), " RECORDED-MOVIES ", CancellationToken.None);
        var missing = await listed.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None);
        var down = await Rig((request, _) => throw new HttpRequestException("refused")).Client.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None);

        Assert.Equal((DownloadClientCategoryStatuses.Configuration, "Enter a category before checking it.", true, false), (blank.Status, blank.Message, blank.Supported, blank.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Configuration, "Add the qBittorrent address before checking a category.", true, false), (noAddress.Status, noAddress.Message, noAddress.Supported, noAddress.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Unsupported, "This qBittorrent does not list its categories, so Deluno cannot check this one. It will still try the move.", false, false), (unsupported.Status, unsupported.Message, unsupported.Supported, unsupported.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Unreachable, "qBittorrent returned 502 while checking its categories.", true, false), (failing.Status, failing.Message, failing.Supported, failing.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Ready, "qBittorrent has a category named RECORDED-MOVIES.", true, true), (ready.Status, ready.Message, ready.Supported, ready.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Missing, "qBittorrent does not have a category named seeding. Create it in qBittorrent first; Deluno does not create categories.", true, false), (missing.Status, missing.Message, missing.Supported, missing.Found));
        Assert.Equal((DownloadClientCategoryStatuses.Unreachable, "Deluno could not reach qBittorrent: refused", true, false), (down.Status, down.Message, down.Supported, down.Found));
    }

    [Fact]
    public async Task A_category_move_says_what_went_wrong_in_words()
    {
        var (missingCategory, _) = Listing((request, _) => Is(request, "setCategory") ? Status(409) : null);
        var (refused, _) = Listing((request, _) => Is(request, "setCategory") ? Status(403) : null);
        var moved = false;
        var ok = Rig((request, _) =>
        {
            if (Is(request, "setCategory")) moved = true;
            return Is(request, "torrents/info")
                ? RecordedReplies.Json(moved ? RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info", ("0.category", "seeding")) : FinishedInfo())
                : null;
        }).Client;

        var no = await missingCategory.SetCategoryAsync(Item(), Finished, "seeding", "recorded-movies", CancellationToken.None);
        var bad = await refused.SetCategoryAsync(Item(), Finished, "seeding", "recorded-movies", CancellationToken.None);
        var done = await ok.SetCategoryAsync(Item(), Finished, "seeding", "recorded-movies", CancellationToken.None);

        Assert.False(no.Succeeded);
        Assert.Equal("qBittorrent does not have a category named seeding. Create it in qBittorrent first; Deluno does not create categories.", no.Message);
        Assert.False(bad.Succeeded);
        Assert.Equal("qBittorrent returned 403 when asked to set the category.", bad.Message);
        Assert.True(done.Succeeded, done.Message);
        Assert.StartsWith("qBittorrent moved this download to seeding", done.Message);
    }
}
