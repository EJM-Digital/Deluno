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
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// A failed grab is not sent again by itself, and says so (#620).
///
/// <para><b>Why this file exists.</b> A grab that failed with a dispatch id
/// queued a <c>download.grab.retry</c> job and marked the failure "Retry
/// scheduled". No handler and no worker lane ever took that job type, so the job
/// sat queued for good and nothing was re-sent. Any grab that timed out also read
/// "Retry scheduled" in Queue, because a timeout is classed as retryable.</para>
/// </summary>
public sealed class AFailedGrabPromisesNoRetryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    [Fact]
    public async Task A_grab_the_client_did_not_answer_is_not_said_to_be_retried_and_queues_nothing()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Deluge", "deluge", "localhost", 8112, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
        var library = await new SqliteLibrariesRepository(storage.Factory, time).CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "planned", null,
            cancellationToken: CancellationToken.None);
        var service = new DownloadClientGrabService(
            connections,
            new DownloadClientRegistry([new ClientThatDoesNotAnswer("deluge")]),
            new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance),
            dispatches,
            new NullRealtimeEventPublisher());

        var result = await service.GrabAsync(
            client.Id,
            new DownloadClientGrabRequest(
                "Arrival.2016.1080p.WEB", "https://indexer.invalid/get?apikey=secret", "movies", "movies", "Fixture", DispatchId: dispatchId),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(IntegrationRetryState.NotRetryable, result.Failure?.RetryState);
        Assert.Null(result.Failure?.RetryAfterUtc);
        Assert.DoesNotContain("will retry", result.Failure!.NextAction, StringComparison.OrdinalIgnoreCase);
        var stored = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal(IntegrationRetryState.NotRetryable, stored?.Failure?.RetryState);
        Assert.DoesNotContain(
            await jobs.ListAsync(100, CancellationToken.None),
            job => string.Equals(job.JobType, "download.grab.retry", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connectivity")]
    public void A_failure_classed_as_retryable_is_told_as_not_retried(string category)
    {
        var failed = Failed(IntegrationFailureFactory.FromLegacy("download-client", "c", "Deluge", "grab", category, "No answer."));
        Assert.Equal(IntegrationRetryState.RetryScheduled, failed.Failure?.RetryState);

        var told = DownloadClientGrabService.WithoutARetryPromise(failed);

        Assert.Equal(IntegrationRetryState.NotRetryable, told.Failure?.RetryState);
        Assert.Null(told.Failure?.RetryAfterUtc);
    }

    /// <summary>
    /// A client that asks Deluno to wait has said until when, and that is still
    /// worth showing; only the promise that Deluno will act on it goes.
    /// </summary>
    [Fact]
    public void A_rate_limit_keeps_the_time_the_client_asked_for()
    {
        var until = Now.AddMinutes(10);
        var told = DownloadClientGrabService.WithoutARetryPromise(
            Failed(IntegrationFailureFactory.FromLegacy("download-client", "c", "Deluge", "grab", "rateLimit", "Slow down.", retryAfterUtc: until)));

        Assert.Equal(IntegrationRetryState.NotRetryable, told.Failure?.RetryState);
        Assert.Equal(until, told.Failure?.RetryAfterUtc);
    }

    [Fact]
    public void A_paused_client_and_a_failure_that_needs_a_person_keep_their_states()
    {
        var paused = Failed(IntegrationFailureFactory.CircuitOpen("download-client", "c", "Deluge", "grab", Now.AddMinutes(1)));
        var needsPerson = Failed(IntegrationFailureFactory.FromLegacy("download-client", "c", "Deluge", "grab", "auth", "Wrong password."));

        Assert.Equal(paused, DownloadClientGrabService.WithoutARetryPromise(paused));
        Assert.Equal(needsPerson, DownloadClientGrabService.WithoutARetryPromise(needsPerson));
    }

    private static DownloadClientGrabResult Failed(IntegrationFailure failure)
        => new("c", "Arrival.2016.1080p.WEB", false, "failed", failure.Message) { Failure = failure };

    private sealed class ClientThatDoesNotAnswer(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientGrabResult(client.Id, request.ReleaseName, false, "failed", "The client did not answer.")
            {
                Failure = IntegrationFailureFactory.FromLegacy("download-client", client.Id, client.Name, "grab", "timeout", "The client did not answer.")
            });

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
