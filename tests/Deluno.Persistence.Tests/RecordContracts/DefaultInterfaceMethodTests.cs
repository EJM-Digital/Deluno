using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Hardening;
using Deluno.Series.Data;

namespace Deluno.Persistence.Tests.RecordContracts;

/// <summary>
/// #1268: the default bodies that interfaces carry so a test double need not write them. Each is run
/// on a stand-in that answers only the abstract members, so what the default does by itself, and what
/// it asks of the member it forwards to, is what is asserted.
/// </summary>
public sealed class DefaultInterfaceMethodTests
{
    private static readonly CancellationToken Token = new CancellationTokenSource().Token;

    private static readonly ActivityEventItem Event =
        new("a1", "cat", "message", null, null, "movie", "m1", DateTimeOffset.UnixEpoch);

    private static readonly DownloadClientItem Client = new(
        "c1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "key", "http://localhost:8080/", false, null,
        "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_activity_page_by_query_asks_for_the_query_entity()
    {
        var page = Page<ActivityEventItem>.Of([Event], null);
        var feed = InterfaceStub.Create<IActivityFeedRepository>((name, args) =>
        {
            Assert.Equal("ListActivityPageAsync", name);
            Assert.Equal("movie", args[1]);
            Assert.Equal("m1", args[2]);
            return Task.FromResult(page);
        });

        var result = await feed.ListActivityPageAsync(new PageRequest(), new ActivityQuery(RelatedEntityType: "movie", RelatedEntityId: "m1"), Token);

        Assert.Same(page, result);
    }

    [Fact]
    public async Task Recording_on_change_records_every_time_by_default()
    {
        var feed = InterfaceStub.Create<IActivityFeedRepository>((name, args) =>
        {
            Assert.Equal("RecordActivityAsync", name);
            Assert.Equal("cat", args[0]);
            Assert.Equal("message", args[1]);
            Assert.Equal("m1", args[5]);
            return Task.FromResult(Event);
        });

        var recorded = await feed.RecordActivityOnChangeAsync("cat", "message", null, null, "movie", "m1", Token);

        Assert.Same(Event, recorded);
    }

    [Fact]
    public async Task Pruning_the_activity_feed_removes_nothing_by_default()
    {
        var feed = InterfaceStub.Create<IActivityFeedRepository>();

        Assert.Equal(0, await feed.PruneActivityAsync(DateTimeOffset.UtcNow, Token));
        Assert.Empty(InterfaceStub.Of(feed).Calls);
    }

    [Fact]
    public async Task Listing_blocked_releases_awaiting_cleanup_with_no_filter_passes_no_filter()
    {
        IReadOnlyList<BlockedRelease> list = [];
        var blocked = InterfaceStub.Create<IBlockedReleaseRepository>((name, args) =>
        {
            Assert.Equal("ListAwaitingCleanupAsync", name);
            Assert.Null(args[0]);
            return Task.FromResult(list);
        });

        Assert.Same(list, await blocked.ListAwaitingCleanupAsync(Token));
    }

    [Fact]
    public async Task A_queue_with_no_dispatch_lookup_finds_no_link()
    {
        var queue = InterfaceStub.Create<IJobQueueRepository>();

        Assert.Null(await queue.FindDispatchLinkByClientItemAsync("c1", "item", Token));
        Assert.Empty(InterfaceStub.Of(queue).Calls);
    }

    [Fact]
    public async Task A_movie_catalogue_with_no_collections_or_settled_cases_answers_empty()
    {
        var movies = InterfaceStub.Create<IMovieCatalogRepository>();

        Assert.Empty(await movies.ListCollectionMetadataAsync(Token));
        Assert.Empty(await movies.SettleImportRecoveryCasesListedAsync(new ImportRecoveryCaseScope([], []), "gone", Token));
        Assert.Empty(InterfaceStub.Of(movies).Calls);
    }

    [Fact]
    public async Task A_series_catalogue_with_no_repair_support_repairs_and_clears_nothing()
    {
        var series = InterfaceStub.Create<ISeriesCatalogRepository>();

        Assert.Empty(await series.RepairWrongEpisodeFileLinksAsync(Token));
        Assert.Empty(await series.ListEpisodeFileLinksAsync(Token));
        Assert.False(await series.ClearMislabelledEpisodeFileAsync("e1", "/tv/a.mkv", Token));
        Assert.Equal(0, await series.DetachSeriesFileRecordsAsync("s1", "/tv/a.mkv", Token));
        Assert.Equal(0, await series.ClearSeriesFileRecordsInRecycleBinAsync(Token));
        Assert.Empty(await series.RelinkEpisodesWithTheirOwnFilesAsync(Token));
        Assert.Empty(await series.SettleImportRecoveryCasesListedAsync(new ImportRecoveryCaseScope([], []), "gone", Token));
        Assert.Empty(InterfaceStub.Of(series).Calls);
    }

    [Fact]
    public async Task A_processor_store_with_no_supersede_support_finds_and_settles_nothing()
    {
        var processors = InterfaceStub.Create<IProcessorRepository>();

        Assert.Null(await processors.FindSupersededProcessorHandoffAsync("lib", "h1", Token));
        Assert.False(await processors.ReplacedAnEarlierSendAsync("lib", "h1", Token));
        Assert.False(await processors.SettleSupersededProcessorHandoffAsync("h1", "done", Token));
        await processors.TellHandoffClosedAsync("h1", Token);
        Assert.Empty(InterfaceStub.Of(processors).Calls);
    }

    [Fact]
    public async Task Updating_a_handoff_only_if_its_status_matches_updates_and_says_yes_by_default()
    {
        var processors = InterfaceStub.Create<IProcessorRepository>((name, args) =>
        {
            Assert.Equal("UpdateProcessorHandoffAsync", name);
            Assert.Equal("h1", args[0]);
            Assert.Equal("done", args[1]);
            return Task.FromResult<ProcessorHandoffItem?>(null);
        });

        Assert.True(await processors.UpdateProcessorHandoffIfStatusAsync("h1", "started", "done", null, null, null, Token));
        Assert.Equal(["UpdateProcessorHandoffAsync"], InterfaceStub.Of(processors).Calls);
    }

    [Fact]
    public async Task Open_handoffs_are_the_ones_with_a_processor()
    {
        IReadOnlyList<ProcessorHandoffItem> list = [];
        var processors = InterfaceStub.Create<IProcessorRepository>((name, args) =>
        {
            Assert.Equal("ListProcessorHandoffsWithStatusAsync", name);
            Assert.Same(ProcessorHandoffStatuses.WithProcessor, args[0]);
            return Task.FromResult(list);
        });

        Assert.Same(list, await processors.ListOpenProcessorHandoffsAsync(Token));
    }

    [Fact]
    public async Task A_download_client_with_no_extras_declines_each_optional_action()
    {
        var client = InterfaceStub.Create<IDownloadClient>();

        Assert.False(client.SetsSaveFolderPerGrab);
        Assert.Null(await client.HasUsenetProviderAsync(Client, Token));
        Assert.Null(await client.SetUsenetProviderAsync(Client, null!, Token));
        Assert.False(await client.ClearWarningsAsync(Client, Token));
        Assert.Equal(ClientResumeResult.NotSupported, await client.ResumeClientAsync(Client, Token));

        var category = await client.SetCategoryAsync(Client, "q1", "tv", null, Token);
        Assert.False(category.Succeeded);
        Assert.Equal("c1", category.ClientId);
        Assert.Equal("q1", category.QueueItemId);
        Assert.Equal(DownloadClientActions.SetCategory, category.Action);

        var check = await client.CheckKeepSeedingCategoryAsync(Client, "movies", Token);
        Assert.False(check.Supported);
        Assert.False(check.Found);
        Assert.Equal("movies", check.Category);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, check.Status);
        Assert.Contains("only a torrent can keep seeding", check.Message);
        Assert.Empty(InterfaceStub.Of(client).Calls);
    }

    [Fact]
    public void A_tracker_name_source_with_no_privacy_knows_none()
    {
        var names = InterfaceStub.Create<ITrackerDefinitionNames>();

        Assert.Null(names.Privacy("1337x"));
    }

    [Fact]
    public async Task A_title_unmonitor_service_does_not_know_whether_a_title_has_a_file_by_default()
    {
        var service = InterfaceStub.Create<IDispatchTitleUnmonitorService>();

        Assert.Null(await service.HasFileAsync("movie", "m1", Token));
        Assert.Empty(InterfaceStub.Of(service).Calls);
    }

    [Fact]
    public async Task The_no_op_notifier_completes_without_doing_anything()
    {
        await new NoOpLocalNetworkAuthChangeNotifier().NotifyChangedAsync(true, Token);
    }
}
