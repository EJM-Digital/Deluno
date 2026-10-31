using Deluno.Contracts;
using Moq;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The small shared rules of Deluno.Contracts: which list family a provider belongs to, who a tagged
/// connection or indexer is used for, what started a search, the System/Settings change pushes and
/// the do-nothing defaults of the recovery handler.
/// </summary>
public sealed class SharedContractHelpersTests
{
    [Theory]
    [InlineData("tmdb", "tmdb", "TMDb")]
    [InlineData("TMDB-popular", "tmdb", "TMDb")]
    [InlineData("trakt-trending", "trakt", "Trakt")]
    [InlineData(" mdblist ", "mdblist", "MDBList")]
    [InlineData("letterboxd-watchlist", "letterboxd", "Letterboxd")]
    [InlineData("stevenlu", "stevenlu", "StevenLu")]
    [InlineData("rss", "rss", "RSS feeds")]
    [InlineData("url-list", "url-list", "Web lists")]
    [InlineData(null, "other", "Lists")]
    [InlineData("  ", "other", "Lists")]
    [InlineData("plex", "plex", "Plex")]
    [InlineData("tmdbish", "tmdbish", "Tmdbish")]
    public void A_list_provider_belongs_to_the_family_a_person_knows_it_by(string? provider, string key, string name)
    {
        Assert.Equal(key, ListProviderFamilies.KeyFor(provider));
        Assert.Equal(name, ListProviderFamilies.NameFor(provider));
    }

    [Theory]
    [InlineData(null, new string[0], true)]
    [InlineData("  ", new[] { "a" }, true)]
    [InlineData("4k", null, false)]
    [InlineData("4k", new string[0], false)]
    [InlineData("4k, anime", new[] { " ANIME ", "" }, true)]
    [InlineData("4k", new[] { "anime" }, false)]
    public void A_tagged_connection_is_used_only_for_a_title_sharing_a_tag(string? connectionTags, string[]? titleTags, bool eligible)
        => Assert.Equal(eligible, TagRoutingMatch.IsEligible(connectionTags, titleTags));

    [Fact]
    public void The_reason_for_a_tag_route_names_the_tag_that_matched()
    {
        Assert.Null(TagRoutingMatch.Explain(null, ["4k"], "Client"));
        Assert.Null(TagRoutingMatch.Explain("4k", null, "Client"));
        Assert.Null(TagRoutingMatch.Explain("4k", ["anime"], "Client"));
        Assert.Equal("Client only takes titles tagged \"4k\", and this one is.", TagRoutingMatch.Explain("hd, 4k", [" 4K "], "Client"));
    }

    [Fact]
    public void A_librarys_indexer_choices_refuse_by_never_tags_first_then_by_only_tags()
    {
        Assert.True(TagRoutingMatch.LibrarySourceIsEligible(null, null, null));
        Assert.True(TagRoutingMatch.LibrarySourceIsEligible("anime", "kids", ["Anime"]));
        Assert.False(TagRoutingMatch.LibrarySourceIsEligible("anime", null, ["tv"]));
        Assert.False(TagRoutingMatch.LibrarySourceIsEligible("anime", "kids", ["anime", "kids"]));

        Assert.Equal(
            "Nyaa is never used for titles tagged \"kids\", and Bluey has that tag.",
            TagRoutingMatch.LibrarySourceRefusal("anime", "kids", ["kids"], "Nyaa", "Bluey"));
        Assert.Equal(
            "Nyaa is only used for titles tagged \"anime\" or \"ova\", and Bluey has none of them.",
            TagRoutingMatch.LibrarySourceRefusal("anime,ova", null, null, "Nyaa", "Bluey"));
        Assert.Null(TagRoutingMatch.LibrarySourceRefusal(null, "kids", ["tv"], "Nyaa", "Bluey"));
    }

    [Theory]
    [InlineData("manual", "right away, because you asked")]
    [InlineData(" Manual-Season ", "right away, because you asked")]
    [InlineData("movie-added", "right away, because a film was just added")]
    [InlineData("series-added", "right away, because a show was just added")]
    [InlineData("rss", "because new releases appeared on your indexers")]
    [InlineData("recovery-retry", "to try again after a problem")]
    [InlineData("grab-rejected", "to find another release after one was refused")]
    [InlineData("health-replacement", "to replace a download that went wrong")]
    [InlineData("download-integrity-failed", "to replace a damaged download")]
    [InlineData("season-pack-partial-import", "to fetch what a partly imported season was missing")]
    [InlineData("external-redownload", "because an outside app asked for another copy")]
    [InlineData("mislabelled-file", "to replace a file that was another episode")]
    [InlineData("automatic-season", "as part of the automatic library check")]
    [InlineData("scheduled", "on schedule")]
    [InlineData("", "on schedule")]
    [InlineData(null, "on schedule")]
    public void Every_start_of_a_search_says_itself_in_plain_words(string? triggeredBy, string words)
        => Assert.Equal(words, SearchStartedWords.WhenQueued(triggeredBy));

    [Fact]
    public async Task Each_system_change_push_names_what_changed_and_a_missing_publisher_is_fine()
    {
        var publisher = new Mock<IRealtimeEventPublisher>();
        publisher.Setup(p => p.PublishEntityChangedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        publisher.Setup(p => p.PublishDashboardEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var ct = CancellationToken.None;

        await publisher.Object.PublishBackupChangedAsync(ct);
        await publisher.Object.PublishUpdateStatusChangedAsync(ct);
        await publisher.Object.PublishRecycleBinChangedAsync(ct);
        await publisher.Object.PublishSystemTaskChangedAsync("nightly", ct);
        await publisher.Object.PublishMonitoringChangedAsync(ct);
        await publisher.Object.PublishSystemDatabasesChangedAsync(ct);
        await publisher.Object.PublishOutboundThrottleChangedAsync(ct);
        await publisher.Object.PublishCompanionJobChangedAsync("job-1", ct);
        await publisher.Object.PublishBundledComponentChangedAsync("ffmpeg", ct);
        await publisher.Object.PublishConfigChangedAsync(ConfigAreas.Tags, ct);

        foreach (var (type, id) in new[]
        {
            ("Backup", "backups"), ("UpdateStatus", "updates"), ("RecycleBin", "recycle-bin"), ("SystemTask", "nightly"),
            ("Monitoring", "monitoring"), ("SystemDatabases", "databases"), ("OutboundThrottle", "outbound"),
            ("CompanionJob", "job-1"), ("BundledComponent", "ffmpeg")
        })
        {
            publisher.Verify(p => p.PublishEntityChangedAsync(type, id, ct), Times.Once);
        }

        publisher.Verify(p => p.PublishDashboardEventAsync("ConfigChanged", It.Is<object>(o => o.ToString()!.Contains("tags")), ct), Times.Once);

        IRealtimeEventPublisher? none = null;
        await none.PublishBackupChangedAsync();
        await none.PublishUpdateStatusChangedAsync();
        await none.PublishRecycleBinChangedAsync();
        await none.PublishSystemTaskChangedAsync("x");
        await none.PublishMonitoringChangedAsync();
        await none.PublishSystemDatabasesChangedAsync();
        await none.PublishOutboundThrottleChangedAsync();
        await none.PublishCompanionJobChangedAsync("x");
        await none.PublishBundledComponentChangedAsync("x");
        await none.PublishConfigChangedAsync("x");
    }

    [Fact]
    public void The_record_of_a_cleanup_reports_each_count_as_metadata()
    {
        Assert.Equal(new DeletionCleanup(0, 0, 0, 0, 0), DeletionCleanup.Nothing);
        var metadata = new DeletionCleanup(1, 2, 3, 4, 5).AsMetadata();
        Assert.Equal("1", metadata["cancelledJobCount"]);
        Assert.Equal("2", metadata["archivedDownloadCount"]);
        Assert.Equal("3", metadata["endedProcessorHandoffCount"]);
        Assert.Equal("4", metadata["settledRecoveryCaseCount"]);
        Assert.Equal("5", metadata["forgottenHealthRecordCount"]);
    }

    private sealed class BareHandler : IDispatchRecoveryHandler
    {
        public Task HandleGrabTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleDetectionTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportFailureAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string importFailureCode, string importFailureMessage, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_recovery_handler_that_writes_no_cases_ignores_vanished_settled_and_scope_calls()
    {
        IDispatchRecoveryHandler handler = new BareHandler();
        await handler.HandleVanishedDownloadingAsync("t", "movie", "c", "C", "r", "e", "{}", CancellationToken.None);
        await handler.HandleDispatchSettledAsync("d", "done", CancellationToken.None);
        Assert.Equal(0, await handler.HandleScopeSettledAsync(new ImportRecoveryCaseScope([], []), "gone", CancellationToken.None));
    }
}
