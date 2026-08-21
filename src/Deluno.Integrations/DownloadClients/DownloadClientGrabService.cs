using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientGrabService(
    IDownloadHealthRepository platformRepository,
    IConnectionsRepository connectionsRepository,
    IDownloadClientRegistry downloadClientRegistry,
    IIntegrationResiliencePolicy resiliencePolicy,
    IJobScheduler jobScheduler,
    IDownloadDispatchRepository dispatchRepository,
    IDownloadDispatchesRepository dispatchesRepository,
    IRealtimeEventPublisher realtimeEventPublisher,
    TimeProvider timeProvider,
    IRetryPolicyCatalog retryPolicyCatalog)
    : IDownloadClientGrabService
{
    public async Task<DownloadClientGrabResult> GrabAsync(
        string clientId,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return Failed(clientId, request, "notFound", "Download client was not found.");
        }

        if (!client.IsEnabled)
        {
            return Failed(client.Id, request, "paused", "Download client is disabled.");
        }

        if (await platformRepository.IsDownloadReleaseBlockedAsync(client.Id, request.ReleaseName, cancellationToken))
        {
            return Failed(
                client.Id,
                request,
                "blocked",
                "This exact release has repeatedly failed download-health checks. Review or temporarily ignore its health finding before trying it again.");
        }

        // A normal acquisition must never dispatch to a client that was only
        // saved rather than capability-tested.
        if (!string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                client.Id,
                request,
                "unready",
                "Download client has not passed a successful connection test. Test it before sending a release.");
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            return Failed(client.Id, request, "planned", "No downloadable URL was available for this release.");
        }

        await realtimeEventPublisher.PublishDispatchGrabAttemptAsync(
            request.DispatchId ?? "unknown",
            request.ReleaseName,
            client.Id,
            client.Name,
            cancellationToken);

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "grab"),
                "download-client.grab",
                MaxAttempts: 1,
                FailureThreshold: 3),
            token => GrabCoreAsync(client, request, token),
            value => value.Succeeded
                ? IntegrationResilienceOutcome.Success
                : value.Status == "failed"
                    ? IntegrationResilienceOutcome.RetryableFailure
                    : IntegrationResilienceOutcome.NonRetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            await realtimeEventPublisher.PublishDispatchGrabCompletedAsync(
                request.DispatchId ?? "unknown",
                request.ReleaseName,
                client.Id,
                false,
                "Circuit breaker open",
                cancellationToken);

            return Failed(
                client.Id,
                request,
                "circuitOpen",
                "Deluno paused grabs for this client after repeated failures. Test the client connection before sending another release.");
        }

        var grabResult = result.Value ?? Failed(client.Id, request, "failed", result.FailureMessage ?? "Download client grab failed.");

        if (!string.IsNullOrWhiteSpace(request.DispatchId))
        {
            await dispatchesRepository.RecordGrabAsync(
                request.DispatchId,
                grabResult.Status,
                grabResult.ResponseCode,
                grabResult.Message,
                grabResult.FailureCode,
                grabResult.ResponseJson,
                cancellationToken);

            await realtimeEventPublisher.PublishDispatchGrabCompletedAsync(
                request.DispatchId,
                request.ReleaseName,
                client.Id,
                grabResult.Succeeded,
                grabResult.Message,
                cancellationToken);
        }

        if (!grabResult.Succeeded && !string.IsNullOrWhiteSpace(request.DispatchId))
        {
            await ScheduleGrabRetryAsync(request.DispatchId, client.Id, request, cancellationToken);
        }

        return grabResult;
    }

    private async Task ScheduleGrabRetryAsync(
        string dispatchId,
        string clientId,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var policy = retryPolicyCatalog.GrabTimeout;
        var attemptCount = await dispatchRepository.IncrementAttemptCountAsync(dispatchId, cancellationToken);
        var delay = retryPolicyCatalog.CalculateNextRetryDelay(attemptCount, policy);

        if (delay == TimeSpan.Zero)
        {
            await dispatchRepository.MarkDispatchFailedAsync(dispatchId, cancellationToken);
            return;
        }

        var scheduledAt = timeProvider.GetUtcNow().Add(delay);
        var payload = JsonSerializer.Serialize(new
        {
            dispatchId,
            clientId,
            releaseName = request.ReleaseName,
            downloadUrl = request.DownloadUrl,
            mediaType = request.MediaType,
            category = request.Category,
            indexerName = request.IndexerName,
            attemptCount
        });

        await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "download.grab.retry",
                Source: "download-client",
                PayloadJson: payload,
                RelatedEntityType: "download_dispatch",
                RelatedEntityId: dispatchId,
                ScheduledUtc: scheduledAt,
                DedupeKey: $"download.grab.retry:{dispatchId}"),
            cancellationToken);
    }

    private async Task<DownloadClientGrabResult> GrabCoreAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!downloadClientRegistry.TryGet(client.Protocol, out var implementation))
            {
                return Failed(
                    client.Id,
                    request,
                    "failed",
                    $"'{client.Protocol}' is not a supported download client protocol. Supported protocols: {string.Join(", ", downloadClientRegistry.KnownProtocols)}.");
            }

            return await implementation.GrabAsync(client, request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return Failed(client.Id, request, "failed", exception.Message);
        }
    }

    private static DownloadClientGrabResult Failed(string clientId, DownloadClientGrabRequest request, string status, string message)
        => new(clientId, request.ReleaseName, false, status, message);
}
