using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// The Dashboard's client "Resume" button resumes the client as a whole (the
/// pause SABnzbd and NZBGet report as <c>ClientPaused</c>), not one item in its
/// queue. Asking says what happened in Activity in plain words, and a client
/// with no pause of its own, or one that would not resume, says so and writes
/// nothing.
/// </summary>
public sealed class ResumingAWholeDownloadClientTests
{
    [Fact]
    public async Task Resuming_a_paused_client_asks_it_and_says_so_in_Activity()
    {
        using var world = await World.CreateAsync(new ClientResumeResult(true, true, "SABnzbd is downloading again."));

        var result = await world.Service.ResumeClientAsync(world.ClientId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, world.Fake.ResumeCalls);
        var activity = await world.Jobs.ListActivityAsync(20, "download_client", world.ClientId, CancellationToken.None);
        var entry = Assert.Single(activity, item => item.Category == "download.client.resumed");
        Assert.Contains("Resumed SABnzbd", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_with_no_pause_of_its_own_says_so_and_writes_nothing()
    {
        using var world = await World.CreateAsync(ClientResumeResult.NotSupported);

        var result = await world.Service.ResumeClientAsync(world.ClientId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("nothing to resume", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await world.Jobs.ListActivityAsync(20, "download_client", world.ClientId, CancellationToken.None),
            item => item.Category == "download.client.resumed");
    }

    [Fact]
    public async Task A_client_that_would_not_resume_gives_its_reason_and_writes_nothing()
    {
        using var world = await World.CreateAsync(new ClientResumeResult(true, false, "SABnzbd would not resume: locked."));

        var result = await world.Service.ResumeClientAsync(world.ClientId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("SABnzbd would not resume: locked.", result.Message);
        Assert.DoesNotContain(
            await world.Jobs.ListActivityAsync(20, "download_client", world.ClientId, CancellationToken.None),
            item => item.Category == "download.client.resumed");
    }

    [Fact]
    public async Task A_client_that_cannot_be_reached_is_reported_not_thrown()
    {
        using var world = await World.CreateAsync(throws: true);

        var result = await world.Service.ResumeClientAsync(world.ClientId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("could not reach", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_is_gone_says_so()
    {
        using var world = await World.CreateAsync(ClientResumeResult.NotSupported);

        var result = await world.Service.ResumeClientAsync("no-such-client", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Download client was not found.", result.Message);
    }

    private sealed class ResumableClient(ClientResumeResult outcome, bool throws) : DownloadClientBase
    {
        public int ResumeCalls { get; private set; }

        public override string Protocol => "sabnzbd";

        public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public override Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(null);

        public override Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";

        public override Task<ClientResumeResult> ResumeClientAsync(DownloadClientItem client, CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return throws ? throw new HttpRequestException("refused") : Task.FromResult(outcome);
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage, DownloadClientTelemetryService service, ResumableClient fake, DownloadClientItem client, SqliteJobStore jobs)
        {
            _storage = storage;
            Service = service;
            Fake = fake;
            ClientId = client.Id;
            Jobs = jobs;
        }

        public DownloadClientTelemetryService Service { get; }

        public ResumableClient Fake { get; }

        public string ClientId { get; }

        public SqliteJobStore Jobs { get; }

        public static async Task<World> CreateAsync(ClientResumeResult? outcome = null, bool throws = false)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T09:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var secretProtection = TestSecretProtection.Create(storage);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, secretProtection);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, secretProtection);
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "SABnzbd", "sabnzbd", "localhost", 8080, null, "an-api-key", null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            var fake = new ResumableClient(outcome ?? ClientResumeResult.NotSupported, throws);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, new DownloadClientRegistry([fake]), time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance, blocklist);
            return new World(storage, service, fake, client, jobs);
        }

        public void Dispose() => _storage.Dispose();
    }
}
