using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
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
/// #1470: a call the client refused after a grab that can stop the import (the category or the save folder) is kept in the
/// dispatch's notes and said on the download's row, in plain words with the fix, until a later grab has the same call taken.
/// </summary>
public sealed class AFollowUpTheClientRefusedShowsOnTheDownloadsRowTests
{
    private const string Release = "Arrival.2016.WEB.1080p-DELUNO";

    private static readonly DownloadClientFollowUpRefusal Category = new("setCategory", "the movies category", true, "Incorrect category name");

    [Fact]
    public async Task A_refused_category_is_said_on_the_row_with_what_to_do_about_it()
    {
        using var world = await World.CreateAsync();
        await world.Dispatches.SetFollowUpRefusalsAsync(world.DispatchId, FollowUpRefusalNotes.ToJson([Category], world.ClientName), CancellationToken.None);

        var overview = await world.PollAsync();

        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        var finding = Assert.Single(item.HealthFindings!, finding => finding.Kind == "follow-up-refused");
        Assert.Equal($"{world.ClientName} did not take the movies category: Incorrect category name.", finding.Summary);
        Assert.Equal($"Create the category in {world.ClientName} or change the library's category, then send it again.", finding.RecommendedAction);
        Assert.Equal(0, finding.StrikeCount);
        Assert.False(finding.CandidateBlocked);
    }

    [Fact]
    public async Task A_refused_save_folder_gets_the_folder_fix()
    {
        using var world = await World.CreateAsync();
        var folder = new DownloadClientFollowUpRefusal("setLocation", "the save folder /downloads/movies", true, "Cannot make save path");
        await world.Dispatches.SetFollowUpRefusalsAsync(world.DispatchId, FollowUpRefusalNotes.ToJson([folder], world.ClientName), CancellationToken.None);

        var overview = await world.PollAsync();

        var finding = Assert.Single(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!, finding => finding.Kind == "follow-up-refused");
        Assert.Contains("did not take the save folder /downloads/movies: Cannot make save path.", finding.Summary, StringComparison.Ordinal);
        Assert.Contains("change the library's downloads folder, then send it again", finding.RecommendedAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_grab_that_has_the_call_taken_clears_it_from_the_row()
    {
        using var world = await World.CreateAsync();
        await world.Dispatches.SetFollowUpRefusalsAsync(world.DispatchId, FollowUpRefusalNotes.ToJson([Category], world.ClientName), CancellationToken.None);
        await world.Dispatches.SetFollowUpRefusalsAsync(world.DispatchId, null, CancellationToken.None);

        var overview = await world.PollAsync();

        Assert.DoesNotContain(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings ?? [], finding => finding.Kind == "follow-up-refused");
        Assert.DoesNotContain("followUpRefusals", (await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None))!.NotesJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_that_cannot_stop_the_import_is_not_kept()
    {
        var shareLimits = new DownloadClientFollowUpRefusal("setShareLimits", "the share limits", false, "Missing required parameters");

        Assert.Null(FollowUpRefusalNotes.ToJson([shareLimits], "qBittorrent"));
        Assert.Empty(FollowUpRefusalNotes.FindingsFrom("not json", "qBittorrent"));
        Assert.Empty(FollowUpRefusalNotes.FindingsFrom("{\"other\":1}", "qBittorrent"));
    }

    [Fact]
    public async Task A_dispatch_recorded_with_the_grabs_refusals_says_them_on_the_row_from_the_first_look()
    {
        using var world = await World.CreateAsync(refusalsJson: FollowUpRefusalNotes.ToJson([Category], "SABnzbd"), notesJson: "{\"candidates\":[]}");

        var overview = await world.PollAsync();

        var finding = Assert.Single(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!, finding => finding.Kind == "follow-up-refused");
        Assert.Equal($"{world.ClientName} did not take the movies category: Incorrect category name.", finding.Summary);
        var notes = (await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None))!.NotesJson!;
        Assert.Contains("\"candidates\"", notes, StringComparison.Ordinal);
        Assert.Contains("followUpRefusals", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dispatch_recorded_with_nothing_refused_has_no_refusal_in_its_notes()
    {
        using var world = await World.CreateAsync(notesJson: "{\"candidates\":[]}");

        Assert.DoesNotContain("followUpRefusals", (await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None))!.NotesJson ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class QueueClient(string clientId, string clientName, Func<DownloadQueueItem> queueItemFactory, IReadOnlyList<string>? clientWarnings = null) : IDownloadClient
    {
        public string Protocol => "sabnzbd";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, false, true, "api-key");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                clientId, clientName, "sabnzbd", "http://127.0.0.1:8080", "healthy", null,
                Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [queueItemFactory()],
                [],
                capturedUtc)
            {
                ClientWarnings = clientWarnings
            });

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "downloading";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;

        private World(TestStorage storage, DownloadClientTelemetryService service, string clientName, SqliteDownloadDispatchesRepository dispatches, string dispatchId)
        {
            Dispatches = dispatches;
            DispatchId = dispatchId;
            _storage = storage;
            _service = service;
            ClientName = clientName;
        }

        public string ClientName { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public string DispatchId { get; }

        public static async Task<World> CreateAsync(IReadOnlyList<string>? clientWarnings = null, string? refusalsJson = null, string? notesJson = null)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
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
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            // #933: the Queue only shows downloads Deluno recorded dispatching.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", notesJson, cancellationToken: CancellationToken.None, followUpRefusalsJson: refusalsJson);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 2_000_000_000, completed: false, CancellationToken.None);

            var fakeClient = new QueueClient(client.Id, client.Name, () => new DownloadQueueItem(
                "queue-arrival", client.Id, client.Name, "sabnzbd", "movies", "Arrival", Release,
                "deluno-movies", DownloadQueueStatuses.Downloading, 42, 5, 600, 2_000_000_000, 840_000_000, 0, "Fixture", null,
                time.GetUtcNow(), LibraryId: library.Id));
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);

            return new World(storage, service, client.Name, dispatches, dispatchId);
        }

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
