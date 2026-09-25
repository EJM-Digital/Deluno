using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #653, #646: a season-pack dispatch's <c>EntityId</c> is the series, not any
/// one episode — the whole season shares one dispatch because the download
/// itself belongs to no single episode. Targeting the replacement search at
/// the series id made the retry job <c>IsTargeted</c>, which lifted the retry
/// window for every missing episode across every season of the series, not
/// just the one that failed. Proven live on Pioneer One: the season's grab
/// (a single episode, since no pack existed — see #653's other fix) failed,
/// the replacement search it queued searched the whole series again, grabbed
/// the next single episode it found, that failed the same way, and so on —
/// one grab per season inside a minute. The replacement search now targets
/// exactly the episodes <see cref="Deluno.Series.Services.SeasonSearchExecutor"/>
/// recorded the dispatch as covering.
/// </summary>
public sealed class AFailedSeasonPackRetriesOnlyItsEpisodesTests
{
    private const string Release = "Pioneer.One.S01.1080p.WEB-DL";

    [Fact]
    public async Task A_failed_season_dispatch_queues_one_retry_per_episode_it_covered_not_the_whole_series()
    {
        using var world = await World.CreateAsync(["episode-1", "episode-2", "episode-3"]);

        await world.ShowHistoryAsync(NzbGetHistory());

        var jobs = await world.Jobs.ListAsync(10, CancellationToken.None);
        var targets = jobs
            .Select(job => JsonDocument.Parse(job.PayloadJson!).RootElement.GetProperty("targetEntityId").GetString())
            .ToArray();

        Assert.Equal(["episode-1", "episode-2", "episode-3"], targets.OrderBy(id => id, StringComparer.Ordinal));
        // Never the series id itself — that is exactly what lifted the retry
        // window for the whole series.
        Assert.DoesNotContain("series-pioneer-one", targets);
    }

    [Fact]
    public async Task A_failed_non_season_dispatch_still_retries_its_own_entity()
    {
        using var world = await World.CreateAsync(episodeIds: null, entityType: "episode", entityId: "episode-1");

        await world.ShowHistoryAsync(NzbGetHistory());

        var job = Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
        var target = JsonDocument.Parse(job.PayloadJson!).RootElement.GetProperty("targetEntityId").GetString();
        Assert.Equal("episode-1", target);
    }

    private static DownloadClientHistoryItem NzbGetHistory()
    {
        const string status = "FAILURE/HEALTH";
        var outcome = NzbGetDownloadClient.NormalizeOutcome(status);
        var errorMessage = NzbGetDownloadClient.FailureReason(status);
        return new DownloadClientHistoryItem(
            Id: "331", ClientId: "client-1", ClientName: "NZBGet", Protocol: "nzbget",
            MediaType: "tv", Title: "Pioneer One", ReleaseName: Release, Category: "tv",
            Outcome: outcome, IndexerName: "NZBGeek", SizeBytes: 2_000_000_000,
            CompletedUtc: DateTimeOffset.Parse("2026-09-25T04:55:00Z"), ErrorMessage: errorMessage,
            HistorySource: "native", ExternalId: "331");
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;
        private readonly string _dispatchId;

        private World(
            TestStorage storage,
            DateTimeOffset now,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            string dispatchId,
            SqliteJobStore jobs,
            SqliteDownloadDispatchesRepository dispatches)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            _dispatchId = dispatchId;
            Jobs = jobs;
            Dispatches = dispatches;
        }

        public DateTimeOffset Now { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public static async Task<World> CreateAsync(
            IReadOnlyList<string>? episodeIds,
            string entityType = "series",
            string entityId = "series-pioneer-one")
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-25T04:55:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "TV", "tv", "library", Path.Combine(storage.DataRoot, "tv"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "NZBGet", "nzbget", "localhost", 6789, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            // The season search's own dispatch notes — the shape
            // SeasonSearchExecutor.ExecuteAsync serializes.
            var notesJson = episodeIds is null
                ? null
                : JsonSerializer.Serialize(new { seasonNumber = 1, episodeIds });
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "tv", entityType, entityId, Release, "NZBGeek", client.Id, client.Name, "sent", notesJson,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "331", null, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, blocklist);
            return new World(storage, now, service, client, dispatchId, jobs, dispatches);
        }

        public Task ShowHistoryAsync(DownloadClientHistoryItem history)
        {
            var snapshot = new DownloadClientTelemetrySnapshot(
                _client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                Queue: [],
                History: [history with { ClientId = _client.Id, ClientName = _client.Name }],
                Now);
            return RecordAsync(snapshot);
        }

        public async Task RecordAsync(DownloadClientTelemetrySnapshot snapshot)
        {
            var clientDispatches = new[] { (await Dispatches.GetDispatchAsync(_dispatchId, CancellationToken.None))! };
            await _service.RecordWhatTheClientShowsAsync(snapshot, clientDispatches, CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
