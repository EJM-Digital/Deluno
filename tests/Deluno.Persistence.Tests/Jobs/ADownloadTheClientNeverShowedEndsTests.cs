using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// A download the client accepted and then never showed comes to an end.
///
/// <para><b>Why this file exists.</b> Before the soak the rig held four
/// downloads from days earlier that still read "Sent", in green, on every
/// screen. Their torrents had been removed. The poller had raised an alert and
/// an import issue about each and left the download itself untouched, so no
/// screen offered anything to do with them, and the "14-day sweep" that was
/// meant to archive them was a constant nothing read. They were for ever.</para>
/// </summary>
public sealed class ADownloadTheClientNeverShowedEndsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-19T00:00:00Z");

    [Fact]
    public async Task It_stops_reading_sent_once_the_detection_limit_has_passed()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "gone", TimeSpan.FromHours(3), clock);
        await InsertAsync(storage.Factory, "fresh", TimeSpan.FromMinutes(30), clock);

        await poller.PollAsync(CancellationToken.None);

        var gone = await dispatches.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("failed", gone?.Status);
        Assert.Equal("not_found", gone?.GrabFailureCode);
        Assert.Contains("never showed it", gone?.GrabMessage);
        // The client did accept it: the grab retry and the indexer scoreboard read this column.
        Assert.Equal("succeeded", gone?.GrabStatus);
        Assert.Equal("sent", (await dispatches.GetDispatchAsync("fresh", CancellationToken.None))?.Status);
    }

    /// <summary>
    /// The rig's case exactly: the alert was raised and acknowledged days ago,
    /// which is what stops the poller saying it again. The download must still end.
    /// </summary>
    [Fact]
    public async Task One_whose_alert_was_acknowledged_long_ago_still_ends()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, alerts) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "old", TimeSpan.FromDays(3), clock);
        await alerts.CreateAlertAsync("old", "old", "Download not detected in qBittorrent", "grab-timeout", "warning", null, CancellationToken.None);
        await alerts.AcknowledgeAlertsForDispatchAsync("old", CancellationToken.None);

        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal("failed", (await dispatches.GetDispatchAsync("old", CancellationToken.None))?.Status);
        Assert.Equal(0, report.GrabTimeoutsDetected);
    }

    /// <summary>
    /// A client that was down for an afternoon shows its downloads late. That
    /// is not a failure, and the ending must not survive it.
    /// </summary>
    [Fact]
    public async Task One_the_client_shows_late_is_a_download_again()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "late", TimeSpan.FromHours(3), clock);
        await poller.PollAsync(CancellationToken.None);

        var detected = await dispatches.RecordDetectionAsync("late", "hash-1", 1024, completed: false, CancellationToken.None);

        Assert.Equal("sent", detected.Status);
        Assert.Null(detected.GrabFailureCode);
        Assert.NotNull(detected.DetectedUtc);
    }

    [Fact]
    public async Task Ending_it_twice_changes_nothing_the_second_time()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (_, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "gone", TimeSpan.FromHours(3), clock);

        Assert.True(await dispatches.MarkNeverDetectedAsync("gone", "never showed it", CancellationToken.None));
        Assert.False(await dispatches.MarkNeverDetectedAsync("gone", "never showed it", CancellationToken.None));
    }

    /// <summary>
    /// Transfers lists a failed download beside the import issue about it. They
    /// are one problem: a person who settles the issue should not find the
    /// download still waiting under another name.
    /// </summary>
    [Fact]
    public async Task Settling_the_import_issue_about_it_settles_the_download_too()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, alerts) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "gone", TimeSpan.FromHours(3), clock);
        await InsertAsync(storage.Factory, "fresh", TimeSpan.FromMinutes(30), clock);
        await poller.PollAsync(CancellationToken.None);

        await RecoveryCaseAlerts.AcknowledgeAsync("""{"DispatchId":"gone"}""", alerts, CancellationToken.None, dispatches);
        await RecoveryCaseAlerts.AcknowledgeAsync("""{"DispatchId":"fresh"}""", alerts, CancellationToken.None, dispatches);

        Assert.Null(await dispatches.GetDispatchAsync("gone", CancellationToken.None));
        // A download that has not failed is not archived by someone tidying an issue.
        Assert.Equal("sent", (await dispatches.GetDispatchAsync("fresh", CancellationToken.None))?.Status);
    }

    /// <summary>
    /// #554, the rig's case: SABnzbd showed each download and Deluno handed it
    /// to Weir, and two hours later the poller called it never shown, because
    /// only a completion webhook had ever recorded a sighting.
    /// </summary>
    [Fact]
    public async Task A_download_the_client_has_shown_is_never_ended_as_never_shown()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, alerts) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "shown", TimeSpan.FromMinutes(5), clock);
        await dispatches.RecordDetectionAsync("shown", "nzo-1", 1024, completed: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var shown = await dispatches.GetDispatchAsync("shown", CancellationToken.None);
        Assert.Equal("sent", shown?.Status);
        Assert.Null(shown?.GrabFailureCode);
        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.False(await alerts.HasUnresolvedAlertAsync("shown", "grab-timeout", CancellationToken.None));
    }

    /// <summary>
    /// A download can be on screen in its client for many hours. The wait for an
    /// import starts when the client finishes it, not when it first appeared.
    /// </summary>
    [Fact]
    public async Task A_long_download_is_not_overdue_until_the_client_has_finished_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (poller, dispatches, alerts) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "long", TimeSpan.FromMinutes(5), clock);
        await dispatches.RecordDetectionAsync("long", "hash-long", 1024, completed: false, CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(10));
        var stillDownloading = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, stillDownloading.DetectionTimeoutsDetected);
        Assert.False(await alerts.HasUnresolvedAlertAsync("long", "detection-timeout", CancellationToken.None));

        await dispatches.RecordDetectionAsync("long", "hash-long", 4096, completed: true, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(5));
        var finishedLongAgo = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, finishedLongAgo.DetectionTimeoutsDetected);
        Assert.True(await alerts.HasUnresolvedAlertAsync("long", "detection-timeout", CancellationToken.None));
    }

    /// <summary>
    /// Polling reports a download on every pass. Each fact is written once, so a
    /// later pass moves no time and adds nothing to the download's timeline.
    /// </summary>
    [Fact]
    public async Task Each_sighting_is_recorded_once()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (_, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "seen", TimeSpan.FromMinutes(5), clock);

        var first = await dispatches.RecordDetectionAsync("seen", "hash-seen", 100, completed: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await dispatches.RecordDetectionAsync("seen", "hash-seen", 200, completed: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var finished = await dispatches.RecordDetectionAsync("seen", "hash-seen", 300, completed: true, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatches.RecordDetectionAsync("seen", "hash-seen", 300, completed: true, CancellationToken.None);

        Assert.Equal(Start, first.DetectedUtc);
        Assert.Null(first.ClientCompletedUtc);
        Assert.Equal(first.DetectedUtc, again.DetectedUtc);
        Assert.Equal(first.DetectedUtc, finished.DetectedUtc);
        Assert.Equal(Start.AddMinutes(2), finished.ClientCompletedUtc);
        var events = (await dispatches.GetDispatchTimelineAsync("seen", CancellationToken.None)).Select(item => item.EventType).ToArray();
        Assert.Equal(1, events.Count(type => type == "detection_succeeded"));
        Assert.Equal(1, events.Count(type => type == "client_completed"));
    }

    /// <summary>
    /// Polling can see a download before the client's completion webhook
    /// arrives. The webhook used to treat any earlier sighting as a duplicate of
    /// itself, and the completion it carries would have been lost.
    /// </summary>
    [Fact]
    public async Task A_completion_webhook_after_polling_saw_the_download_still_records_its_end()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (_, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "hooked", TimeSpan.FromMinutes(5), clock);
        await dispatches.RecordDetectionAsync("hooked", "hash-hooked", 100, completed: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(30));
        var webhook = new Deluno.Integrations.DownloadClients.DownloadClientWebhookService(
            dispatches, NullLogger<Deluno.Integrations.DownloadClients.DownloadClientWebhookService>.Instance);

        var result = await webhook.HandleAsync(
            "qbittorrent-main",
            new Deluno.Integrations.DownloadClients.DownloadClientWebhookRequest("completed", "hooked", "hash-hooked", null, null, 4096, null),
            CancellationToken.None);

        Assert.True(result.Accepted);
        var hooked = await dispatches.GetDispatchAsync("hooked", CancellationToken.None);
        Assert.Equal(Start, hooked?.DetectedUtc);
        Assert.Equal(Start.AddMinutes(30), hooked?.ClientCompletedUtc);
    }

    /// <summary>
    /// What keeps a title off the work list (#554, #555). A download the client
    /// has shown and that is waiting for its import, or sitting with a processor,
    /// is still happening; so is one the client never showed, whose client may
    /// only be unreachable. An imported one is not.
    /// </summary>
    [Fact]
    public async Task A_download_waiting_for_its_import_is_still_happening()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        var (_, dispatches, _) = await CreateAsync(storage, clock);
        await InsertAsync(storage.Factory, "waiting", TimeSpan.FromHours(1), clock);
        await InsertAsync(storage.Factory, "unseen", TimeSpan.FromHours(3), clock);
        await InsertAsync(storage.Factory, "imported", TimeSpan.FromHours(1), clock);
        await dispatches.RecordDetectionAsync("waiting", "hash-waiting", 1024, completed: true, CancellationToken.None);
        await dispatches.MarkNeverDetectedAsync("unseen", "never showed it", CancellationToken.None);
        await dispatches.RecordDetectionAsync("imported", "hash-imported", 1024, completed: true, CancellationToken.None);
        await SetImportStatusAsync(storage.Factory, "imported", "imported");

        var live = await new Deluno.Media.DispatchLiveDownloadLookup(dispatches).ListEntityIdsStillDownloadingAsync(CancellationToken.None);

        Assert.Contains("waiting", live);
        Assert.Contains("unseen", live);
        Assert.DoesNotContain("imported", live);

        // The one its client never showed is marked, so a search may send it again.
        var inFlight = await dispatches.ListDownloadsInFlightAsync(100, CancellationToken.None);
        Assert.True(inFlight.Single(download => download.EntityId == "unseen").NeverShown);
        Assert.False(inFlight.Single(download => download.EntityId == "waiting").NeverShown);
    }

    private static async Task SetImportStatusAsync(IDelunoDatabaseConnectionFactory factory, string id, string importStatus)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_dispatches SET import_status = @status WHERE id = @id";
        foreach (var (name, value) in new (string, object)[] { ("@id", id), ("@status", importStatus) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<(DownloadDispatchPollingService Poller, SqliteDownloadDispatchesRepository Dispatches, SqliteDispatchAlertRepository Alerts)> CreateAsync(
        TestStorage storage,
        FixedTimeProvider clock)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var poller = new DownloadDispatchPollingService(
            storage.Factory,
            clock,
            new QuietRecoveryHandler(),
            new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            dispatches,
            alerts,
            new NullRealtimeEventPublisher());
        return (poller, dispatches, alerts);
    }

    private static async Task InsertAsync(IDelunoDatabaseConnectionFactory factory, string id, TimeSpan grabbedAgo, FixedTimeProvider clock)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc,
                grab_status, grab_attempted_utc
            ) VALUES (
                @id, 'movies-main', 'movie', 'movie', @id, @release,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'sent', @grabbed,
                'succeeded', @grabbed
            )
            """;
        foreach (var (name, value) in new (string, object)[]
        {
            ("@id", id),
            ("@release", $"{id}.2010.1080p.WEB-DL-DELUNO"),
            ("@grabbed", clock.GetUtcNow().Subtract(grabbedAgo).ToString("O"))
        })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class QuietRecoveryHandler : IDispatchRecoveryHandler
    {
        public Task HandleGrabTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleDetectionTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportFailureAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string importFailureCode, string importFailureMessage, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
