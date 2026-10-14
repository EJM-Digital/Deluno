using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1312: with the content check on, the telemetry pass asks each client for a download's file list. One client failing that
/// call (a wrong Deluge password threw out of it) must not break the pass for the others: its own downloads are still shown, the
/// other client's are shown too, and the pass does not throw.
/// </summary>
public sealed class OneClientsFileListFailingDoesNotBreakTheTelemetryPassTests
{
    public static IEnumerable<object[]> Failures() =>
    [
        [new InvalidOperationException("Deluge refused the password.")],
        [new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).")],
        [new IOException("The connection was reset.")],
        [new NotSupportedException("An unexpected failure.")]
    ];

    [Theory, MemberData(nameof(Failures))]
    public async Task A_client_whose_file_list_throws_leaves_the_pass_and_the_other_client_alone(Exception failure)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var secretProtection = TestSecretProtection.Create(storage);
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, secretProtection);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, secretProtection);
        var libraries = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var broken = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Deluge", "deluge", "localhost", 8112, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
        var working = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 2, true), CancellationToken.None);

        var health = new SqliteDownloadHealthRepository(storage.Factory, time);
        await health.SaveContentBlocklistSettingsAsync(new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false, CustomPatterns: ["*.exe"],
            RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: time.GetUtcNow()), CancellationToken.None);

        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        foreach (var (client, release, id) in new[] { (broken, "Broken.Client.Release.2026", "queue-broken"), (working, "Working.Client.Release.2026", "queue-working") })
        {
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-" + id, release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, id, 4_000_000_000, completed: false, CancellationToken.None);
        }

        var registry = new DownloadClientRegistry([
            new FileListClient("deluge", broken, "Broken.Client.Release.2026", "queue-broken", library.Id, time, failure),
            new FileListClient("qbittorrent", working, "Working.Client.Release.2026", "queue-working", library.Id, time, null)]);
        var service = new DownloadClientTelemetryService(
            settings, health, libraries, connections, jobs, registry, time,
            new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance),
            jobs, dispatches, jobs, new DownloadHealthEvaluator(), new SqliteProcessorRepository(storage.Factory, time, secretProtection),
            NullConnectivityMonitor.Instance, new SqliteBlockedReleaseRepository(storage.Factory, time));

        var overview = await service.GetOverviewAsync(CancellationToken.None);

        Assert.Equal(2, overview.Clients.Count);
        Assert.Equal("queue-broken", Assert.Single(overview.Clients.Single(c => c.ClientId == broken.Id).Queue).Id);
        Assert.Equal("queue-working", Assert.Single(overview.Clients.Single(c => c.ClientId == working.Id).Queue).Id);
    }

    private sealed class FileListClient(string protocol, DownloadClientItem owner, string release, string queueId, string libraryId, TimeProvider time, Exception? filesFailure) : IDownloadClient
    {
        public string Protocol => protocol;

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                owner.Id, owner.Name, protocol, "http://127.0.0.1:8080", "healthy", null, Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [new DownloadQueueItem(queueId, owner.Id, owner.Name, protocol, "movies", release, release, "deluno-movies",
                    DownloadQueueStatuses.Downloading, 40d, 0, 0, 4_000_000_000, 1_600_000_000, 3, "Fixture", null,
                    time.GetUtcNow(), LibraryId: libraryId)],
                [], capturedUtc));

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => filesFailure is null ? Task.FromResult<IReadOnlyList<string>?>(["Movie.mkv"]) : throw filesFailure;

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, "ok"));

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
