using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1062: Deluno writes its own record of a grab, with the release's known
/// hash, BEFORE the release is sent, so a second Deluno grab of the same
/// release (a film in two libraries, two search jobs at once) sees it and
/// follows the copy instead of blocklisting a release Deluno itself sent. A
/// failed send removes the reservation.
/// </summary>
public sealed class AGrabIsReservedBeforeItIsSentTests
{
    private const string Hash = "de4158016fbc49ebf35641903cec65a0ad0d30b5";
    private const string Release = "Arrival.2016.1080p.WEB";

    [Fact]
    public async Task A_reservation_is_seen_by_another_grab_but_never_by_the_grab_that_made_it()
    {
        using var world = await World.CreateAsync();

        var first = await world.Dispatches.ReserveGrabAsync(world.ClientId, Hash.ToUpperInvariant(), Release, CancellationToken.None);

        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, first, CancellationToken.None));
        Assert.True(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, "some-other-grab", CancellationToken.None));
        Assert.True(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash.ToUpperInvariant(), null, CancellationToken.None));
        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync("another-client", Hash, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_released_reservation_is_gone_and_old_ones_are_swept()
    {
        using var world = await World.CreateAsync();
        var released = await world.Dispatches.ReserveGrabAsync(world.ClientId, Hash, Release, CancellationToken.None);
        await world.Dispatches.ReleaseGrabReservationAsync(released, CancellationToken.None);
        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, null, CancellationToken.None));

        await world.Dispatches.ReserveGrabAsync(world.ClientId, Hash, Release, CancellationToken.None);
        world.Time.Advance(TimeSpan.FromDays(3));
        await world.Dispatches.ReserveGrabAsync(world.ClientId, new string('b', 40), "Other.Release", CancellationToken.None);

        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, null, CancellationToken.None));
    }

    [Fact]
    public async Task Two_grabs_of_the_same_release_at_once_the_second_follows_the_first_and_is_never_blocklisted()
    {
        using var world = await World.CreateAsync();
        var firstIsSending = new TaskCompletionSource();
        var letFirstFinish = new TaskCompletionSource();
        var clientCalls = 0;
        world.ClientBehaviour = async (client, request) =>
        {
            if (Interlocked.Increment(ref clientCalls) == 1)
            {
                // The first grab is still being sent when the second one starts.
                firstIsSending.SetResult();
                await letFirstFinish.Task;
                return new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.") { ExternalId = Hash };
            }

            // The client already has it: the first grab put it there.
            return new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "qBittorrent already holds this release.")
            {
                ExternalId = Hash,
                IsDuplicateFollow = true
            };
        };

        var first = world.GrabAsync();
        await firstIsSending.Task;
        var second = await world.GrabAsync();
        letFirstFinish.SetResult();
        var firstResult = await first;

        Assert.True(second.Succeeded, second.Message);
        Assert.True(second.IsDuplicateFollow);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, second.Status);
        Assert.True(firstResult.Succeeded);
    }

    [Fact]
    public async Task A_client_that_has_the_release_from_elsewhere_with_no_reservation_still_fails_it()
    {
        using var world = await World.CreateAsync();
        world.ClientBehaviour = (client, request) => Task.FromResult(
            new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "qBittorrent already holds this release.")
            {
                ExternalId = Hash,
                IsDuplicateFollow = true
            });

        var result = await world.GrabAsync();

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("already has this one from elsewhere", result.Message, StringComparison.Ordinal);
        // And its own reservation is removed: nothing of Deluno's was sent.
        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_send_removes_the_reservation_and_a_good_one_keeps_it()
    {
        using var world = await World.CreateAsync();
        world.ClientBehaviour = (client, request) => Task.FromResult(
            new DownloadClientGrabResult(client.Id, request.ReleaseName, false, "failed", "Client is down."));
        await world.GrabAsync();
        Assert.False(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, null, CancellationToken.None));

        world.ClientBehaviour = (client, request) => Task.FromResult(
            new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.") { ExternalId = Hash });
        await world.GrabAsync();
        Assert.True(await world.Dispatches.HasOtherGrabReservationAsync(world.ClientId, Hash, null, CancellationToken.None));
    }

    [Fact]
    public async Task Asking_who_owns_an_id_uses_the_index_on_client_and_id_ignoring_case()
    {
        using var world = await World.CreateAsync();
        await using var connection = await world.Storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT id FROM download_dispatches
            WHERE download_client_id = 'c' AND torrent_hash_or_item_id COLLATE NOCASE IN ('abc', 'def')
            """;

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, line => line.Contains("ix_download_dispatches_client_item_id", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.Ordinal));
    }

    private sealed class World : IDisposable
    {
        private readonly DownloadClientGrabService service;
        private readonly BehaviourClient behaviour;

        private World(TestStorage storage, FixedTimeProvider time, SqliteDownloadDispatchesRepository dispatches, DownloadClientGrabService service, BehaviourClient behaviour, string clientId)
        {
            Storage = storage;
            Time = time;
            Dispatches = dispatches;
            this.service = service;
            this.behaviour = behaviour;
            ClientId = clientId;
        }

        public TestStorage Storage { get; }

        public FixedTimeProvider Time { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public string ClientId { get; }

        public Func<DownloadClientItem, DownloadClientGrabRequest, Task<DownloadClientGrabResult>> ClientBehaviour
        {
            set => behaviour.Behaviour = value;
        }

        public Task<DownloadClientGrabResult> GrabAsync()
            => service.GrabAsync(
                ClientId,
                new DownloadClientGrabRequest(Release, "magnet:?xt=urn:btih:" + Hash, "movies", "movies", "Fixture"),
                CancellationToken.None);

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var behaviour = new BehaviourClient();
            var service = new DownloadClientGrabService(
                connections,
                new DownloadClientRegistry([behaviour]),
                new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance),
                dispatches,
                new NullRealtimeEventPublisher());
            return new World(storage, time, dispatches, service, behaviour, client.Id);
        }

        public void Dispose() => Storage.Dispose();
    }

    private sealed class BehaviourClient : IDownloadClient
    {
        public Func<DownloadClientItem, DownloadClientGrabRequest, Task<DownloadClientGrabResult>> Behaviour { get; set; } = null!;

        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => Behaviour(client, request);

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
