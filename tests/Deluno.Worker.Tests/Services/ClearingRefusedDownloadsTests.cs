using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Clearing up after a release Deluno has refused.
///
/// <para>James found the hole: <i>"if we are refusing something, is it being
/// deleted and cleaned up so there are no traces of it"</i>. It was not. A
/// refused copy kept costing disk, kept sitting in the client's queue, and —
/// worst of the three — the client kept remembering it, so the day you
/// un-refused it the client would silently decline to fetch it. The original
/// trap, reappearing at the far end of its own fix.</para>
///
/// <para>What is guarded here is the waiting. The sharing rule owns a
/// finished download until the site's rule is met, and this pass has no
/// business overruling it — so a copy still under a hold is left alone and
/// tried again later. DESIGN-007 decisions 16 and 17.</para>
/// </summary>
public sealed class ClearingRefusedDownloadsTests
{
    [Fact]
    public async Task It_asks_the_client_to_forget_a_refused_release()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

        // Forget, not delete: on a usenet client the history outlives the
        // transfer, and it is the history that refuses the release.
        clients.Verify(
            service => service.ExecuteActionAsync(
                "qbittorrent-main",
                It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.RemoveRefusedCopy),
                It.IsAny<CancellationToken>()),
            Times.Once);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// #887: off by default -- a still-packed archive is blocklisted, but its
    /// files never used to be an option to clear. Turned on, they are cleared
    /// exactly the same way any other justified refusal is.
    /// </summary>
    [Fact]
    public async Task A_still_packed_archive_is_left_alone_unless_the_setting_is_turned_on()
    {
        var blocklist = Blocklist(RefusedPackedArchive("hash-1"));
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);
        blocklist.Verify(
            list => list.ListAwaitingCleanupAsync(
                It.Is<Func<string, bool>>(decide => decide(ImportFailurePolicy.UnsupportedFile) == false),
                It.IsAny<CancellationToken>()),
            Times.Once);

        blocklist.Invocations.Clear();
        await Cleanup(blocklist.Object, clients.Object, Sharing(), removeAndDeletePackedArchivesFromClient: true)
            .CleanUpEverythingAsync(CancellationToken.None);
        blocklist.Verify(
            list => list.ListAwaitingCleanupAsync(
                It.Is<Func<string, bool>>(decide => decide(ImportFailurePolicy.UnsupportedFile) == true),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The setting only ever adds the packed-archive reason. Everything
        // the shipped table already says to clear is untouched by it.
        blocklist.Verify(
            list => list.ListAwaitingCleanupAsync(
                It.Is<Func<string, bool>>(decide => decide(ImportFailurePolicy.LikelySample) == true),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The one that matters. Deluno refused the release; the tracker still
    /// expects the seed. The rule that knows what the site wants wins.
    /// </summary>
    [Fact]
    public async Task It_leaves_a_copy_the_sharing_rule_still_needs()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing("hash-1")).CleanUpEverythingAsync(CancellationToken.None);

        clients.VerifyNoOtherCalls();
        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A client that is off right now will not be off for ever, so nothing is
    /// marked done and the next pass tries again.
    /// </summary>
    [Fact]
    public async Task A_client_that_cannot_be_reached_is_tried_again_next_time()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        clients.Setup(service => service.ExecuteActionAsync(
                It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("The client is not answering."));

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_to_clear_asks_nobody_anything()
    {
        var blocklist = Blocklist();
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

        clients.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The manual half. DESIGN-007: "nothing automatic is only automatic" —
    /// a refusal that predates the setting, or one whose client was off when
    /// the schedule came round, can be cleared by hand.
    /// </summary>
    [Fact]
    public async Task One_refusal_can_be_cleared_by_hand()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing())
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.Cleared, outcome);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// And the button does not get to overrule the sharing rule either. A
    /// manual override that ignored the tracker would be a good way to lose an
    /// account.
    /// </summary>
    [Fact]
    public async Task Clearing_by_hand_still_waits_for_the_sharing_rule()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing("hash-1"))
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.StillSharing, outcome);
        clients.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Told apart, because they need different words on the screen: one is
    /// "there was nothing left to clear", the other is "that is not a thing".
    /// </summary>
    [Fact]
    public async Task A_refusal_with_nothing_left_to_clear_is_not_the_same_as_one_that_does_not_exist()
    {
        var blocklist = Blocklist();
        blocklist.Setup(list => list.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Refused("hash-1")]);

        var cleanup = Cleanup(blocklist.Object, Clients().Object, Sharing());

        Assert.Equal(
            RefusedDownloadCleanupOutcomes.NothingToClear,
            await cleanup.CleanUpOneAsync("block-1", CancellationToken.None));
        Assert.Equal(
            RefusedDownloadCleanupOutcomes.NotFound,
            await cleanup.CleanUpOneAsync("never-blocked", CancellationToken.None));
    }

    /// <summary>
    /// #1062: a download Deluno never sent is left alone, and said so plainly
    /// rather than reported as the client being unavailable. The row is
    /// closed and Activity says why, so it never sits "awaiting cleanup"
    /// unseen for ever.
    /// </summary>
    [Fact]
    public async Task A_download_Deluno_never_sent_is_left_alone_and_the_row_is_closed_with_an_Activity_line()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        clients.Setup(service => service.ExecuteActionAsync(
                It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientId, DownloadClientActionRequest request, CancellationToken _) =>
                new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, false, "Deluno did not send it.") { Refused = true });
        var activity = new Mock<IActivityFeedRepository>();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing(), activityFeedRepository: activity.Object)
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.NotSentByDeluno, outcome);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(feed => feed.RecordActivityAsync(
            "download.cleanup.left-alone",
            It.Is<string>(message => message.Contains("Deluno did not send that download")),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        // Never asked the client to remove it: only ownership was refused.
        clients.Verify(service => service.ExecuteActionAsync(
            It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// #882/#883/#885, cleanup sheet D1/D2/D4: a release the client itself
    /// reported as failed (missing articles, an encrypted archive, a damaged
    /// unpack) is removed from the client with its data once the user's own
    /// "remove a bad download" and "delete its files" switches are on.
    /// </summary>
    [Fact]
    public async Task A_client_reported_failure_is_removed_with_its_data_once_cleanup_is_turned_on()
    {
        var blocklist = Blocklist(RefusedForClientReportedFailure("nzo-1"));
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing(), clientReportedFailureCleanupOn: true)
            .CleanUpEverythingAsync(CancellationToken.None);

        clients.Verify(
            service => service.ExecuteActionAsync(
                "sabnzbd-main",
                It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.RemoveRefusedCopy),
                It.IsAny<CancellationToken>()),
            Times.Once);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// #882/#883/#885: removing a refused download and its files is an
    /// automatic action just like the blocklist-and-search-again it follows,
    /// so it must reach Activity in plain words too — a logger line settles
    /// nothing for a person looking at the screen.
    /// </summary>
    [Fact]
    public async Task Clearing_a_client_reported_failure_is_written_to_activity_in_plain_words()
    {
        var blocklist = Blocklist(RefusedForClientReportedFailure("nzo-1"));
        var clients = Clients();
        var activity = new Mock<IActivityFeedRepository>();

        await Cleanup(blocklist.Object, clients.Object, Sharing(), clientReportedFailureCleanupOn: true, activityFeedRepository: activity.Object)
            .CleanUpEverythingAsync(CancellationToken.None);

        activity.Verify(
            repository => repository.RecordActivityAsync(
                "download.cleanup.cleared",
                It.Is<string>(message => message.Contains("removed", StringComparison.OrdinalIgnoreCase) && message.Contains("deleted its files", StringComparison.OrdinalIgnoreCase)),
                null, null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #936: nothing automatic is on by default, and an existing install is
    /// never flipped on. With the switches off, the release stays exactly
    /// where it is — still blocklisted and still shown (the Blocklist screen
    /// already says why), but not removed and not deleted.
    /// </summary>
    [Fact]
    public async Task A_client_reported_failure_is_left_alone_while_cleanup_is_off()
    {
        var blocklist = Blocklist(RefusedForClientReportedFailure("nzo-1"));
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing(), clientReportedFailureCleanupOn: false)
            .CleanUpEverythingAsync(CancellationToken.None);

        clients.VerifyNoOtherCalls();
        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The manual "Clean up now" button answers to the same setting as the automatic pass.</summary>
    [Fact]
    public async Task Clearing_a_client_reported_failure_by_hand_still_respects_the_setting()
    {
        var blocklist = Blocklist(RefusedForClientReportedFailure("nzo-1"));
        var clients = Clients();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing(), clientReportedFailureCleanupOn: false)
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.CleanupDisabled, outcome);
        clients.VerifyNoOtherCalls();
    }

    /// <summary>
    /// #966: cleanup-cleared already landed in Activity but never reached the
    /// outbound path. Gated by its own preference, off by default (#936).
    /// </summary>
    [Fact]
    public async Task Clearing_a_release_dispatches_cleanup_cleared_when_the_toggle_is_on()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();
        var outbound = new RecordingOutboundNotificationService();
        var preferences = new InMemoryNotificationPreferences();
        var current = await preferences.GetPreferencesAsync(CancellationToken.None);
        current.CleanupClearedEnabled = true;
        await preferences.UpdatePreferencesAsync(current, CancellationToken.None);

        await Cleanup(blocklist.Object, clients.Object, Sharing(), outboundNotificationService: outbound, notificationPreferences: preferences)
            .CleanUpEverythingAsync(CancellationToken.None);

        Assert.Contains("download.cleanup.cleared", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Clearing_a_release_stays_silent_when_the_toggle_is_off()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();
        var outbound = new RecordingOutboundNotificationService();
        var preferences = new InMemoryNotificationPreferences();

        await Cleanup(blocklist.Object, clients.Object, Sharing(), outboundNotificationService: outbound, notificationPreferences: preferences)
            .CleanUpEverythingAsync(CancellationToken.None);

        Assert.DoesNotContain("download.cleanup.cleared", outbound.DispatchedCategories);
    }

    private sealed class RecordingOutboundNotificationService : IOutboundNotificationService
    {
        public List<string> DispatchedCategories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            DispatchedCategories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ helpers

    private static RefusedDownloadCleanupService Cleanup(
        IBlockedReleaseRepository blocklist,
        IDownloadClientTelemetryService clients,
        IDownloadSharingRepository sharing,
        bool clientReportedFailureCleanupOn = false,
        bool removeAndDeletePackedArchivesFromClient = false,
        IActivityFeedRepository? activityFeedRepository = null,
        IOutboundNotificationService? outboundNotificationService = null,
        INotificationService? notificationPreferences = null)
        => new(
            blocklist,
            clients,
            sharing,
            PlatformSettings(clientReportedFailureCleanupOn, removeAndDeletePackedArchivesFromClient),
            activityFeedRepository ?? Mock.Of<IActivityFeedRepository>(),
            NullLogger<RefusedDownloadCleanupService>.Instance,
            outboundNotificationService,
            notificationPreferences);

    private static Mock<IBlockedReleaseRepository> Blocklist(params BlockedRelease[] pending)
    {
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist.Setup(list => list.ListAwaitingCleanupAsync(It.IsAny<Func<string, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        return blocklist;
    }

    /// <summary>#882/#883/#885 and #887: both off unless a test says otherwise.</summary>
    private static IPlatformSettingsRepository PlatformSettings(
        bool clientReportedFailureCleanupOn = false,
        bool removeAndDeletePackedArchivesFromClient = false)
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformSettingsSnapshot(
                MovieRootPath: null,
                SeriesRootPath: null,
                DownloadsPath: null,
                IncompleteDownloadsPath: null,
                AutoStartJobs: true,
                EnableNotifications: true,
                RenameOnImport: true,
                UseHardlinks: true,
                CleanupEmptyFolders: true,
                MovieFolderFormat: "{Movie}",
                SeriesFolderFormat: "{Series}",
                MovieFileFormat: "{Movie}",
                EpisodeFileFormat: "{Episode}",
                HostBindAddress: "127.0.0.1",
                HostPort: 7979,
                UrlBase: string.Empty,
                RequireAuthentication: false,
                UiTheme: "dark",
                DefaultMovieView: "poster",
                DefaultShowView: "poster",
                MetadataCertificationCountry: "US",
                MetadataLanguage: "en",
                MetadataProviderMode: "auto",
                MetadataBrokerUrl: string.Empty,
                MetadataBrokerConfigured: false,
                MetadataTmdbApiKeyConfigured: false,
                MetadataOmdbApiKeyConfigured: false,
                ReleaseNeverGrabPatterns: string.Empty,
                SearchScoringMode: "balanced",
                ImportRecoveryRetentionDays: 30,
                UpdatedUtc: DateTimeOffset.UnixEpoch,
                CleanupRemoveClientEntryAfterThreshold: clientReportedFailureCleanupOn,
                CleanupPurgePayloadAfterThreshold: clientReportedFailureCleanupOn,
                RemoveAndDeletePackedArchivesFromClient: removeAndDeletePackedArchivesFromClient));
        return settings.Object;
    }

    private static Mock<IDownloadClientTelemetryService> Clients()
    {
        var clients = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        clients.Setup(service => service.ExecuteActionAsync(
                It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientId, DownloadClientActionRequest request, CancellationToken _) =>
                new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, true, "Forgotten."));
        return clients;
    }

    private static IDownloadSharingRepository Sharing(params string[] heldQueueItemIds)
    {
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repository => repository.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadSharingSnapshot(
                heldQueueItemIds
                    .Select(id => new DownloadSharingHold(
                        "qbittorrent-main", "qBittorrent", id, "Arrival", "2 days left", 0, false, false))
                    .ToArray(),
                0,
                null,
                DateTimeOffset.UnixEpoch));
        return sharing.Object;
    }

    private static BlockedRelease Refused(string queueItemId)
        => new(
            "block-1",
            BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"),
            "Arrival.2016.2160p",
            "Nebula",
            "movies",
            "movie-1",
            "Arrival",
            ImportFailurePolicy.LikelySample,
            "It was a sample, not the film.",
            queueItemId,
            "qbittorrent-main",
            "qBittorrent",
            DateTimeOffset.UnixEpoch);

    /// <summary>A release SABnzbd or NZBGet itself reported as failed (#882/#883/#885) — missing articles, an encrypted archive, or a damaged unpack.</summary>
    private static BlockedRelease RefusedForClientReportedFailure(string queueItemId)
        => new(
            "block-1",
            BlockedReleaseKeys.For("Arrival.2016.2160p", "NZBGeek"),
            "Arrival.2016.2160p",
            "NZBGeek",
            "movies",
            "movie-1",
            "Arrival",
            ImportFailurePolicy.ClientReportedFailure,
            "SABnzbd could not download it: too many articles are missing.",
            queueItemId,
            "sabnzbd-main",
            "SABnzbd",
            DateTimeOffset.UnixEpoch);

    private static BlockedRelease RefusedPackedArchive(string queueItemId)
        => new(
            "block-1",
            BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"),
            "Arrival.2016.2160p",
            "Nebula",
            "movies",
            "movie-1",
            "Arrival",
            ImportFailurePolicy.UnsupportedFile,
            "It was still a packed archive.",
            queueItemId,
            "qbittorrent-main",
            "qBittorrent",
            DateTimeOffset.UnixEpoch);
}

/// <summary>Holds preferences in memory for tests that have no database.</summary>
internal sealed class InMemoryNotificationPreferences : INotificationService
{
    private Deluno.Notifications.Contracts.NotificationPreferences current = new();

    public Task<Deluno.Notifications.Contracts.NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(current);

    public Task UpdatePreferencesAsync(Deluno.Notifications.Contracts.NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        current = preferences;
        return Task.CompletedTask;
    }
}
