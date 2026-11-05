using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// A download client that answers from a script and a clock a test moves: the queue it lists is whatever the test set, and a
/// removal asked of it takes the entry out of that queue, as a real client does. Every action asked of it is kept.
/// </summary>
internal sealed class ScriptedDownloadClient(string protocol) : IDownloadClient
{
    public string Protocol => protocol;

    public List<DownloadQueueItem> Queue { get; } = [];

    public bool? AlternateSpeedLimit { get; set; }

    public List<(string Action, string ItemId)> Actions { get; } = [];

    public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, protocol);

    public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var queue = Queue.Select(item => item with { ClientId = client.Id, ClientName = client.Name }).ToArray();
        return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
            client.Id, client.Name, client.Protocol, null, "healthy", null, Capabilities,
            new DownloadTelemetrySummary(0, 0, 0, queue.Length, 0, 0, 0), queue, [], capturedUtc)
        {
            AlternateSpeedLimitEnabled = AlternateSpeedLimit
        });
    }

    public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        Actions.Add((action, queueItemId));
        if (action is DownloadClientActions.DeleteWithData or DownloadClientActions.Delete or DownloadClientActions.RemoveRefusedCopy)
        {
            Queue.RemoveAll(item => item.Id == queueItemId);
        }

        return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));
    }

    public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

    public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        => nativeStatus ?? "unknown";
}

/// <summary>
/// A real client adapter reading its recorded replies, with every action asked of it kept instead of sent: the recording has one
/// state of the client, so a removal cannot change what it answers next; a test that needs the client to move on between checks
/// hands over a new reading each time.
/// </summary>
internal sealed class RecordedClientThatKeepsItsActions(Func<IDownloadClient> current) : IDownloadClient
{
    public IDownloadClient Inner => current();

    public string Protocol => Inner.Protocol;

    public List<(string Action, string ItemId)> Actions { get; } = [];

    public DownloadClientTelemetryCapabilities Capabilities => Inner.Capabilities;

    public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        => Inner.GetSnapshotAsync(client, capturedUtc, cancellationToken);

    public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        Actions.Add((action, queueItemId));
        return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));
    }

    public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

    public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        => Inner.NormalizeStatus(nativeStatus, progress, errorCode, errorMessage);
}

/// <summary>
/// The real strike pass, as fast as a unit test: <see cref="DownloadClientTelemetryService"/> reads a client, the real evaluator and the
/// real download-health repository on a temporary database count the strikes, and the removal, the blocklist, Activity and the
/// replacement search are the real ones. Only the client is a fake and the clock is moved by the test, so each check is one call to
/// <see cref="CheckAsync"/> and a run that would take hours on a real torrent takes milliseconds.
/// </summary>
internal sealed class AStrikePass : IDisposable
{
    public const string Release = "Example.Movie.2026.1080p.WEB-DL-GROUP";
    public const string ItemId = "queue-movie";
    public const string EntityId = "movie-example";
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-05T10:00:00Z");

    private readonly TestStorage _storage;
    private int _checks;

    private AStrikePass(
        TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service, DownloadClientItem client,
        SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist, SqliteDownloadDispatchesRepository dispatches,
        SqliteDownloadHealthRepository health, string dispatchId, string payloadPath, TimeSpan window, DownloadHealthRule rule)
    {
        _storage = storage;
        Time = time;
        Service = service;
        Client = client;
        Jobs = jobs;
        Blocklist = blocklist;
        Dispatches = dispatches;
        Health = health;
        DispatchId = dispatchId;
        PayloadPath = payloadPath;
        Window = window;
        Rule = rule;
    }

    public ASchedulerThatWatchesTheSearchBeingQueued Watcher { get; private init; } = null!;

    public FixedTimeProvider Time { get; }
    public DownloadClientTelemetryService Service { get; }
    public DownloadClientItem Client { get; }
    public SqliteJobStore Jobs { get; }
    public SqliteBlockedReleaseRepository Blocklist { get; }
    public SqliteDownloadDispatchesRepository Dispatches { get; }
    public SqliteDownloadHealthRepository Health { get; }
    public string DispatchId { get; }
    public string ReleaseName { get; private init; } = Release;
    public string QueueItemId { get; private init; } = ItemId;
    public string PayloadPath { get; }

    /// <summary>One "check every" interval: the time between two strikes.</summary>
    public TimeSpan Window { get; }

    public DownloadHealthRule Rule { get; }

    /// <summary>What one check showed: the overview the client's reading became, and what the remediation pass did with it.</summary>
    public sealed record Check(DownloadTelemetryOverview Overview, DownloadHealthRemediationReport Report)
    {
        public DownloadQueueItem? Item => Overview.Clients.SelectMany(client => client.Queue).FirstOrDefault();

        public int Strikes(string kind)
            => Item?.HealthFindings?.FirstOrDefault(finding => finding.Kind == kind)?.StrikeCount ?? 0;
    }

    public static async Task<AStrikePass> CreateAsync(
        DownloadHealthRule rule,
        IDownloadClient fake,
        string release = Release,
        string itemId = ItemId,
        string? category = null,
        string protocol = "qbittorrent",
        string entityType = "movie",
        string? username = null,
        string? secret = null)
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var platform = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var libraries = new SqliteLibrariesRepository(storage.Factory, time);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), downloadsPath, null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        Directory.CreateDirectory(downloadsPath);
        var payloadPath = Path.Combine(downloadsPath, "Example.Movie.2026.mkv");
        await File.WriteAllTextAsync(payloadPath, "fixture payload");

        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Example client", protocol, "localhost", 8080, username, secret, null, category ?? "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", entityType, EntityId, release, "Example indexer", client.Id, client.Name, "sent", null, grabResponseCode: 200,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, itemId, 1024, completed: false, CancellationToken.None);
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
        var health = new SqliteDownloadHealthRepository(storage.Factory, time);

        var settings = await platform.GetAsync(CancellationToken.None);
        var saved = await health.SaveDownloadHealthRuleAsync(rule, settings, CancellationToken.None);
        Assert.Null(saved.Error);
        var window = TimeSpan.FromMinutes(DownloadHealthBounds.ClampMinutes(settings.DownloadHealthDefaultStrikeWindowMinutes));

        var watcher = new ASchedulerThatWatchesTheSearchBeingQueued(jobs);
        var service = new DownloadClientTelemetryService(
            platform, health, libraries, connections, jobs,
            new DownloadClientRegistry([fake]), time, new PassThroughPolicy(),
            watcher, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage)),
            Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);
        return new AStrikePass(storage, time, service, client, jobs, blocklist, dispatches, health, dispatchId, payloadPath, window, rule) { Watcher = watcher, ReleaseName = release, QueueItemId = itemId };
    }

    /// <summary>
    /// One check of the client: the clock moves one "check every" interval (the first check happens at once), the client is read, the
    /// strikes are counted and, when a download has reached its limit, it is dealt with.
    /// </summary>
    public async Task<Check> CheckAsync(TimeSpan? after = null)
    {
        Time.Advance(after ?? (_checks == 0 ? TimeSpan.Zero : Window + TimeSpan.FromSeconds(1)));
        _checks++;
        var overview = await Service.GetOverviewAsync(CancellationToken.None);
        var report = await Service.RunConfiguredHealthRemediationAsync(overview, CancellationToken.None);
        return new Check(overview, report);
    }

    public async Task<DownloadHealthRecord?> RecordOfAsync(string kind)
        => (await Health.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).FirstOrDefault(record => record.Kind == kind);

    public async Task<IReadOnlyList<JobQueueItem>> JobsQueuedAsync() => await Jobs.ListAsync(10, CancellationToken.None);

    public void Dispose() => _storage.Dispose();

    private sealed class PassThroughPolicy : IIntegrationResiliencePolicy
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
}

/// <summary>
/// Passes every job on to the real store, and notes what the world looked like at the moment a replacement search was queued: the
/// search can start before the download it replaces is removed and archived (#1482), so what it would have counted as "on its way"
/// then is what matters.
/// </summary>
internal sealed class ASchedulerThatWatchesTheSearchBeingQueued(SqliteJobStore jobs) : IJobScheduler
{
    /// <summary>The downloads the store held in flight when the search was queued; null until one was.</summary>
    public IReadOnlyList<DownloadInFlight>? InFlightWhenQueued { get; private set; }

    /// <summary>What a search for another release of the title was told at that moment, ignoring the dispatch it replaces.</summary>
    public string? HeldBackIgnoringTheReplacedDownload { get; private set; }

    /// <summary>The same, not ignoring it: the answer the search would have got before #1482.</summary>
    public string? HeldBackNotIgnoringIt { get; private set; }

    public async Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
    {
        InFlightWhenQueued = await jobs.ListDownloadsInFlightAsync(100, cancellationToken);
        HeldBackIgnoringTheReplacedDownload = await DispatchCoverage.AlreadyOnItsWayAsync(
            jobs, "Another.Release.2026", [AStrikePass.EntityId], cancellationToken, request.RelatedEntityId);
        HeldBackNotIgnoringIt = await DispatchCoverage.AlreadyOnItsWayAsync(
            jobs, "Another.Release.2026", [AStrikePass.EntityId], cancellationToken);
        return await jobs.EnqueueAsync(request, cancellationToken);
    }
}
