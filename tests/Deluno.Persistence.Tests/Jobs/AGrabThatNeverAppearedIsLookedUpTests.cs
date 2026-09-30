using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #911 (cleanup sheet B4): once the appearance timeout expires, Deluno looks
/// the release up across the client's whole queue and history, in every
/// category, and acts on why it never appeared instead of always ending the
/// download the same way.
/// </summary>
public sealed class AGrabThatNeverAppearedIsLookedUpTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task Found_under_its_own_category_is_followed_not_ended()
    {
        var (poller, dispatches, _, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.FoundAsExpected, QueueItemId: "hash-found"));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.NotNull(dispatch?.DetectedUtc);
        Assert.Equal(1, report.GrabAppearanceResolved);
        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Contains(activity.Recorded, item => item.Category == "download.grab.foundElsewhere");
    }

    [Fact]
    public async Task Found_under_another_category_is_followed_and_says_so()
    {
        var (poller, dispatches, _, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.FoundUnderAnotherCategory,
            FoundCategory: "sonarr-extra",
            ExpectedCategory: "sonarr",
            QueueItemId: "hash-found"));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.NotNull(dispatch?.DetectedUtc);
        var recorded = Assert.Single(activity.Recorded, item => item.Category == "download.grab.foundElsewhere");
        Assert.Contains("sonarr-extra", recorded.Message);
        Assert.Contains("category setting", recorded.Message);
    }

    [Fact]
    public async Task A_duplicate_the_client_already_holds_is_followed()
    {
        var (poller, dispatches, _, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.Duplicate, QueueItemId: "nzo-1"));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.NotNull(dispatch?.DetectedUtc);
        Assert.Equal(1, report.GrabAppearanceResolved);
        Assert.Contains(activity.Recorded, item => item.Category == "download.grab.duplicate");
    }

    [Fact]
    public async Task A_release_the_client_rejected_is_blocklisted_and_searched_again()
    {
        var (poller, dispatches, blocked, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.Rejected, Detail: "password protected archive"));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchIncludingArchivedAsync("gone", CancellationToken.None);
        Assert.Equal("archived", dispatch?.Status);
        var block = Assert.Single(await blocked.ListAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.ClientReportedFailure, block.ReasonCode);
        Assert.Equal(1, report.GrabAppearanceResolved);
        Assert.Contains(activity.Recorded, item => item.Category == "download.grab.rejected");
    }

    [Fact]
    public async Task A_release_chosen_on_search_that_the_client_rejected_is_not_blocklisted_or_searched_for_and_says_why()
    {
        var (poller, dispatches, blocked, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.Rejected, Detail: "password protected archive"));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3), entityType: "release");

        await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchIncludingArchivedAsync("gone", CancellationToken.None);
        Assert.Equal("archived", dispatch?.Status);
        Assert.Empty(await blocked.ListAsync(CancellationToken.None));
        var recorded = Assert.Single(activity.Recorded, item => item.Category == "download.grab.rejected");
        Assert.Contains("choose one on Search again", recorded.Message);
        Assert.DoesNotContain("searching for another copy", recorded.Message);
    }

    /// <summary>
    /// #911 acceptance: an unreachable client is a setup problem or an
    /// outage, never a bad release — it is retried, and told, never ended.
    /// </summary>
    [Fact]
    public async Task An_unreachable_client_retries_and_tells_you_without_ending_the_download()
    {
        var (poller, dispatches, blocked, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.Unreachable, Detail: "qBittorrent did not answer."));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.Null(dispatch?.DetectedUtc);
        Assert.Empty(await blocked.ListAsync(CancellationToken.None));
        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Equal(1, report.GrabAppearanceRetried);
        var recorded = Assert.Single(activity.Recorded, item => item.Category == "download.grab.unreachable");
        Assert.Contains("setup problem", recorded.Message);
    }

    /// <summary>An outage that lasts several polls is told once, not once per pass.</summary>
    [Fact]
    public async Task An_unreachable_client_only_says_so_once_while_it_stays_unreachable()
    {
        var (poller, dispatches, _, activity) = await CreateAsync(new GrabAppearanceLookupResult(
            GrabAppearanceOutcome.Unreachable, Detail: "qBittorrent did not answer."));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        await poller.PollAsync(CancellationToken.None);
        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.Equal(1, report.GrabAppearanceRetried);
        Assert.Single(activity.Recorded, item => item.Category == "download.grab.unreachable");
    }

    /// <summary>#911: not found the first time is retried once, silently, before it is treated as gone.</summary>
    [Fact]
    public async Task Not_found_the_first_time_is_retried_once_before_giving_up()
    {
        var (poller, dispatches, blocked, activity) = await CreateAsync(
            new GrabAppearanceLookupResult(GrabAppearanceOutcome.NotThere));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.Empty(await blocked.ListAsync(CancellationToken.None));
        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Equal(1, report.GrabAppearanceRetried);
        Assert.DoesNotContain(activity.Recorded, item => item.Category == "download.grab.timeout");
    }

    /// <summary>Still not found on the retry: genuinely gone, ended the ordinary way, with the release name and client told in Activity.</summary>
    [Fact]
    public async Task Not_found_on_the_retry_ends_the_download_and_names_the_release_and_client()
    {
        var (poller, dispatches, blocked, activity) = await CreateAsync(
            new GrabAppearanceLookupResult(GrabAppearanceOutcome.NotThere));
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        await poller.PollAsync(CancellationToken.None);
        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("failed", dispatch?.Status);
        Assert.Empty(await blocked.ListAsync(CancellationToken.None));
        Assert.Equal(1, report.GrabTimeoutsDetected);
        var recorded = Assert.Single(activity.Recorded, item => item.Category == "download.grab.timeout");
        Assert.Contains("gone.2010.1080p.WEB-DL-DELUNO", recorded.Message);
        Assert.Contains("qBittorrent", recorded.Message);
    }

    /// <summary>A lookup service that throws is read exactly like an unreachable client: retried, never ended.</summary>
    [Fact]
    public async Task A_lookup_that_throws_is_read_as_unreachable_and_retried()
    {
        var (poller, dispatches, blocked, _) = await CreateAsync(lookup: null, throwing: true);
        await InsertAsync(dispatches.Storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.Repository.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("sent", dispatch?.Status);
        Assert.Empty(await blocked.ListAsync(CancellationToken.None));
        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Equal(1, report.GrabAppearanceRetried);
    }

    /// <summary>No lookup service configured at all: nothing can say why, so this still ends the download the ordinary way, as before #911.</summary>
    [Fact]
    public async Task No_lookup_service_configured_still_ends_the_download_the_ordinary_way()
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        var blocked = new SqliteBlockedReleaseRepository(storage.Factory, clock);
        var activity = new RecordingActivityFeed();

        var poller = new DownloadDispatchPollingService(
            storage.Factory,
            clock,
            new QuietRecoveryHandler(),
            activity,
            dispatches,
            alerts,
            new NullRealtimeEventPublisher());

        await InsertAsync(storage, "gone", TimeSpan.FromHours(3));

        var report = await poller.PollAsync(CancellationToken.None);

        var dispatch = await dispatches.GetDispatchAsync("gone", CancellationToken.None);
        Assert.Equal("failed", dispatch?.Status);
        Assert.Equal(1, report.GrabTimeoutsDetected);
    }

    private static async Task<(
        DownloadDispatchPollingService Poller,
        (TestStorage Storage, SqliteDownloadDispatchesRepository Repository) Dispatches,
        IBlockedReleaseRepository Blocked,
        RecordingActivityFeed Activity)> CreateAsync(
        GrabAppearanceLookupResult? lookup,
        bool throwing = false)
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        var blocked = new SqliteBlockedReleaseRepository(storage.Factory, clock);
        var activity = new RecordingActivityFeed();
        var appearanceLookup = new FakeGrabAppearanceLookupService(lookup, throwing);

        var poller = new DownloadDispatchPollingService(
            storage.Factory,
            clock,
            new QuietRecoveryHandler(),
            activity,
            dispatches,
            alerts,
            new NullRealtimeEventPublisher(),
            appearanceLookup,
            blocked,
            new RecordingJobScheduler());

        return (poller, (storage, dispatches), blocked, activity);
    }

    private static async Task InsertAsync(TestStorage storage, string id, TimeSpan grabbedAgo, string entityType = "movie")
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc,
                grab_status, grab_attempted_utc, torrent_hash_or_item_id
            ) VALUES (
                @id, 'movies-main', 'movies', @entityType, @id, @release,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'sent', @grabbed,
                'succeeded', @grabbed, 'hash-1'
            )
            """;
        foreach (var (name, value) in new (string, object)[]
        {
            ("@id", id),
            ("@entityType", entityType),
            ("@release", $"{id}.2010.1080p.WEB-DL-DELUNO"),
            ("@grabbed", Start.Subtract(grabbedAgo).ToString("O"))
        })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class FakeGrabAppearanceLookupService(GrabAppearanceLookupResult? result, bool throwing) : IGrabAppearanceLookupService
    {
        public Task<GrabAppearanceLookupResult> LookupAsync(
            string downloadClientId,
            string mediaType,
            string? category,
            string? torrentHashOrItemId,
            string releaseName,
            CancellationToken cancellationToken)
            => throwing
                ? throw new InvalidOperationException("the client did not answer")
                : Task.FromResult(result!);
    }

    private sealed class RecordingActivityFeed : IActivityFeedRepository
    {
        public readonly List<ActivityEventItem> Recorded = [];

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ActivityEventItem>>(Recorded);

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => Task.FromResult(new Page<ActivityEventItem>(Recorded, null, false));

        public Task<ActivityEventItem> RecordActivityAsync(string category, string message, string? detailsJson, string? relatedJobId, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            var item = new ActivityEventItem(Guid.NewGuid().ToString("N"), category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, DateTimeOffset.UtcNow);
            Recorded.Add(item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
        {
            Recorded.AddRange(activities);
            return Task.FromResult(activities);
        }

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
        {
            var count = Recorded.Count;
            Recorded.Clear();
            return Task.FromResult(count);
        }

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingJobScheduler : IJobScheduler
    {
        public readonly List<EnqueueJobRequest> Requests = [];

        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new JobQueueItem(
                Guid.NewGuid().ToString("N"), request.JobType, request.Source, "queued", request.PayloadJson,
                0, Start, Start, null, null, null, null, null,
                request.RelatedEntityType, request.RelatedEntityId, request.IdempotencyKey, request.DedupeKey,
                3, null, Start));
        }
    }

    private sealed class QuietRecoveryHandler : IDispatchRecoveryHandler
    {
        public Task HandleGrabTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleDetectionTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleImportFailureAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string importFailureCode, string importFailureMessage, string detailsJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
