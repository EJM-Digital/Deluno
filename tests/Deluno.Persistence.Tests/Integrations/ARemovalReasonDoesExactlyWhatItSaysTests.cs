using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Cleanup sheet J1/J2 (#924): removing a download from the Queue asks why,
/// and each of the three plain-word choices does exactly what it says and
/// nothing else.
///
/// <para>Covers both a torrent-shaped client (qBittorrent, which refuses a
/// held copy by infohash — <see cref="DownloadClientActions.DeleteWithData"/>
/// alone is enough to let it back in) and a Usenet-shaped one (SABnzbd, which
/// remembers a refused release in history rather than the queue), because the
/// three reasons ask the client to do different things and both protocol
/// shapes have to come out the same in Deluno's own records regardless.</para>
/// </summary>
public sealed class ARemovalReasonDoesExactlyWhatItSaysTests
{
    private const string Release = "Some.Movie.2024.1080p.WEB-DL-GROUP";
    private const string Indexer = "TestIndexer";
    private const string QueueItemId = "queue-item-1";

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Its_bad_removes_deletes_blocklists_everywhere_and_searches_again(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Failed);

        Assert.True(result.Succeeded);
        Assert.True(result.Blocklisted);
        Assert.True(result.SearchStarted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Equal(QueueItemId, world.Client.LastQueueItemId);

        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.ManualFailure, refused.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(world.EntityId, refused.EntityId);
        // #800: a person chose "It's bad" from the Queue, so this is a manual
        // block, the same origin as blocking a release straight from a search.
        Assert.Equal(BlockedReleaseOrigins.Manual, refused.Origin);
        // #874: "It's bad" refuses the release from every indexer, not only
        // the one it was downloaded from.
        Assert.True(refused.EveryIndexer);
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer(Release), await world.Blocklist.ListKeysAsync(CancellationToken.None));

        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("bad", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocklisted", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search", activity.Message, StringComparison.OrdinalIgnoreCase);
        // #799's last line: the removal, blocklist entry and new search this
        // reason triggers are attributed to a person's own action, not an
        // automatic one. Deluno's convention for that attribution is the
        // message's own wording — "You marked/said/told…" — rather than a
        // separate flag on the activity record itself.
        Assert.StartsWith("You marked", activity.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task I_dont_want_it_removes_and_deletes_but_never_blocklists_or_searches(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Unwanted);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));

        // The caller still gets the title back — the entity id and media type
        // this removal resolved, for a caller (or an older web client build)
        // that wants to confirm the outcome for itself.
        Assert.Equal(world.EntityId, result.EntityId);
        Assert.Equal(world.MediaType, result.MediaType);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("don't want", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blocklist", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #1190: after any removal Deluno makes, the title has no active send, so a new search or hand send
    /// is accepted and never refused as already downloading. One release sent twice shares its hash, so
    /// there are two open records, and both close.
    /// </summary>
    [Theory]
    [InlineData(QueueRemovalReasons.Unwanted)]
    [InlineData(QueueRemovalReasons.Failed)]
    [InlineData(QueueRemovalReasons.Forget)]
    [InlineData(QueueRemovalReasons.Reclaim)]
    [InlineData(QueueRemovalReasons.GaveUp)]
    public async Task A_removal_leaves_the_title_with_no_active_send(string reason)
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        var again = await world.Jobs.RecordDownloadDispatchAsync(
            world.LibraryId, world.MediaType, "movie", world.EntityId, Release, Indexer, world.ClientId, "qbittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await world.Dispatches.RecordDetectionAsync(again, QueueItemId, null, completed: false, CancellationToken.None);
        Assert.NotEmpty(await world.Dispatches.ListDownloadsInFlightAsync(100, CancellationToken.None));

        var result = await world.RemoveAsync(reason);

        Assert.True(result.Succeeded);
        Assert.Empty(await world.Dispatches.ListDownloadsInFlightAsync(100, CancellationToken.None));
        Assert.Empty(await world.Dispatches.FindOpenDispatchesByHashAsync(world.ClientId, QueueItemId, CancellationToken.None));
    }

    /// <summary>The flag-based API call ("remove from client", no blocklist) closes the send the same way.</summary>
    [Fact]
    public async Task A_removal_by_flags_leaves_the_title_with_no_active_send()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        Assert.NotEmpty(await world.Dispatches.ListDownloadsInFlightAsync(100, CancellationToken.None));

        var result = await world.RemoveAsync(removeFromClient: true, blocklist: false, skipRedownload: true);

        Assert.True(result.Succeeded);
        Assert.Empty(await world.Dispatches.ListDownloadsInFlightAsync(100, CancellationToken.None));
    }

    /// <summary>
    /// #924: "I don't want it" does the unmonitor itself now, server-side, so
    /// an API caller that never makes the web client's old separate call
    /// gets the same outcome — and Activity says so in the same sentence as
    /// the removal and the deletion, rather than needing a second line.
    /// </summary>
    [Fact]
    public async Task I_dont_want_it_also_unmonitors_the_movie_and_says_so_in_one_line()
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);

        var result = await world.RemoveAsync(QueueRemovalReasons.Unwanted);

        Assert.True(result.Succeeded);
        var movie = await world.Movies!.GetByIdAsync(world.EntityId, CancellationToken.None);
        Assert.NotNull(movie);
        Assert.False(movie!.Monitored);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("don't want", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stopped wanting it", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same as above for TV, and scoped to only the episode that was
    /// downloaded — not its whole series (an automatic per-episode search
    /// dispatches with entity type "episode", the same distinction
    /// <c>SupportsReplacementSearch</c> already makes for the replacement
    /// search).
    /// </summary>
    [Fact]
    public async Task I_dont_want_it_also_unmonitors_only_the_episode_and_says_so_in_one_line()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Episode);

        var result = await world.RemoveAsync(QueueRemovalReasons.Unwanted);

        Assert.True(result.Succeeded);
        var detail = await world.Series!.GetInventoryDetailAsync(world.SeriesId!, CancellationToken.None);
        var episode = Assert.Single(detail!.Episodes);
        Assert.False(episode.Monitored);
        // Unmonitoring one episode never touches the series' own flag.
        var series = await world.Series!.GetByIdAsync(world.SeriesId!, CancellationToken.None);
        Assert.True(series!.Monitored);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("don't want", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stopped wanting it", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "It's bad" and "just stop tracking it" never unmonitor — only "I don't
    /// want it" means that.
    /// </summary>
    [Theory]
    [InlineData(QueueRemovalReasons.Failed)]
    [InlineData(QueueRemovalReasons.Forget)]
    public async Task Every_other_reason_leaves_the_movie_monitored(string reason)
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);

        await world.RemoveAsync(reason);

        var movie = await world.Movies!.GetByIdAsync(world.EntityId, CancellationToken.None);
        Assert.True(movie!.Monitored);
    }

    /// <summary>Giving up on a release removes it with its files and judges nothing: the caller blocklists and searches.</summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Giving_up_removes_the_download_with_its_files_and_leaves_blocklisting_to_the_caller(string protocol)
    {
        using var world = await World.CreateAsync(protocol, TitleKind.Movie);

        var result = await world.RemoveAsync(QueueRemovalReasons.GaveUp);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
        Assert.True((await world.Movies!.GetByIdAsync(world.EntityId, CancellationToken.None))!.Monitored);
    }

    /// <summary>"Remove this copy" on an unlinked leftover: out of the client with its files, nothing judged.</summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Remove_this_copy_deletes_it_from_the_client_and_judges_nothing(string protocol)
    {
        using var world = await World.CreateAsync(protocol, TitleKind.Movie);

        var result = await world.RemoveAsync(QueueRemovalReasons.Reclaim);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
        Assert.True((await world.Movies!.GetByIdAsync(world.EntityId, CancellationToken.None))!.Monitored);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("library file was not touched", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Just_stop_tracking_it_forgets_without_touching_the_clients_payload(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Forget);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Equal(DownloadClientActions.Forget, world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("stop tracking", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untouched", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #1022: the API's removeFromClient / blocklist / skipRedownload flags reach
    /// the same removal, each part on its own, with Radarr's meaning: only a
    /// blocklist searches again, and skipRedownload turns that off.
    /// </summary>
    [Theory]
    [InlineData(true, true, false, DownloadClientActions.DeleteWithData, true, true)]
    [InlineData(true, true, true, DownloadClientActions.DeleteWithData, true, false)]
    [InlineData(false, true, false, DownloadClientActions.Forget, true, true)]
    [InlineData(true, false, false, DownloadClientActions.DeleteWithData, false, false)]
    [InlineData(false, false, false, DownloadClientActions.Forget, false, false)]
    public async Task The_flags_do_each_part_on_their_own(
        bool removeFromClient,
        bool blocklist,
        bool skipRedownload,
        string expectedAction,
        bool expectBlocklisted,
        bool expectSearch)
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);

        var result = await world.RemoveAsync(removeFromClient, blocklist, skipRedownload);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedAction, world.Client.LastAction);
        Assert.Equal(expectBlocklisted, result.Blocklisted);
        Assert.Equal(expectBlocklisted, (await world.Blocklist.ListAsync(CancellationToken.None)).Count == 1);
        Assert.Equal(expectSearch, result.SearchStarted);
        Assert.Equal(expectSearch ? 1 : 0, (await world.Jobs.ListAsync(10, CancellationToken.None)).Count);

        // Never unmonitors: only the "I don't want it" choice does.
        Assert.True((await world.Movies!.GetByIdAsync(world.EntityId, CancellationToken.None))!.Monitored);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.StartsWith("An outside program removed", activity.Message, StringComparison.Ordinal);
        Assert.Equal(removeFromClient, activity.Message.Contains("deleted its files", StringComparison.Ordinal));
        Assert.Equal(!removeFromClient, activity.Message.Contains("untouched", StringComparison.Ordinal));
        Assert.Equal(expectBlocklisted, activity.Message.Contains("blocklisted", StringComparison.Ordinal));
        Assert.Equal(expectSearch, activity.Message.Contains("started a new search", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1062/#933: a download Deluno never sent is refused by every removal
    /// path (the Queue's reasons, the API's flags, the raw action) and the
    /// client is never asked to do anything.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task A_download_Deluno_did_not_send_is_refused_by_every_removal_path_and_the_client_is_never_called(string protocol)
    {
        using var world = await World.CreateAsync(protocol, TitleKind.Movie);
        const string foreign = "hand-added-hash";

        var byReason = await world.RemoveForeignAsync(foreign, QueueRemovalReasons.Failed);
        var byFlags = await world.RemoveForeignAsync(foreign, true, true, false);
        var byAction = await world.ActForeignAsync(foreign, DownloadClientActions.DeleteWithData);

        foreach (var result in new[] { byReason, byFlags })
        {
            Assert.False(result.Succeeded);
            Assert.True(result.Refused);
            Assert.Contains("Deluno did not send", result.Message);
            Assert.Contains(foreign, result.Message);
        }

        Assert.False(byAction.Succeeded);
        Assert.True(byAction.Refused);
        Assert.Null(world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task The_shared_ownership_check_says_yes_for_a_dispatch_and_no_for_anything_else()
    {
        using var world = await World.CreateAsync("qbittorrent");

        Assert.True((await world.CheckOwnershipAsync(QueueItemId)).Owned);
        Assert.False((await world.CheckOwnershipAsync("someone-elses")).Owned);
    }

    /// <summary>#1062: Deluno archives a dispatch as it finishes with it; the download can still be in the client and is still Deluno's.</summary>
    [Fact]
    public async Task A_download_whose_dispatch_is_archived_is_still_Delunos_own_and_can_be_cleaned_up()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        await world.Dispatches.ArchiveDispatchAsync(world.DispatchId, "finished", CancellationToken.None);

        Assert.True((await world.CheckOwnershipAsync(QueueItemId)).Owned);
        var forgotten = await world.ActAsync(QueueItemId, DownloadClientActions.Forget);

        Assert.True(forgotten.Succeeded);
        Assert.False(forgotten.Refused);
        Assert.Equal(DownloadClientActions.Forget, world.Client.LastAction);
        Assert.False((await world.CheckOwnershipAsync("someone-elses")).Owned);
    }

    /// <summary>
    /// #1062 (review finding): the public queue-actions body cannot name a
    /// dispatch at all, so a caller can never make an item Deluno's by saying
    /// so. Ownership comes only from Deluno's own records.
    /// </summary>
    [Fact]
    public void The_public_action_request_has_no_way_to_name_a_dispatch()
    {
        Assert.Null(typeof(DownloadClientActionRequest).GetProperty("DispatchId"));
        Assert.Equal(2, typeof(DownloadClientActionRequest).GetConstructors().Single().GetParameters().Length);
    }

    /// <summary>#1062 (James, 30 Sep 2026): a dispatch with no id is never matched to a client item by release name, even when the client shows an item under exactly that name.</summary>
    [Fact]
    public async Task A_dispatch_the_client_never_gave_an_id_for_is_not_matched_to_a_same_named_item()
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);
        var idLessDispatch = await world.RecordDispatchWithoutIdAsync("Other.Movie.2024.1080p.WEB-DL-GROUP");
        world.Client.Queue.Add(ClientItem("id-the-client-gave-later", "Other.Movie.2024.1080p.WEB-DL-GROUP"));

        var own = await world.ActForDispatchAsync("id-the-client-gave-later", DownloadClientActions.Forget, idLessDispatch);

        Assert.True(own.Refused);
        Assert.Null(world.Client.LastAction);
        // ...and nothing was adopted: the item is still not Deluno's.
        Assert.False((await world.CheckOwnershipAsync("id-the-client-gave-later")).Owned);
    }

    [Fact]
    public async Task A_dispatch_is_never_proof_for_an_item_it_does_not_describe()
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);
        var idLessDispatch = await world.RecordDispatchWithoutIdAsync("Other.Movie.2024.1080p.WEB-DL-GROUP");
        world.Client.Queue.Add(ClientItem("hand-added", "Something.Else.Entirely.2020.720p"));

        // Names a hand-added item (different release name): refused, and nothing called.
        var byName = await world.ActForDispatchAsync("hand-added", DownloadClientActions.Forget, idLessDispatch);
        Assert.True(byName.Refused);
        Assert.Null(world.Client.LastAction);

        // The world's own dispatch has its own id; naming another item with it: refused.
        var byId = await world.ActForDispatchAsync("hand-added", DownloadClientActions.DeleteWithData, world.DispatchId);
        Assert.True(byId.Refused);
        Assert.Null(world.Client.LastAction);

        // An item that is not in the client at all cannot be proven either.
        var missing = await world.ActForDispatchAsync("not-there", DownloadClientActions.Forget, idLessDispatch);
        Assert.True(missing.Refused);
        Assert.False((await world.CheckOwnershipAsync("hand-added")).Owned);
    }

    private static DownloadQueueItem ClientItem(string id, string releaseName)
        => new(
            Id: id, ClientId: "client", ClientName: "client", Protocol: "sabnzbd", MediaType: "movie",
            Title: releaseName, ReleaseName: releaseName, Category: "movies", Status: "downloading",
            Progress: 0.5, SpeedMbps: 1, EtaSeconds: 60, SizeBytes: 1000, DownloadedBytes: 500, Peers: 0,
            IndexerName: "TestIndexer", ErrorMessage: null, AddedUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Delunos_own_download_deletes_its_data_only_when_removeFromClient_is_set()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);

        var kept = await world.RemoveAsync(false, false, false);
        Assert.True(kept.Succeeded);
        Assert.Equal(DownloadClientActions.Forget, world.Client.LastAction);

        var deleted = await world.RemoveAsync(true, false, false);
        Assert.True(deleted.Succeeded);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
    }

    [Fact]
    public async Task An_unrecognised_reason_is_refused_rather_than_guessed()
    {
        using var world = await World.CreateAsync("qbittorrent");

        var result = await world.RemoveAsync("obliterate");

        Assert.False(result.Succeeded);
        Assert.Null(world.Client.LastAction);
    }

    // Golden path, rc.30.9: a download Deluno removed itself kept its record open, so the title read "on its way".
    [Theory]
    [InlineData(QueueRemovalReasons.Failed)]
    [InlineData(QueueRemovalReasons.Unwanted)]
    [InlineData(QueueRemovalReasons.GaveUp)]
    public async Task A_download_Deluno_removes_closes_its_record_so_the_title_is_free(string reason)
    {
        using var world = await World.CreateAsync("qbittorrent");

        var result = await world.RemoveAsync(reason);

        Assert.True(result.Succeeded);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        var dispatch = await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("archived", dispatch!.Status);
    }

    [Fact]
    public async Task A_removal_the_client_cannot_do_because_it_no_longer_holds_the_download_still_frees_the_title()
    {
        using var world = await World.CreateAsync("qbittorrent");
        world.Client.FailActions = true;

        var result = await world.RemoveAsync(QueueRemovalReasons.GaveUp);

        Assert.True(result.Succeeded, result.Message);
        var dispatch = await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("archived", dispatch!.Status);
    }

    [Fact]
    public async Task A_removal_the_client_cannot_do_while_it_still_holds_the_download_keeps_the_record_open()
    {
        using var world = await World.CreateAsync("qbittorrent");
        world.Client.FailActions = true;
        world.Client.Queue.Add(ClientItem(QueueItemId, Release));

        var result = await world.RemoveAsync(QueueRemovalReasons.GaveUp);

        Assert.False(result.Succeeded);
        var dispatch = await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None);
        Assert.NotEqual("archived", dispatch!.Status);
    }

    /// <summary>#1212: a client that removed the download but left its files says so; Activity never claims they were deleted.</summary>
    [Fact]
    public async Task A_removal_that_left_the_files_on_disk_says_so_instead_of_claiming_they_were_deleted()
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);
        world.Client.FilesNote = "Deluno could not delete the finished folder: it is in use.";

        var result = await world.RemoveAsync(QueueRemovalReasons.Unwanted);

        Assert.True(result.Succeeded);
        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.DoesNotContain("deleted its files", activity.Message, StringComparison.Ordinal);
        Assert.Contains("left its files on disk", activity.Message, StringComparison.Ordinal);
        Assert.Contains("it is in use", activity.Message, StringComparison.Ordinal);
    }

    /// <summary>#1212 (b): the second removal of the same download did nothing and still read Done, with a second Activity row.</summary>
    [Fact]
    public async Task A_second_removal_of_a_download_the_client_no_longer_has_fails_and_writes_no_second_done_row()
    {
        using var world = await World.CreateAsync("sabnzbd", TitleKind.Movie);
        var first = await world.RemoveAsync(QueueRemovalReasons.Reclaim);
        Assert.True(first.Succeeded);

        world.Client.FailActions = true;
        var second = await world.RemoveAsync(QueueRemovalReasons.Reclaim);

        Assert.False(second.Succeeded);
        var rows = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Where(entry => entry.Category == "download.queue.removed");
        Assert.Single(rows);
    }

    /// <summary>
    /// #1216: Retryfilm's download was removed by Deluno, and its hand-off, put back to waiting by a retry, came back
    /// forty minutes later as "never picked up". Removing the download closes the hand-off in whatever state it is in.
    /// </summary>
    [Theory]
    [InlineData(ProcessorHandoffStatuses.Waiting)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.Started)]
    public async Task Removing_a_download_closes_its_open_or_failed_hand_off_at_once(string status)
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        var handoff = await world.HandoffAsync(status);

        var result = await world.RemoveAsync(QueueRemovalReasons.Failed);

        Assert.True(result.Succeeded);
        var stored = await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));
        Assert.False(ProcessorHandoffStatuses.IsWithProcessor(stored.Status));
        var activity = await world.Activity.ListActivityAsync(20, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "processing.dismissed" && entry.Message.Contains("because Deluno removed the download", StringComparison.Ordinal));
        Assert.DoesNotContain(activity, entry => entry.Category == "processing.waiting-too-long");
    }

    [Fact]
    public async Task A_hand_off_that_already_finished_is_left_as_it_was_when_its_download_is_removed()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        var handoff = await world.HandoffAsync(ProcessorHandoffStatuses.Completed);

        await world.RemoveAsync(QueueRemovalReasons.GaveUp);

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? LastAction { get; set; }

        public string? LastQueueItemId { get; private set; }

        /// <summary>The client refuses every action (a hung or half-gone download).</summary>
        public bool FailActions { get; set; }

        /// <summary>The client removed the download but could not remove its files, and says why (#1212).</summary>
        public string? FilesNote { get; set; }

        public DownloadClientTelemetryCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <summary>What the client is showing right now.</summary>
        public List<DownloadQueueItem> Queue { get; } = [];

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, protocol, null, "healthy", null, Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                Queue.ToArray(), [], capturedUtc));

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            LastAction = action;
            LastQueueItemId = queueItemId;
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, !FailActions, FailActions ? "The client would not do that." : $"{action} sent to {client.Name}")
            {
                FilesNote = FilesNote
            });
        }

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class PassThroughResiliencePolicy : IIntegrationResiliencePolicy
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

    /// <summary>Which real catalog title (if any) the world wires up behind the dispatch's entity id, so #924's unmonitor step has something real to flip.</summary>
    private enum TitleKind
    {
        /// <summary>No catalog wired up — the entity id is synthetic, as it was before #924. Unmonitoring silently finds nothing.</summary>
        None,
        Movie,
        Series,
        Episode
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;

        private World(
            TestStorage storage,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            FakeClient fakeClient,
            string entityId,
            string mediaType,
            SqliteJobStore jobs,
            SqliteBlockedReleaseRepository blocklist,
            SqliteMovieCatalogRepository? movies,
            SqliteSeriesCatalogRepository? series,
            string? seriesId,
            SqliteDownloadDispatchesRepository dispatches,
            string dispatchId,
            string libraryId,
            SqliteProcessorRepository processors)
        {
            Processors = processors;
            Dispatches = dispatches;
            DispatchId = dispatchId;
            LibraryId = libraryId;
            _storage = storage;
            _service = service;
            Client = fakeClient;
            ClientId = client.Id;
            EntityId = entityId;
            MediaType = mediaType;
            Jobs = jobs;
            Activity = jobs;
            Blocklist = blocklist;
            Movies = movies;
            Series = series;
            SeriesId = seriesId;
        }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteProcessorRepository Processors { get; }

        public string DispatchId { get; }

        public string LibraryId { get; }

        public FakeClient Client { get; }

        public string ClientId { get; }

        public string EntityId { get; }

        public string MediaType { get; }

        public SqliteJobStore Jobs { get; }

        public IActivityFeedRepository Activity { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        /// <summary>Set only for <see cref="TitleKind.Movie"/>.</summary>
        public SqliteMovieCatalogRepository? Movies { get; }

        /// <summary>Set for <see cref="TitleKind.Series"/> and <see cref="TitleKind.Episode"/>.</summary>
        public SqliteSeriesCatalogRepository? Series { get; }

        /// <summary>The parent series id, when <see cref="EntityId"/> names an episode instead.</summary>
        public string? SeriesId { get; }

        public static Task<World> CreateAsync(string protocol) => CreateAsync(protocol, TitleKind.None);

        public static async Task<World> CreateAsync(string protocol, TitleKind titleKind)
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-27T09:00:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var mediaType = protocol == "sabnzbd" ? "movie" : "tv";
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Library", mediaType, "library", Path.Combine(storage.DataRoot, "media"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                protocol, protocol, "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var entityType = mediaType == "tv" ? "series" : "movie";

            SqliteMovieCatalogRepository? movies = null;
            SqliteSeriesCatalogRepository? series = null;
            string? seriesId = null;
            string entityId;
            IDispatchTitleUnmonitorComponent? unmonitorComponent = null;

            switch (titleKind)
            {
                case TitleKind.Movie:
                    await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
                    movies = new SqliteMovieCatalogRepository(storage.Factory, time);
                    var addedMovie = await movies.AddAsync(new CreateMovieRequest(Release, 2024, null), CancellationToken.None);
                    entityType = "movie";
                    entityId = addedMovie.Id;
                    unmonitorComponent = new MovieDispatchTitleUnmonitorService(movies);
                    break;

                case TitleKind.Series:
                    await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
                    series = new SqliteSeriesCatalogRepository(storage.Factory, time);
                    var addedSeries = await series.AddAsync(new CreateSeriesRequest(Release, 2024, null), CancellationToken.None);
                    entityType = "series";
                    entityId = addedSeries.Id;
                    unmonitorComponent = new SeriesDispatchTitleUnmonitorService(series);
                    break;

                case TitleKind.Episode:
                    await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
                    series = new SqliteSeriesCatalogRepository(storage.Factory, time);
                    var parentSeries = await series.AddAsync(new CreateSeriesRequest(Release, 2024, null), CancellationToken.None);
                    await series.SyncEpisodeCatalogueAsync(
                        parentSeries.Id,
                        [new CatalogueEpisodeItem(1, 1, "Pilot", null, time.GetUtcNow())],
                        "provider",
                        CancellationToken.None);
                    var detail = await series.GetInventoryDetailAsync(parentSeries.Id, CancellationToken.None);
                    entityType = "episode";
                    entityId = detail!.Episodes.Single().EpisodeId;
                    seriesId = parentSeries.Id;
                    unmonitorComponent = new SeriesDispatchTitleUnmonitorService(series);
                    break;

                default:
                    entityId = $"{entityType}-1";
                    break;
            }

            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, mediaType, entityType, entityId, Release, Indexer, client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, QueueItemId, null, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var fakeClient = new FakeClient(protocol);
            var registry = new DownloadClientRegistry([fakeClient]);
            var titleUnmonitorService = unmonitorComponent is null
                ? null
                : new CompositeDispatchTitleUnmonitorService([unmonitorComponent]);
            var service = new DownloadClientTelemetryService(
                settings,
                new SqliteDownloadHealthRepository(storage.Factory, time),
                libraries,
                connections,
                null!,
                registry,
                time,
                new PassThroughResiliencePolicy(),
                jobs,
                dispatches,
                jobs,
                new Deluno.Recovery.Policies.DownloadHealthEvaluator(),
                processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance,
                blocklist,
                titleUnmonitorService: titleUnmonitorService);

            return new World(storage, service, client, fakeClient, entityId, mediaType, jobs, blocklist, movies, series, seriesId, dispatches, dispatchId, library.Id, processors);
        }

        public async Task<ProcessorHandoffItem> HandoffAsync(string status)
        {
            var handoff = await Processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest(LibraryId, MediaType, ClientId, QueueItemId, Release, Path.Combine(Path.GetTempPath(), Release), "Weir"),
                CancellationToken.None);
            await Processors.UpdateProcessorHandoffAsync(
                handoff.Id, status, null, null,
                status == ProcessorHandoffStatuses.Failed ? "Deluno could not submit the hand-off." : null,
                CancellationToken.None);
            return (await Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        }

        public Task<QueueRemovalResult> RemoveAsync(string reason)
            => _service.RemoveWithReasonAsync(ClientId, QueueItemId, reason, CancellationToken.None);

        public Task<QueueRemovalResult> RemoveAsync(bool removeFromClient, bool blocklist, bool skipRedownload)
            => _service.RemoveWithOptionsAsync(ClientId, QueueItemId, removeFromClient, blocklist, skipRedownload, CancellationToken.None);

        public Task<QueueRemovalResult> RemoveForeignAsync(string queueItemId, string reason)
            => _service.RemoveWithReasonAsync(ClientId, queueItemId, reason, CancellationToken.None);

        public Task<QueueRemovalResult> RemoveForeignAsync(string queueItemId, bool removeFromClient, bool blocklist, bool skipRedownload)
            => _service.RemoveWithOptionsAsync(ClientId, queueItemId, removeFromClient, blocklist, skipRedownload, CancellationToken.None);

        public Task<DownloadClientActionResult> ActForeignAsync(string queueItemId, string action)
            => _service.ExecuteActionAsync(ClientId, new DownloadClientActionRequest(action, queueItemId), CancellationToken.None);

        public Task<DownloadClientActionResult> ActAsync(string queueItemId, string action)
            => _service.ExecuteActionAsync(ClientId, new DownloadClientActionRequest(action, queueItemId), CancellationToken.None);

        public async Task<DownloadClientActionResult> ActForDispatchAsync(string queueItemId, string action, string dispatchId)
        {
            var dispatch = (await Dispatches.GetDispatchIncludingArchivedAsync(dispatchId, CancellationToken.None))!;
            return await _service.ExecuteActionForDispatchAsync(ClientId, new DownloadClientActionRequest(action, queueItemId), dispatch, CancellationToken.None);
        }

        /// <summary>A dispatch the client never handed an id back for.</summary>
        public Task<string> RecordDispatchWithoutIdAsync(string release)
            => Jobs.RecordDownloadDispatchAsync(
                LibraryId, MediaType, MediaType == "tv" ? "series" : "movie", EntityId, release, Indexer, ClientId, ClientId, "sent", null,
                cancellationToken: CancellationToken.None);

        public Task<DownloadOwnership> CheckOwnershipAsync(string queueItemId)
            => _service.CheckOwnershipAsync(ClientId, queueItemId, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
