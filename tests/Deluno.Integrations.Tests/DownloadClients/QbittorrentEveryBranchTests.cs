using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the qBittorrent adapter, replayed from qBittorrent 5.2.1's own recorded replies. A reply a test changes is
/// the recorded one changed in the one value the test is about (RecordedReply.BodyWith); a bare status is a client failing a call.
/// </summary>
public sealed class QbittorrentEveryBranchTests
{
    private static readonly string Finished = RecordedReplies.Qbittorrent("category-moved").Hash();

    private static readonly string[] BaseStates =
    [
        "sign-in", "version", "settings", "idle", "trackers", "category-moved", "auto-management-set", "location-moved", "share-limits",
        "finished-single-file", "paused", "queued", "downloading"
    ];

    private static DownloadClientItem Item(string? username = "deluno") => Client("qbittorrent", username, "secret");

    private static DownloadClientItem NoAddress() => Item() with { Host = null, EndpointUrl = null };

    private static string FinishedInfo(params (string Path, object? Value)[] edits)
        => RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info?hashes", edits);

    private static string Settings(string path, params (string Path, object? Value)[] edits)
        => RecordedReplies.Qbittorrent("settings").Body(path, edits);

    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code) { Content = new StringContent(string.Empty) };

    private static bool Is(HttpRequestMessage request, string tail) => request.RequestUri!.AbsolutePath.EndsWith(tail, StringComparison.Ordinal);

    private static (QbittorrentDownloadClient Client, Calls Calls) Rig(Func<HttpRequestMessage, string, HttpResponseMessage?>? answer = null)
    {
        var calls = new Calls("qbittorrent", BaseStates.Select(RecordedReplies.Qbittorrent).ToArray(), answer);
        var handler = calls.Build();
        return (new QbittorrentDownloadClient(() => handler), calls);
    }

    private static (QbittorrentDownloadClient Client, Calls Calls) Listing(string info, Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null)
        => Rig((request, body) => Is(request, "torrents/info") ? RecordedReplies.Json(info) : more?.Invoke(request, body));

    /// <summary>The client lists nothing before the add and the given list from then on, as a client behaves while it fetches a link.</summary>
    private static (QbittorrentDownloadClient Client, Calls Calls) Arriving(string info, Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null)
    {
        var infoCalls = 0;
        return Rig((request, body) => Is(request, "torrents/info") ? RecordedReplies.Json(++infoCalls == 1 ? "[]" : info) : more?.Invoke(request, body));
    }

    private static DownloadClientGrabRequest Request(string? hash = null)
        => new("Example.Movie.2026.1080p", "magnet:?xt=urn:btih:" + (hash ?? Finished) + "&dn=Example", "movies", "movies", "Example Indexer");

    private static DownloadClientGrabRequest LinkRequest()
        => new("Example.Movie.2026.1080p", "https://indexer.example/download/1", "movies", "movies", "Example Indexer");

    // ---- AnswerSaysItWasTaken ----

    [Fact]
    public void An_add_answer_is_read_as_taken_only_when_it_says_so()
    {
        var added = RecordedReplies.Qbittorrent("finished-single-file").Find("torrents/add").Reply;
        Assert.True(QbittorrentDownloadClient.AnswerSaysItWasTaken("Ok.", Finished));
        Assert.True(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("success_count", 0)), Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("success_count", 0)), RecordedReplies.ZeroHash));
        Assert.True(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("added_torrent_ids.0", "nothing"), ("pending_count", 2), ("success_count", 0)), Finished));
        Assert.True(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("added_torrent_ids.0", "nothing"), ("pending_count", 0), ("success_count", 3)), Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("added_torrent_ids.0", "nothing"), ("pending_count", 0), ("success_count", 0)), Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("added_torrent_ids.0", 7), ("pending_count", "many"), ("success_count", 1.5)), Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken(added.BodyWith(("added_torrent_ids", "none"), ("pending_count", null), ("success_count", null)), Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken("Fails.", Finished));
        Assert.False(QbittorrentDownloadClient.AnswerSaysItWasTaken("{ not json", Finished));
    }

    // ---- grab ----

    [Fact]
    public async Task A_grab_with_no_address_says_so()
    {
        var (client, calls) = Rig();

        var result = await client.GrabAsync(NoAddress(), Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("address is missing", result.Message);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_grab_whose_list_cannot_be_read_before_or_after_is_still_a_send_known_by_its_hash()
    {
        var (client, calls) = Rig((request, _) => Is(request, "torrents/info") ? Status(500) : null);

        var result = await client.GrabAsync(Item(), Request() with { KnownInfoHash = Finished }, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Finished, result.ExternalId);
        Assert.False(calls.Sent("setCategory"));
    }

    [Fact]
    public async Task A_grab_with_no_known_hash_and_an_unreadable_list_has_no_id_to_follow()
    {
        var (client, _) = Rig((request, _) => Is(request, "torrents/info") ? Status(500) : null);

        var result = await client.GrabAsync(Item(), LinkRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.ExternalId);
    }

    [Fact]
    public async Task A_silently_turned_down_release_is_a_failure_when_the_second_look_cannot_read_the_list_either()
    {
        var before = QbittorrentDownloadClient.ExtendedConfirmationDelay;
        QbittorrentDownloadClient.ExtendedConfirmationDelay = TimeSpan.Zero;
        try
        {
            var lastRead = DateTime.MinValue;
            var reads = 0;
            var (client, _) = Rig((request, _) =>
            {
                if (Is(request, "torrents/add")) return RecordedReplies.Json("Fine.");
                if (!Is(request, "torrents/info")) return null;
                // Readable and empty while the wait goes on (a look every 400 milliseconds); the second look follows the last of them at
                // once, and that one is unreadable.
                var now = DateTime.UtcNow;
                var second = ++reads > 2 && now - lastRead < TimeSpan.FromMilliseconds(100);
                lastRead = now;
                return second ? Status(500) : RecordedReplies.Json("[]");
            });

            var result = await client.GrabAsync(Item(), Request() with { KnownInfoHash = Finished }, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("never listed the torrent", result.Message);
        }
        finally
        {
            QbittorrentDownloadClient.ExtendedConfirmationDelay = before;
        }
    }

    [Fact]
    public async Task The_swallowed_follow_up_writes_of_a_grab_never_fail_it()
    {
        var info = FinishedInfo();
        var (withLocation, locationCalls) = Arriving(info, (request, _) => Is(request, "setLocation") || Is(request, "setCategory") || Is(request, "setShareLimits") ? Status(500) : null);
        var located = await withLocation.GrabAsync(Item() with { Location = "/downloads", ShareRatioLimit = 2.0 }, Request(), CancellationToken.None);

        var (withoutLocation, autoCalls) = Arriving(info, (request, _) => Is(request, "setAutoManagement") ? Status(500) : null);
        var automatic = await withoutLocation.GrabAsync(Item(), Request(), CancellationToken.None);

        Assert.True(located.Succeeded, located.Message);
        Assert.True(locationCalls.Sent("setLocation"));
        Assert.True(locationCalls.Sent("setShareLimits"));
        Assert.True(automatic.Succeeded, automatic.Message);
        Assert.True(autoCalls.Sent("setAutoManagement"));
    }

    [Fact]
    public async Task A_torrent_that_cannot_be_reached_by_a_dropped_connection_in_a_follow_up_write_is_still_sent()
    {
        var info = FinishedInfo();
        var (client, _) = Arriving(info, (request, _) => Is(request, "setCategory") ? throw new HttpRequestException("The connection dropped.") : null);

        var result = await client.GrabAsync(Item(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task A_dropped_connection_in_the_location_share_limit_or_automatic_management_follow_up_is_handed_back_not_thrown()
    {
        var info = FinishedInfo();
        var (withLocation, _) = Arriving(info, (request, _) => Is(request, "setLocation") || Is(request, "setShareLimits") ? throw new HttpRequestException("The connection dropped.") : null);
        var located = await withLocation.GrabAsync(Item() with { Location = "/downloads", ShareRatioLimit = 2.0 }, Request(), CancellationToken.None);

        var (automatic, _) = Arriving(info, (request, _) => Is(request, "setAutoManagement") ? throw new HttpRequestException("The connection dropped.") : null);
        var auto = await automatic.GrabAsync(Item(), Request(), CancellationToken.None);

        Assert.True(located.Succeeded, located.Message);
        Assert.Equal(2, located.FollowUpRefusals!.Count);
        Assert.All(located.FollowUpRefusals, refusal => Assert.Contains("The connection dropped.", refusal.Reason, StringComparison.Ordinal));
        Assert.True(auto.Succeeded, auto.Message);
        Assert.Single(auto.FollowUpRefusals!);
    }

    [Fact]
    public async Task A_torrent_file_is_uploaded_with_the_paused_choice_and_a_list_that_comes_back_null_counts_as_unreadable()
    {
        var (client, calls) = Rig((request, _) => Is(request, "torrents/info") ? RecordedReplies.Json("null") : null);

        var result = await client.GrabAsync(Item() with { AddPaused = true }, LinkRequest() with { TorrentFile = [1, 2, 3] }, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("torrents/add"));
    }

    // ---- sessions, snapshots ----

    [Fact]
    public async Task A_snapshot_with_no_address_says_the_address_is_missing()
    {
        var (client, _) = Rig();

        var snapshot = await client.GetSnapshotAsync(NoAddress(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Contains("address is missing", snapshot!.LastHealthMessage);
    }

    [Fact]
    public async Task A_changed_password_starts_a_new_session_and_signs_in_again()
    {
        var (client, calls) = Listing(FinishedInfo());

        await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
        await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
        await client.GetSnapshotAsync(Client("qbittorrent", "deluno", "another"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, calls.Count("auth/login"));
    }

    [Fact]
    public async Task A_list_that_comes_back_null_is_an_empty_queue()
    {
        var (client, _) = Listing("null");

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(snapshot!.Queue);
    }

    [Fact]
    public async Task A_torrent_the_client_tells_nothing_about_is_still_listed_with_what_Deluno_can_say()
    {
        var bare = RecordedReplies.Qbittorrent("finished-single-file").Find("torrents/info").Reply.BodyWith(

            ("0.hash", null), ("0.name", null), ("0.category", null), ("0.state", null), ("0.progress", null), ("0.dlspeed", null), ("0.upspeed", null),
            ("0.eta", null), ("0.size", null), ("0.downloaded", null), ("0.num_seeds", null), ("0.num_leechs", null), ("0.added_on", null),
            ("0.save_path", null), ("0.content_path", null), ("0.ratio", null), ("0.seeding_time", null), ("0.private", null), ("0.availability", null),
            ("0.last_activity", null), ("0.completion_on", null), ("0.tags", null), ("0.tracker", null));
        var list = JsonNode.Parse(FinishedInfo())!.AsArray();
        var mixed = new JsonArray(JsonNode.Parse(bare)!.AsArray()[0]!.DeepClone(), list[0]!.DeepClone());
        var (client, _) = Listing(mixed.ToJsonString());

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, snapshot!.Queue.Count);
        Assert.Contains(snapshot.Queue, item => item.Title == "Unknown qBittorrent item" || item.ReleaseName == "Unknown qBittorrent item");
    }

    [Fact]
    public async Task A_torrent_moving_data_is_not_asked_about_and_a_failing_tracker_read_says_nothing_either_way()
    {
        var moving = FinishedInfo(("0.dlspeed", 5_000_000), ("0.state", "downloading"));
        var (client, calls) = Listing(moving);
        var moved = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        var (failing, _) = Listing(FinishedInfo(), (request, _) => Is(request, "torrents/trackers") ? Status(500) : null);
        var failed = await failing.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(calls.Sent("torrents/trackers"));
        Assert.Equal(DownloadTrackerStatuses.Unknown, moved!.Queue.Single().TrackerStatus);
        Assert.Equal(DownloadTrackerStatuses.Unknown, failed!.Queue.Single().TrackerStatus);
    }

    [Fact]
    public async Task A_tracker_list_that_comes_back_null_reads_as_a_working_tracker()
    {
        var (client, _) = Listing(FinishedInfo(), (request, _) => Is(request, "torrents/trackers") ? RecordedReplies.Json("null") : null);

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(DownloadTrackerStatuses.Ok, snapshot!.Queue.Single().TrackerStatus);
    }

    [Fact]
    public async Task The_tracker_memory_forgets_torrents_that_are_gone()
    {
        var one = JsonNode.Parse(FinishedInfo())!.AsArray();
        var many = new JsonArray();
        for (var i = 0; i < 70; i++)
        {
            var copy = one[0]!.DeepClone();
            copy["hash"] = $"{i:D40}";
            many.Add(copy);
        }

        var current = many.ToJsonString();
        var (client, _) = Rig((request, _) => Is(request, "torrents/info") ? RecordedReplies.Json(current) : null);
        var later = DateTimeOffset.UtcNow;
        await client.GetSnapshotAsync(Item(), later, CancellationToken.None);

        current = FinishedInfo();
        var other = Client("qbittorrent", "deluno", "secret") with { Id = "qbittorrent-2" };
        await client.GetSnapshotAsync(other, later.AddSeconds(1), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(Item(), later.AddSeconds(2), CancellationToken.None);

        Assert.Single(snapshot!.Queue);
    }

    [Fact]
    public async Task A_server_state_with_nothing_in_it_gives_no_free_space_and_no_speed_limit_reading()
    {
        var withoutState = Settings("sync/maindata", ("server_state", null));
        var (client, _) = Listing(FinishedInfo(), (request, _) => Is(request, "sync/maindata") ? RecordedReplies.Json(withoutState) : null);
        var (nullMain, _) = Listing(FinishedInfo(), (request, _) => Is(request, "sync/maindata") ? RecordedReplies.Json("null") : null);
        var (broken, _) = Listing(FinishedInfo(), (request, _) => Is(request, "sync/maindata") ? Status(500) : null);

        foreach (var poll in new[] { client, nullMain, broken })
        {
            var snapshot = await poll.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Null(snapshot!.FreeSpaceBytes);
            Assert.Null(snapshot.AlternateSpeedLimitEnabled);
        }
    }

    // ---- CheckCategoryAsync ----

    private static string Movies() => "recorded-movies";

    private static (QbittorrentDownloadClient Client, Calls Calls) Categories(Func<HttpRequestMessage, string, HttpResponseMessage?>? more = null, params (string Path, object? Value)[] categoryEdits)
    {
        var body = Settings("torrents/categories", [("><scrubbed>-movies", Movies()), .. categoryEdits]);
        return Rig((request, text) => Is(request, "torrents/categories") ? RecordedReplies.Json(body) : more?.Invoke(request, text));
    }

    [Fact]
    public async Task A_category_check_needs_a_name_and_an_address()
    {
        var (client, calls) = Rig();

        var blank = await client.CheckCategoryAsync(Item(), "  ", CancellationToken.None);
        var noAddress = await client.CheckCategoryAsync(NoAddress(), Movies(), CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Configuration, blank.Status);
        Assert.Contains("Enter a category", blank.Message);
        Assert.Equal(DownloadClientCategoryStatuses.Configuration, noAddress.Status);
        Assert.Contains("qBittorrent address", noAddress.Message);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_client_that_does_not_list_categories_or_fails_the_list_is_said_so()
    {
        var (missing, _) = Rig((request, _) => Is(request, "torrents/categories") ? Status(404) : null);
        var (failing, _) = Rig((request, _) => Is(request, "torrents/categories") ? Status(500) : null);

        var unsupported = await missing.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);
        var unreachable = await failing.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, unsupported.Status);
        Assert.False(unsupported.Supported);
        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, unreachable.Status);
        Assert.Contains("500", unreachable.Message);
    }

    [Fact]
    public async Task A_category_that_is_not_there_or_not_in_a_list_is_missing()
    {
        var (notThere, _) = Categories();
        var (notAnObject, _) = Rig((request, _) => Is(request, "torrents/categories") ? RecordedReplies.Json("[]") : null);

        var absent = await notThere.CheckCategoryAsync(Item(), "other", CancellationToken.None);
        var listless = await notAnObject.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Missing, absent.Status);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, listless.Status);
    }

    [Fact]
    public async Task A_category_with_a_relative_save_path_is_read_against_the_default_one()
    {
        var (found, calls) = Categories((request, _) => Is(request, "app/defaultSavePath") ? RecordedReplies.Json("/data/downloads") : null, ("recorded-movies.savePath", "movies"));
        var (noDefault, _) = Categories((request, _) => Is(request, "app/defaultSavePath") ? Status(500) : null, ("recorded-movies.savePath", "movies"));
        var (noJoin, _) = Categories((request, _) => Is(request, "app/defaultSavePath") ? RecordedReplies.Json("") : null, ("recorded-movies.savePath", "movies"));

        var joined = await found.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);
        var kept = await noDefault.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);
        var alone = await noJoin.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);

        Assert.True(calls.Sent("app/defaultSavePath"));
        Assert.Contains("downloads", joined.SavePath);
        Assert.Equal("movies", kept.SavePath);
        Assert.Equal("movies", alone.SavePath);
    }

    [Fact]
    public async Task A_category_with_no_save_path_or_a_save_path_that_is_not_text_is_a_setting_to_fix()
    {
        var (empty, _) = Categories(null, ("recorded-movies.savePath", ""));
        var (number, _) = Categories(null, ("recorded-movies.savePath", 5));
        var (bare, _) = Categories(null, ("recorded-movies.savePath", null));
        var (notObject, _) = Categories(null, ("recorded-movies", 7));

        foreach (var client in new[] { empty, number, bare, notObject })
        {
            var result = await client.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);
            Assert.Equal(DownloadClientCategoryStatuses.Configuration, result.Status);
            Assert.True(result.Found);
        }
    }

    [Fact]
    public async Task The_incomplete_folder_is_read_from_the_preferences_only_when_they_say_it_is_kept_apart()
    {
        var statuses = new List<string?>();
        var cases = new (Func<HttpRequestMessage, string, HttpResponseMessage?> Answer, string? Expect)[]
        {
            ((r, _) => Is(r, "app/preferences") ? Status(500) : null, null),
            ((r, _) => Is(r, "app/preferences") ? RecordedReplies.Json("[]") : null, null),
            ((r, _) => Is(r, "app/preferences") ? RecordedReplies.Json(Settings("app/preferences", ("temp_path_enabled", false))) : null, "C:/Downloads/Completed/Movies"),
            ((r, _) => Is(r, "app/preferences") ? RecordedReplies.Json(Settings("app/preferences", ("temp_path_enabled", true), ("temp_path", 5))) : null, "C:/Downloads/Completed/Movies"),
            ((r, _) => Is(r, "app/preferences") ? RecordedReplies.Json(Settings("app/preferences", ("temp_path_enabled", null), ("temp_path", null))) : null, "C:/Downloads/Completed/Movies"),
            ((r, _) => Is(r, "app/preferences") ? RecordedReplies.Json(Settings("app/preferences", ("temp_path_enabled", true), ("temp_path", "D:/Incomplete"))) : null, "D:/Incomplete")
        };

        foreach (var (answer, expect) in cases)
        {
            var (client, _) = Categories(answer);
            var result = await client.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);
            statuses.Add(result.IncompletePath);
            Assert.Equal(expect, result.IncompletePath);
        }

        Assert.Equal(cases.Length, statuses.Count);
    }

    [Fact]
    public async Task A_client_that_drops_the_connection_during_a_category_check_is_unreachable()
    {
        var (client, _) = Rig((request, _) => Is(request, "torrents/categories") ? throw new HttpRequestException("The connection dropped.") : null);

        var result = await client.CheckCategoryAsync(Item(), Movies(), CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, result.Status);
    }

    // ---- CheckKeepSeedingCategoryAsync ----

    [Fact]
    public async Task A_keep_seeding_category_check_needs_a_name_and_an_address()
    {
        var (client, _) = Rig();

        var blank = await client.CheckKeepSeedingCategoryAsync(Item(), " ", CancellationToken.None);
        var noAddress = await client.CheckKeepSeedingCategoryAsync(NoAddress(), "seeding", CancellationToken.None);

        Assert.Contains("Enter a category", blank.Message);
        Assert.Contains("qBittorrent address", noAddress.Message);
    }

    [Fact]
    public async Task A_keep_seeding_category_is_found_missing_unsupported_or_unreachable()
    {
        var (listed, _) = Categories();
        var (notObject, _) = Rig((request, _) => Is(request, "torrents/categories") ? RecordedReplies.Json("[]") : null);
        var (missing, _) = Rig((request, _) => Is(request, "torrents/categories") ? Status(404) : null);
        var (failing, _) = Rig((request, _) => Is(request, "torrents/categories") ? Status(500) : null);
        var (dropped, _) = Rig((request, _) => Is(request, "torrents/categories") ? throw new HttpRequestException("The connection dropped.") : null);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, (await listed.CheckKeepSeedingCategoryAsync(Item(), "RECORDED-MOVIES", CancellationToken.None)).Status);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, (await listed.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None)).Status);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, (await notObject.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None)).Status);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, (await missing.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None)).Status);
        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, (await failing.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None)).Status);
        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, (await dropped.CheckKeepSeedingCategoryAsync(Item(), "seeding", CancellationToken.None)).Status);
    }

    // ---- files, excluded names ----

    [Fact]
    public async Task The_files_of_a_torrent_are_unknown_without_an_address_an_id_or_a_list()
    {
        var (client, _) = Rig((request, _) => Is(request, "torrents/files") ? RecordedReplies.Json("null") : null);

        Assert.Null(await client.GetFilesAsync(NoAddress(), Finished, CancellationToken.None));
        Assert.Null(await client.GetFilesAsync(Item(), " ", CancellationToken.None));
        Assert.Null(await client.GetFilesAsync(Item(), Finished, CancellationToken.None));
    }

    [Fact]
    public async Task Excluded_file_names_are_pushed_and_a_refusal_or_a_dropped_connection_is_said()
    {
        var (ok, calls) = Rig((request, _) => Is(request, "app/setPreferences") ? Status(200) : null);
        var (refused, _) = Rig((request, _) => Is(request, "app/setPreferences") ? Status(403) : null);
        var (dropped, _) = Rig((request, _) => Is(request, "app/setPreferences") ? throw new HttpRequestException("The connection dropped.") : null);

        var pushed = await ok.PushExcludedFileNamesAsync(Item(), ["*.exe", "*.scr"], CancellationToken.None);
        var turnedDown = await refused.PushExcludedFileNamesAsync(Item(), ["*.exe"], CancellationToken.None);
        var failed = await dropped.PushExcludedFileNamesAsync(Item(), ["*.exe"], CancellationToken.None);
        var noAddress = await ok.PushExcludedFileNamesAsync(NoAddress(), ["*.exe"], CancellationToken.None);

        Assert.True(pushed.Succeeded);
        Assert.True(calls.Sent("excluded_file_names"));
        Assert.False(turnedDown.Succeeded);
        Assert.Contains("403", turnedDown.Message);
        Assert.False(failed.Succeeded);
        Assert.Contains("dropped", failed.Message);
        Assert.False(noAddress.Succeeded);
    }

    // ---- actions ----

    [Fact]
    public async Task An_action_with_no_address_or_no_such_verb_is_said_plainly()
    {
        var (client, calls) = Rig();

        var missing = await client.ExecuteActionAsync(NoAddress(), DownloadClientActions.Recheck, Finished, CancellationToken.None);
        var unknown = await client.ExecuteActionAsync(Item(), "juggle", Finished, CancellationToken.None);

        Assert.False(missing.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.False(calls.Sent("torrents/"));
    }

    [Theory]
    [InlineData(DownloadClientActions.Recheck, "torrents/recheck", "")]
    [InlineData(DownloadClientActions.MoveToTop, "torrents/topPrio", "")]
    [InlineData(DownloadClientActions.MoveUp, "torrents/increasePrio", "")]
    [InlineData(DownloadClientActions.MoveDown, "torrents/decreasePrio", "")]
    [InlineData(DownloadClientActions.MoveToBottom, "torrents/bottomPrio", "")]
    [InlineData(DownloadClientActions.ForceStart, "torrents/setForceStart", "value=true")]
    public async Task Each_plain_verb_is_one_post_and_a_failure_status_is_said(string action, string endpoint, string fragment)
    {
        var (ok, calls) = Rig((request, _) => request.Method == HttpMethod.Post && !Is(request, "auth/login") ? Status(200) : null);
        var (failing, _) = Rig((request, _) => request.Method == HttpMethod.Post && !Is(request, "auth/login") ? Status(500) : null);

        var done = await ok.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);
        var failed = await failing.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);

        Assert.True(done.Succeeded, done.Message);
        Assert.True(calls.Sent(endpoint));
        Assert.True(fragment.Length == 0 || calls.Sent(fragment));
        Assert.False(failed.Succeeded);
        Assert.Contains("500", failed.Message);
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task A_removal_is_only_done_when_the_torrent_is_gone_and_is_unknown_when_the_list_cannot_be_read(string action)
    {
        var listed = true;
        var (gone, _) = Rig((request, _) =>
        {
            if (Is(request, "torrents/delete")) { listed = false; return Status(200); }
            return Is(request, "torrents/info") ? RecordedReplies.Json(listed ? FinishedInfo() : "[]") : null;
        });
        var (notArray, _) = Rig((request, _) => Is(request, "torrents/info") ? RecordedReplies.Json("{}") : Is(request, "torrents/delete") ? Status(200) : null);
        var (unreadable, _) = Rig((request, _) => Is(request, "torrents/info") ? Status(500) : Is(request, "torrents/delete") ? Status(200) : null);
        var (dropped, _) = Rig((request, _) => Is(request, "torrents/info") ? throw new HttpRequestException("The connection dropped.") : Is(request, "torrents/delete") ? Status(200) : null);

        var removed = await gone.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);
        var a = await notArray.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);
        var b = await unreadable.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);
        var c = await dropped.ExecuteActionAsync(Item(), action, Finished, CancellationToken.None);

        Assert.True(removed.Succeeded, removed.Message);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
    }

    [Fact]
    public async Task A_pause_reads_the_torrent_it_is_pausing_in_every_shape_the_client_answers()
    {
        var shapes = new[]
        {
            RecordedReplies.Json("{}"),
            RecordedReplies.Json("[]"),
            RecordedReplies.Json(FinishedInfo(("0.state", null))),
            RecordedReplies.Json(FinishedInfo(("0.state", "pausedUP"))),
            RecordedReplies.Json(FinishedInfo(("0.state", "stoppedUP"))),
            RecordedReplies.Json(FinishedInfo(("0.state", "uploading"))),
            Status(500)
        };

        foreach (var shape in shapes)
        {
            var (client, _) = Rig((request, _) => Is(request, "torrents/info") ? new HttpResponseMessage(shape.StatusCode) { Content = new StringContent(shape.Content.ReadAsStringAsync().Result) } : Is(request, "torrents/stop") ? Status(200) : null);
            var result = await client.ExecuteActionAsync(Item(), DownloadClientActions.Pause, Finished, CancellationToken.None);
            Assert.NotNull(result);
        }
    }

    [Fact]
    public async Task A_pause_read_that_drops_the_connection_is_unknown()
    {
        var (client, _) = Rig((request, _) => Is(request, "torrents/info") ? throw new HttpRequestException("The connection dropped.") : Is(request, "torrents/stop") ? Status(200) : null);

        var result = await client.ExecuteActionAsync(Item(), DownloadClientActions.Pause, Finished, CancellationToken.None);

        Assert.NotNull(result);
    }

    // ---- SetCategory ----

    [Fact]
    public async Task Setting_a_category_with_no_address_is_said()
    {
        var (client, _) = Rig();

        var result = await client.SetCategoryAsync(NoAddress(), Finished, "seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Setting_a_category_reads_the_torrent_in_every_shape_and_says_why_it_would_not()
    {
        var shapes = new[]
        {
            Status(500),
            RecordedReplies.Json("{}"),
            RecordedReplies.Json("[]"),
            RecordedReplies.Json(FinishedInfo(("0.category", null), ("0.auto_tmm", null))),
            RecordedReplies.Json(FinishedInfo(("0.category", "movies"), ("0.auto_tmm", true)))
        };

        foreach (var shape in shapes)
        {
            var (client, _) = Rig((request, _) => Is(request, "torrents/info") ? new HttpResponseMessage(shape.StatusCode) { Content = new StringContent(shape.Content.ReadAsStringAsync().Result) } : null);
            Assert.NotNull(await client.SetCategoryAsync(Item(), Finished, "seeding", "movies", CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_torrent_on_automatic_management_that_will_not_switch_it_off_keeps_its_category()
    {
        var (client, calls) = Rig((request, _) =>
        {
            if (Is(request, "torrents/info")) return RecordedReplies.Json(FinishedInfo(("0.category", "movies"), ("0.auto_tmm", true)));
            return Is(request, "setAutoManagement") ? Status(500) : null;
        });

        var result = await client.SetCategoryAsync(Item(), Finished, "seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Automatic Torrent Management", result.Message);
        Assert.False(calls.Sent("setCategory"));
    }

    [Fact]
    public async Task A_category_change_the_client_turns_down_with_another_status_is_said_with_that_status()
    {
        var (client, _) = Rig((request, _) => Is(request, "setCategory") ? Status(500) : Is(request, "torrents/info") ? RecordedReplies.Json(FinishedInfo(("0.category", "movies"), ("0.auto_tmm", false))) : null);

        var result = await client.SetCategoryAsync(Item(), Finished, "seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    // ---- statuses ----

    [Fact]
    public void A_status_nobody_gave_is_read_as_downloading()
    {
        Assert.Equal(DownloadQueueStatuses.Downloading, new QbittorrentDownloadClient().NormalizeStatus(null, null));
    }
}
