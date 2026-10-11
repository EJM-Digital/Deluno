using System.Net;
using System.Text;
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
using Deluno.Tests.Recorded;

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
    public async Task Just_stop_tracking_it_forgets_without_calling_the_client_at_all(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Forget);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Null(world.Client.LastAction);
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
    [InlineData(false, true, false, null, true, true)]
    [InlineData(true, false, false, DownloadClientActions.DeleteWithData, false, false)]
    [InlineData(false, false, false, null, false, false)]
    public async Task The_flags_do_each_part_on_their_own(
        bool removeFromClient,
        bool blocklist,
        bool skipRedownload,
        string? expectedAction,
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
        var forgotten = await world.ActAsync(QueueItemId, DownloadClientActions.RemoveRefusedCopy);

        Assert.True(forgotten.Succeeded);
        Assert.False(forgotten.Refused);
        Assert.Equal(DownloadClientActions.RemoveRefusedCopy, world.Client.LastAction);
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

        var own = await world.ActForDispatchAsync("id-the-client-gave-later", DownloadClientActions.RemoveRefusedCopy, idLessDispatch);

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
        var byName = await world.ActForDispatchAsync("hand-added", DownloadClientActions.RemoveRefusedCopy, idLessDispatch);
        Assert.True(byName.Refused);
        Assert.Null(world.Client.LastAction);

        // The world's own dispatch has its own id; naming another item with it: refused.
        var byId = await world.ActForDispatchAsync("hand-added", DownloadClientActions.DeleteWithData, world.DispatchId);
        Assert.True(byId.Refused);
        Assert.Null(world.Client.LastAction);

        // An item that is not in the client at all cannot be proven either.
        var missing = await world.ActForDispatchAsync("not-there", DownloadClientActions.RemoveRefusedCopy, idLessDispatch);
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
        Assert.Null(world.Client.LastAction);

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

    /// <summary>#1220: a hand-off closed because a person stopped tracking says so, not that the download was gone.</summary>
    [Fact]
    public async Task A_hand_off_closed_because_a_person_stopped_tracking_the_download_says_so()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        var handoff = await world.HandoffAsync(ProcessorHandoffStatuses.Failed);

        await world.RemoveAsync(QueueRemovalReasons.Forget);

        var stored = await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored!.Status);
        Assert.Contains("stop tracking", stored.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("was gone", stored.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hand_off_that_already_finished_is_left_as_it_was_when_its_download_is_removed()
    {
        using var world = await World.CreateAsync("qbittorrent", TitleKind.Movie);
        var handoff = await world.HandoffAsync(ProcessorHandoffStatuses.Completed);

        await world.RemoveAsync(QueueRemovalReasons.GaveUp);

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
    }

    // ------------------------------------------------------------------ #1241: stop tracking is Deluno-only

    public static TheoryData<string, bool> EveryClientAndEveryWayToStopTracking()
    {
        var data = new TheoryData<string, bool>();
        foreach (var protocol in new[] { "qbittorrent", "deluge", "transmission", "rtorrent", "utorrent", "sabnzbd", "nzbget" })
        {
            data.Add(protocol, true);
            data.Add(protocol, false);
        }

        return data;
    }

    /// <summary>
    /// #1241: "Just stop tracking it" promises the client keeps the download and its files. Every way in
    /// (the Queue dialog's reason, and the API's removeFromClient=false) must send the client nothing at
    /// all, on every client: a remove call, even one that keeps files, is not "untouched".
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryClientAndEveryWayToStopTracking))]
    public async Task Stopping_tracking_sends_no_remove_or_delete_to_any_client(string protocol, bool throughTheDialogsReason)
    {
        var (world, traffic) = await World.CreateWithRealClientAsync(protocol);
        using var _ = world;

        var result = throughTheDialogsReason
            ? await world.RemoveAsync(QueueRemovalReasons.Forget)
            : await world.RemoveAsync(removeFromClient: false, blocklist: false, skipRedownload: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(traffic.Requests);
    }

    /// <summary>The same promise when the API also blocklists: the release is blocklisted, the client is still not touched.</summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("deluge")]
    [InlineData("transmission")]
    [InlineData("rtorrent")]
    [InlineData("utorrent")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task Stopping_tracking_while_blocklisting_still_leaves_the_client_alone(string protocol)
    {
        var (world, traffic) = await World.CreateWithRealClientAsync(protocol);
        using var _ = world;

        var result = await world.RemoveAsync(removeFromClient: false, blocklist: true, skipRedownload: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Blocklisted);
        Assert.Empty(traffic.Requests);
    }

    public static TheoryData<string, string> EveryClientAndEveryRemovalReason()
    {
        var data = new TheoryData<string, string>();
        foreach (var protocol in new[] { "qbittorrent", "deluge", "transmission", "rtorrent", "utorrent", "sabnzbd", "nzbget" })
        {
            foreach (var reason in new[]
                     {
                         QueueRemovalReasons.Failed, QueueRemovalReasons.Unwanted, QueueRemovalReasons.Forget,
                         QueueRemovalReasons.Reclaim, QueueRemovalReasons.GaveUp
                     })
            {
                data.Add(protocol, reason);
            }
        }

        return data;
    }

    /// <summary>
    /// #1241: every Activity sentence about files matches what was sent to the client. A sentence that says
    /// the files were deleted needs a request that removes them; a sentence that says the client still has
    /// it, untouched, needs no request at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryClientAndEveryRemovalReason))]
    public async Task Each_removal_reasons_sentence_matches_the_call_made_to_the_client(string protocol, string reason)
    {
        var (world, traffic) = await World.CreateWithRealClientAsync(protocol);
        using var _ = world;

        var result = await world.RemoveAsync(reason);

        Assert.True(result.Succeeded, result.Message);
        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");

        var claimsUntouched = activity.Message.Contains("untouched", StringComparison.OrdinalIgnoreCase);
        if (reason == QueueRemovalReasons.Forget)
        {
            Assert.True(claimsUntouched, activity.Message);
            Assert.Empty(traffic.Requests);
        }
        else
        {
            Assert.False(claimsUntouched, activity.Message);
            Assert.NotEmpty(traffic.Requests);
        }
    }

    /// <summary>
    /// #1241: the old "forget" verb meant removal with data on a torrent client, so it is no longer a verb
    /// the gateway passes on. A caller that still sends it is refused and the client is not touched.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task The_old_forget_verb_is_refused_and_sends_nothing(string protocol)
    {
        var (world, traffic) = await World.CreateWithRealClientAsync(protocol);
        using var _ = world;

        var result = await world.ActAsync(QueueItemId, "forget");

        Assert.False(result.Succeeded);
        Assert.Empty(traffic.Requests);
    }

    /// <summary>Hands every adapter the one handler that writes down what it is asked (sign-in traffic aside) and answers shaped like the client would.</summary>
    private sealed class TrafficFactory(ClientTraffic traffic) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(traffic, disposeHandler: false);
    }

    private sealed class ClientTraffic : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            var isSignIn = path.Contains("auth/login", StringComparison.OrdinalIgnoreCase)
                || body.Contains("auth.login", StringComparison.OrdinalIgnoreCase)
                || path.Contains("token.html", StringComparison.OrdinalIgnoreCase);
            var isList = (path.Contains("mode=queue", StringComparison.OrdinalIgnoreCase) || path.Contains("mode=history", StringComparison.OrdinalIgnoreCase))
                    && !path.Contains("name=", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"listgroups\"", StringComparison.Ordinal)
                || body.Contains("\"method\":\"history\"", StringComparison.OrdinalIgnoreCase);
            // A torrent client's list of torrents is read before and after a removal (#1252): the torrent is there until a
            // call that removes it went out.
            var isTorrentList = path.Contains("api/v2/torrents/info", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"torrent-get\"", StringComparison.Ordinal)
                || body.Contains("web.update_ui", StringComparison.Ordinal)
                || path.Contains("list=1", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<methodName>d.name</methodName>", StringComparison.Ordinal);
            if (!isSignIn && !isList && !isTorrentList)
            {
                Requests.Add($"{path} {body}".Trim());
            }

            if (isTorrentList)
            {
                var held = Requests.Count == 0;
                if (path.Contains("api/v2/torrents/info", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(held
                        ? RecordedReplies.Qbittorrent("finished-folder").Body("torrents/info")
                        : RecordedReplies.Qbittorrent("removed-twice").Body("torrents/info"));
                }

                if (body.Contains("\"torrent-get\"", StringComparison.Ordinal))
                {
                    return Json(held
                        ? RecordedReplies.Transmission("finished-folder").Body("\"torrent-get\"", ("arguments.torrents.0.downloadDir", "/no/such/folder"))
                        : RecordedReplies.Transmission("removed-twice").Body("\"torrent-get\""));
                }

                if (body.Contains("web.update_ui", StringComparison.Ordinal))
                {
                    return Json(held
                        ? HeldDelugeTorrent()
                        : RecordedReplies.Deluge("removed-twice").Body("web.update_ui"));
                }

                if (body.Contains("<methodName>d.name</methodName>", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            held
                                ? "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>Some.Movie</string></value></param></params></methodResponse>"
                                : "<?xml version=\"1.0\"?><methodResponse><fault><value><struct><member><name>faultCode</name><value><i4>-501</i4></value></member><member><name>faultString</name><value><string>Could not find info-hash.</string></value></member></struct></value></fault></methodResponse>",
                            Encoding.UTF8, "text/xml")
                    };
                }

                return Json(held ? "{\"torrents\":[[\"" + QueueItemId + "\",136,\"Some.Movie\"]]}" : "{\"torrents\":[]}");
            }

            if (path.Contains("token.html", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='token'>test-token</div>", Encoding.UTF8, "text/html") };
            }

            if (path.Contains("api/v2/", StringComparison.OrdinalIgnoreCase))
            {
                return path.Contains("auth/login", StringComparison.OrdinalIgnoreCase)
                    ? RecordedReplies.Qbittorrent("sign-in").Response("auth/login")
                    : RecordedReplies.Qbittorrent("removed-with-data").Response("torrents/delete");
            }

            if (body.Contains("<methodCall>", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\"?><methodResponse><params><param><value><i4>0</i4></value></param></params></methodResponse>",
                        Encoding.UTF8, "text/xml")
                };
            }

            // A Usenet client's list of jobs is read back to check a removal happened; once a delete went out it is empty.
            var removed = Requests.Count > 0;
            if (path.Contains("mode=queue", StringComparison.OrdinalIgnoreCase) && isList)
            {
                // SABnzbd's own queue reply (recorded, #1237): with a job in it until the delete went out, then empty.
                return Json(removed
                    ? RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()))
                    : RecordedReplies.Sabnzbd("downloading").Body("mode=queue&output", ("^queue.slots", 1), ("queue.slots.0.nzo_id", "queue-item-1")));
            }

            if (path.Contains("mode=history", StringComparison.OrdinalIgnoreCase) && isList)
            {
                return Json(RecordedReplies.Sabnzbd("removed-with-data").Body("mode=history&output", ("history.slots", new System.Text.Json.Nodes.JsonArray())));
            }

            if (body.Contains("\"method\":\"history\"", StringComparison.OrdinalIgnoreCase))
            {
                return Json(RecordedReplies.Nzbget("removed-with-data").Body("\"history\"", ("result", new System.Text.Json.Nodes.JsonArray())));
            }

            if (body.Contains("\"listgroups\"", StringComparison.Ordinal))
            {
                return Json(removed
                    ? RecordedReplies.Nzbget("removed-without-data").Body("listgroups", ("result", new System.Text.Json.Nodes.JsonArray()))
                    : RecordedReplies.Nzbget("queued").Body("listgroups", ("^result", 1), ("result.0.NZBID", 42)));
            }

            return RecordedReplies.Json("{\"status\":true,\"result\":true}");
        }

        /// <summary>Deluge's recorded list of a finished torrent, filed under this test's own torrent id.</summary>
        private static string HeldDelugeTorrent()
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(RecordedReplies.Deluge("finished-folder").Body("web.update_ui"))!;
            var torrents = root["result"]!["torrents"]!.AsObject();
            var first = torrents.First();
            torrents.Remove(first.Key);
            torrents[QueueItemId] = first.Value;
            return root.ToJsonString();
        }

        private static HttpResponseMessage Json(string content)
            => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
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

        /// <summary>The id the client holds the dispatched download under.</summary>
        public string ItemId { get; private init; } = QueueItemId;

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

        /// <summary>The real adapter for <paramref name="protocol"/>, talking to a handler that writes down every request it is sent.</summary>
        public static async Task<(World World, ClientTraffic Traffic)> CreateWithRealClientAsync(string protocol, TitleKind titleKind = TitleKind.Movie)
        {
            var traffic = new ClientTraffic();
            IDownloadClient real = protocol switch
            {
                "qbittorrent" => new Deluno.Integrations.DownloadClients.Clients.QbittorrentDownloadClient(() => traffic),
                "deluge" => new Deluno.Integrations.DownloadClients.Clients.DelugeDownloadClient(new TrafficFactory(traffic)),
                "transmission" => new Deluno.Integrations.DownloadClients.Clients.TransmissionDownloadClient(new TrafficFactory(traffic)),
                "utorrent" => new Deluno.Integrations.DownloadClients.Clients.UTorrentDownloadClient(() => traffic),
                "rtorrent" => new Deluno.Integrations.DownloadClients.Clients.RtorrentDownloadClient(new TrafficFactory(traffic)),
                "sabnzbd" => new Deluno.Integrations.DownloadClients.Clients.SabnzbdDownloadClient(new TrafficFactory(traffic)),
                "nzbget" => new Deluno.Integrations.DownloadClients.Clients.NzbGetDownloadClient(new TrafficFactory(traffic)),
                _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
            };
            return (await CreateAsync(protocol, titleKind, real), traffic);
        }

        public static Task<World> CreateAsync(string protocol, TitleKind titleKind) => CreateAsync(protocol, titleKind, null);

        private static async Task<World> CreateAsync(string protocol, TitleKind titleKind, IDownloadClient? realClient)
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
                protocol, protocol, "localhost", 8080, realClient is null ? null : "user", realClient is null ? null : "secret", null, "movies", "tv", null, 1, true), CancellationToken.None);
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
            // Transmission and NZBGet number their downloads, so a real adapter for either is given a number.
            var itemId = realClient is null ? QueueItemId : protocol switch { "nzbget" => "42", "transmission" => "7", _ => QueueItemId };
            await dispatches.RecordDetectionAsync(dispatchId, itemId, null, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var fakeClient = new FakeClient(protocol);
            var registry = new DownloadClientRegistry([realClient ?? fakeClient]);
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

            return new World(storage, service, client, fakeClient, entityId, mediaType, jobs, blocklist, movies, series, seriesId, dispatches, dispatchId, library.Id, processors) { ItemId = itemId };
        }

        public async Task<ProcessorHandoffItem> HandoffAsync(string status)
        {
            var handoff = await Processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest(LibraryId, MediaType, ClientId, ItemId, Release, Path.Combine(Path.GetTempPath(), Release), "Weir"),
                CancellationToken.None);
            await Processors.UpdateProcessorHandoffAsync(
                handoff.Id, status, null, null,
                status == ProcessorHandoffStatuses.Failed ? "Deluno could not submit the hand-off." : null,
                CancellationToken.None);
            return (await Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        }

        public Task<QueueRemovalResult> RemoveAsync(string reason)
            => _service.RemoveWithReasonAsync(ClientId, ItemId, reason, CancellationToken.None);

        public Task<QueueRemovalResult> RemoveAsync(bool removeFromClient, bool blocklist, bool skipRedownload)
            => _service.RemoveWithOptionsAsync(ClientId, ItemId, removeFromClient, blocklist, skipRedownload, CancellationToken.None);

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
