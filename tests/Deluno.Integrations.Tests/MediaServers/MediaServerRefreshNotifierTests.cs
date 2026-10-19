using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #662: the shared notify path behind import, upgrade, season-pack, rename
/// and manual removal - path mapping applied per server, one down server
/// never stopping another from being told, and a failure landing on that
/// server's own health rather than anywhere else.
/// </summary>
public sealed class MediaServerRefreshNotifierTests
{
    private static MediaServerConnection Server(string id, string kind, string healthStatus = "healthy")
        => new(id, kind, "Server " + id, "http://192.168.1.51:8096", "token", true, healthStatus, null, null, null, "Rig", "abc", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_path_is_translated_to_the_servers_own_view_before_the_client_is_called()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var repository = new FakeRepository(
            [server],
            [new MediaServerPathMappingItem("m1", "s1", "/mnt/media/Movies", "/data/Movies", true, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync(
            "movies",
            [new MediaServerPathChange("/mnt/media/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var change = Assert.Single(Assert.Single(client.Calls).Changes);
        Assert.Equal("/data/Movies/Sintel (2010)/Sintel.mkv", change.Path);
    }

    [Fact]
    public async Task A_path_with_no_mapping_is_sent_as_is()
    {
        var server = Server("s1", MediaServerKinds.Jellyfin);
        var repository = new FakeRepository([server], []);
        var client = new FakeClient(MediaServerKinds.Jellyfin);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Equal("/media/Movies/Sintel.mkv", Assert.Single(client.Calls[0].Changes).Path);
    }

    [Fact]
    public async Task One_down_server_never_stops_another_being_told()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var up = Server("up", MediaServerKinds.Jellyfin);
        var repository = new FakeRepository([down, up], []);
        var downClient = new FakeClient(MediaServerKinds.Plex, fail: true);
        var upClient = new FakeClient(MediaServerKinds.Jellyfin);
        var registry = new MultiRegistry(new() { [MediaServerKinds.Plex] = downClient, [MediaServerKinds.Jellyfin] = upClient });
        var notifier = new MediaServerRefreshNotifier(repository, registry, TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Single(downClient.Calls);
        Assert.Single(upClient.Calls);
    }

    [Fact]
    public async Task A_failed_notify_records_a_clearable_health_finding_on_that_server_only()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var repository = new FakeRepository([down], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var recorded = Assert.Single(repository.HealthRecords);
        Assert.Equal("down", recorded.Id);
        Assert.False(recorded.Result.Ok);
    }

    [Fact]
    public async Task A_successful_notify_clears_a_previous_failure_but_does_not_rewrite_a_healthy_server()
    {
        var recovering = Server("recovering", MediaServerKinds.Plex, healthStatus: "unreachable");
        var repository = new FakeRepository([recovering], []);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var recorded = Assert.Single(repository.HealthRecords);
        Assert.True(recorded.Result.Ok);

        repository.HealthRecords.Clear();
        var healthy = Server("healthy", MediaServerKinds.Plex, healthStatus: "healthy");
        var repository2 = new FakeRepository([healthy], []);
        var notifier2 = new MediaServerRefreshNotifier(repository2, new FakeRegistry(client), TimeProvider.System);

        await notifier2.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Empty(repository2.HealthRecords);
    }

    [Fact]
    public async Task Telling_a_server_about_new_files_is_written_to_activity_with_the_server_and_the_file()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var activity = new FakeActivityFeed();
        var notifier = new MediaServerRefreshNotifier(
            new FakeRepository([server], []), new FakeRegistry(new FakeClient(MediaServerKinds.Plex)), TimeProvider.System,
            new FakeBacklogRepository(), new FakeJobScheduler(), activity);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var logged = Assert.Single(activity.Recorded);
        Assert.Equal(MediaServerRefreshActivity.Refreshed, logged.Category);
        Assert.Contains("1 new or changed file", logged.Message);
        Assert.Contains("/media/Movies/Sintel.mkv", logged.DetailsJson);
    }

    [Fact]
    public async Task No_changes_asks_nothing()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(new FakeRepository([server], []), new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [], CancellationToken.None);

        Assert.Empty(client.Calls);
    }

    // ---------------------------------------------------------------- #920

    [Fact]
    public async Task A_failed_notify_backs_off_and_schedules_its_own_retry()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var repository = new FakeRepository([down], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var backlogRepo = new FakeBacklogRepository();
        var jobs = new FakeJobScheduler();
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System, backlogRepo, jobs, new FakeActivityFeed());

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var backlog = Assert.Single(backlogRepo.All());
        Assert.Equal(1, backlog.AttemptCount);
        Assert.Single(backlog.Changes);
        var enqueued = Assert.Single(jobs.Enqueued);
        Assert.Equal(MediaServerJobTypes.RefreshRetry, enqueued.JobType);
        Assert.True(enqueued.ScheduledUtc > DateTimeOffset.UtcNow);
        Assert.Equal("media-server-refresh-retry:down:movies", enqueued.DedupeKey);
    }

    [Fact]
    public async Task Each_failed_attempt_backs_off_further_than_the_last()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var repository = new FakeRepository([down], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var backlogRepo = new FakeBacklogRepository();
        var jobs = new FakeJobScheduler();
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System, backlogRepo, jobs, new FakeActivityFeed());

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        var firstDelay = jobs.Enqueued[0].ScheduledUtc!.Value - DateTimeOffset.UtcNow;

        var stillDown = await notifier.RetryAsync("down", "movies", CancellationToken.None);
        var secondDelay = jobs.Enqueued[1].ScheduledUtc!.Value - DateTimeOffset.UtcNow;

        Assert.False(stillDown);
        Assert.Equal(2, Assert.Single(backlogRepo.All()).AttemptCount);
        Assert.True(secondDelay > firstDelay, $"expected {secondDelay} > {firstDelay}");
    }

    [Fact]
    public async Task A_new_change_while_a_server_is_down_folds_into_the_same_backlog_instead_of_a_second_call()
    {
        var down = Server("down", MediaServerKinds.Plex);
        var repository = new FakeRepository([down], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var backlogRepo = new FakeBacklogRepository();
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System, backlogRepo, new FakeJobScheduler(), new FakeActivityFeed());

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Big Buck Bunny.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var backlog = Assert.Single(backlogRepo.All());
        Assert.Equal(2, backlog.Changes.Count);
        Assert.Equal(2, client.Calls.Count);
        // Both calls sent everything owed so far, not just what was new that time.
        Assert.Single(client.Calls[0].Changes);
        Assert.Equal(2, client.Calls[1].Changes.Count);
    }

    [Fact]
    public async Task A_retry_that_succeeds_catches_up_everything_missed_in_one_call_and_clears_the_backlog()
    {
        var server = Server("s1", MediaServerKinds.Plex, healthStatus: "unreachable");
        var repository = new FakeRepository([server], []);
        var client = new FakeClient(MediaServerKinds.Plex, fail: true);
        var backlogRepo = new FakeBacklogRepository();
        var activity = new FakeActivityFeed();
        var notifier = new MediaServerRefreshNotifier(repository, new FakeRegistry(client), TimeProvider.System, backlogRepo, new FakeJobScheduler(), activity);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Big Buck Bunny.mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        client.Fail = false;

        var answered = await notifier.RetryAsync("s1", "movies", CancellationToken.None);

        Assert.True(answered);
        Assert.Empty(backlogRepo.All());
        Assert.Equal(2, client.Calls[^1].Changes.Count);
        var logged = Assert.Single(activity.Recorded, entry => entry.Category == MediaServerRefreshActivity.CaughtUp);
        Assert.Equal(MediaServerRefreshActivity.CaughtUp, logged.Category);
        Assert.Contains("2 changed files", logged.Message);
    }

    [Fact]
    public async Task A_retry_with_nothing_left_to_send_answers_true_without_calling_the_client()
    {
        var server = Server("s1", MediaServerKinds.Plex);
        var client = new FakeClient(MediaServerKinds.Plex);
        var notifier = new MediaServerRefreshNotifier(
            new FakeRepository([server], []), new FakeRegistry(client), TimeProvider.System,
            new FakeBacklogRepository(), new FakeJobScheduler(), new FakeActivityFeed());

        var answered = await notifier.RetryAsync("s1", "movies", CancellationToken.None);

        Assert.True(answered);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_disabled_server_and_a_server_of_a_kind_with_no_client_are_skipped_while_the_others_are_told()
    {
        var disabled = Server("off", MediaServerKinds.Jellyfin) with { IsEnabled = false };
        var odd = Server("odd", "kodi");
        var ok = Server("ok", MediaServerKinds.Jellyfin);
        var client = new FakeClient(MediaServerKinds.Jellyfin);
        var notifier = new MediaServerRefreshNotifier(new FakeRepository([disabled, odd, ok], []), new FakeRegistry(client), TimeProvider.System);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task A_retry_for_a_server_that_is_gone_switched_off_or_of_a_kind_with_no_client_drops_its_backlog_without_asking_anyone()
    {
        var disabled = Server("off", MediaServerKinds.Jellyfin) with { IsEnabled = false };
        var odd = Server("odd", "kodi");
        var client = new FakeClient(MediaServerKinds.Jellyfin);
        var backlog = new FakeBacklogRepository();
        foreach (var id in new[] { "gone", "off", "odd" })
        {
            await backlog.RecordFailureAsync(id, "movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);
        }

        var notifier = new MediaServerRefreshNotifier(new FakeRepository([disabled, odd], []), new FakeRegistry(client), TimeProvider.System, backlog, new FakeJobScheduler(), new FakeActivityFeed());

        Assert.True(await notifier.RetryAsync("gone", "movies", CancellationToken.None));
        Assert.True(await notifier.RetryAsync("off", "movies", CancellationToken.None));
        Assert.True(await notifier.RetryAsync("odd", "movies", CancellationToken.None));

        Assert.Empty(backlog.All());
        Assert.Empty(client.Calls);
    }

    private sealed class FakeBacklogRepository : IMediaServerRefreshBacklogRepository
    {
        private readonly Dictionary<(string ServerId, string MediaType), MediaServerRefreshBacklogItem> _items = [];

        public IReadOnlyList<MediaServerRefreshBacklogItem> All() => _items.Values.ToArray();

        public Task<MediaServerRefreshBacklogItem?> FindAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken)
            => Task.FromResult(_items.GetValueOrDefault((mediaServerId, mediaType)));

        public Task<IReadOnlyList<MediaServerRefreshBacklogItem>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MediaServerRefreshBacklogItem>>(_items.Values.ToArray());

        public Task<MediaServerRefreshBacklogItem> RecordFailureAsync(string mediaServerId, string mediaType, IReadOnlyList<MediaServerPathChange> pendingChanges, CancellationToken cancellationToken)
        {
            var key = (mediaServerId, mediaType);
            var existing = _items.GetValueOrDefault(key);
            var attempt = (existing?.AttemptCount ?? 0) + 1;
            var now = DateTimeOffset.UtcNow;
            var item = new MediaServerRefreshBacklogItem(mediaServerId, mediaType, existing?.FirstFailedUtc ?? now, now + MediaServerRefreshBackoff.Next(attempt), attempt, pendingChanges, now);
            _items[key] = item;
            return Task.FromResult(item);
        }

        public Task ClearAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken)
        {
            _items.Remove((mediaServerId, mediaType));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJobScheduler : IJobScheduler
    {
        public List<EnqueueJobRequest> Enqueued { get; } = [];

        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
        {
            Enqueued.Add(request);
            return Task.FromResult(new JobQueueItem(
                Guid.NewGuid().ToString("N"), request.JobType, request.Source, "pending", request.PayloadJson, 0,
                DateTimeOffset.UtcNow, request.ScheduledUtc ?? DateTimeOffset.UtcNow, null, null, null, null, null,
                request.RelatedEntityType, request.RelatedEntityId, request.IdempotencyKey, request.DedupeKey,
                request.MaxAttempts ?? 3, null, null));
        }
    }

    private sealed class FakeActivityFeed : IActivityFeedRepository
    {
        public List<(string Category, string Message, string? DetailsJson)> Recorded { get; } = [];

        public Task<int> PruneActivityAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> ClearActivityAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityStatsSnapshot> GetActivityStatsAsync(DateTimeOffset sinceUtc, int windowHours, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(int take, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Page<ActivityEventItem>> ListActivityPageAsync(PageRequest request, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ActivityEventItem> RecordActivityAsync(string category, string message, string? detailsJson, string? relatedJobId, string? relatedEntityType, string? relatedEntityId, CancellationToken cancellationToken)
        {
            Recorded.Add((category, message, detailsJson));
            return Task.FromResult(new ActivityEventItem(Guid.NewGuid().ToString("N"), category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(IReadOnlyList<ActivityEventItem> activities, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed record RefreshCall(string MediaType, IReadOnlyList<MediaServerPathChange> Changes);

    /// <summary>#1098: the Live screen lights a media server's tile when it answers a refresh.</summary>
    [Fact]
    public async Task A_refresh_the_server_answered_is_reported_as_a_use_of_that_server_and_a_failed_one_is_not()
    {
        var up = Server("up", MediaServerKinds.Jellyfin);
        var down = Server("down", MediaServerKinds.Plex);
        var registry = new MultiRegistry(new()
        {
            [MediaServerKinds.Plex] = new FakeClient(MediaServerKinds.Plex, fail: true),
            [MediaServerKinds.Jellyfin] = new FakeClient(MediaServerKinds.Jellyfin)
        });
        var uses = new RecordingUses();
        var notifier = new MediaServerRefreshNotifier(new FakeRepository([down, up], []), registry, TimeProvider.System, connectionUses: uses);

        await notifier.NotifyAsync("movies", [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        var reported = Assert.Single(uses.Reported);
        Assert.Equal((ConnectionKinds.MediaServer, "up", "Server up"), reported);
    }

    private sealed class RecordingUses : IConnectionUseReporter
    {
        public List<(string Kind, string Id, string Name)> Reported { get; } = [];

        public Task ReportAsync(string kind, string id, string name, CancellationToken cancellationToken)
        {
            Reported.Add((kind, id, name));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient(string kind, bool fail = false) : IMediaServerClient
    {
        public string Kind => kind;
        public List<RefreshCall> Calls { get; } = [];
        public bool Fail { get; set; } = fail;

        public Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(MediaServerConnection connection, string mediaType, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(MediaServerConnection connection, MediaServerItem item, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerRefresh>> RefreshAsync(
            MediaServerConnection connection, string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken)
        {
            Calls.Add(new RefreshCall(mediaType, changes));
            return Task.FromResult<MediaServerAnswer<MediaServerRefresh>>(Fail
                ? new MediaServerAnswer<MediaServerRefresh>.Unreachable($"{connection.Name} is down.")
                : new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh("ok", false)));
        }

        public Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(MediaServerConnection connection, string serverPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(MediaServerConnection connection, MediaServerCollectionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeRegistry(IMediaServerClient client) : IMediaServerClientRegistry
    {
        public IMediaServerClient? Find(string kind) => string.Equals(client.Kind, kind, StringComparison.OrdinalIgnoreCase) ? client : null;
    }

    private sealed class MultiRegistry(Dictionary<string, IMediaServerClient> clients) : IMediaServerClientRegistry
    {
        public IMediaServerClient? Find(string kind) => clients.GetValueOrDefault(kind);
    }

    private sealed class FakeRepository(IReadOnlyList<MediaServerConnection> servers, IReadOnlyList<MediaServerPathMappingItem> mappings) : IMediaServerRepository
    {
        public List<(string Id, MediaServerTestResult Result)> HealthRecords { get; } = [];

        public Task<IReadOnlyList<MediaServerConnection>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(servers);

        public Task<MediaServerConnection?> GetAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(servers.FirstOrDefault(server => server.Id == id));

        public Task<MediaServerConnection?> SaveAsync(string? id, SaveMediaServerRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RecordHealthAsync(string id, MediaServerTestResult result, CancellationToken cancellationToken)
        {
            HealthRecords.Add((id, result));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaServerPathMappingItem>> ListPathMappingsAsync(string? mediaServerId, CancellationToken cancellationToken)
            => Task.FromResult(mediaServerId is null ? mappings : (IReadOnlyList<MediaServerPathMappingItem>)mappings.Where(mapping => mapping.MediaServerId == mediaServerId).ToArray());

        public Task<MediaServerPathMappingItem?> CreatePathMappingAsync(string mediaServerId, CreateMediaServerPathMappingRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> DeletePathMappingAsync(string mediaServerId, string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
