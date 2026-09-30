using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientWebhookService(
    IDownloadDispatchesRepository dispatchesRepository,
    ILogger<DownloadClientWebhookService> logger,
    IConnectionsRepository? connectionsRepository = null,
    IDownloadClientRegistry? downloadClientRegistry = null,
    TimeProvider? timeProvider = null)
    : IDownloadClientWebhookService
{
    public async Task<DownloadClientWebhookResult> HandleAsync(
        string clientId,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var dispatch = await ResolveDispatchAsync(clientId, request, cancellationToken);
        if (dispatch is null)
        {
            logger.LogDebug(
                "Webhook from client {ClientId}: no matching dispatch found (event={Event}, hash={Hash}, name={Name}).",
                clientId, request.Event, request.Hash, request.Name);
            return new DownloadClientWebhookResult(false, null, "No matching dispatch found.");
        }

        var normalizedEvent = NormalizeEvent(request.Event);

        if (normalizedEvent == "completed")
        {
            return await HandleCompletionAsync(dispatch, request, cancellationToken);
        }

        if (normalizedEvent == "failed")
        {
            return await HandleFailureAsync(dispatch, request, cancellationToken);
        }

        logger.LogDebug(
            "Webhook from client {ClientId}: unrecognised event '{Event}' for dispatch {DispatchId} — acknowledged but no action taken.",
            clientId, request.Event, dispatch.Id);

        return new DownloadClientWebhookResult(true, dispatch.Id, $"Event '{request.Event}' acknowledged, no action taken.");
    }

    private async Task<DownloadClientWebhookResult> HandleCompletionAsync(
        DownloadDispatchItem dispatch,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken)
    {
        // Polling can have seen the download already (#554); only a completion
        // recorded before this one makes the webhook a duplicate.
        if (dispatch.ClientCompletedUtc is not null)
        {
            return new DownloadClientWebhookResult(true, dispatch.Id, "Dispatch already detected; skipped duplicate webhook.");
        }

        await dispatchesRepository.RecordDetectionAsync(
            dispatch.Id,
            torrentHashOrItemId: await IdToRecordAsync(dispatch, request, cancellationToken),
            downloadedBytes: request.SizeBytes,
            completed: true,
            cancellationToken);

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "webhook-completed",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                savePath = request.SavePath,
                sizeBytes = request.SizeBytes,
                source = "webhook"
            }),
            cancellationToken);

        logger.LogInformation(
            "Webhook: dispatch {DispatchId} ({ReleaseName}) marked detected via push notification.",
            dispatch.Id, dispatch.ReleaseName);

        return new DownloadClientWebhookResult(true, dispatch.Id, "Download completion recorded.");
    }

    /// <summary>
    /// The id to keep on the dispatch (#1062). A webhook is a caller's claim,
    /// so it can never change an id the dispatch already has, and it can only
    /// give an id to a dispatch that has none when the client itself shows
    /// that id under the dispatch's own release name.
    /// </summary>
    private async Task<string?> IdToRecordAsync(
        DownloadDispatchItem dispatch,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId)
            || string.IsNullOrWhiteSpace(request.Hash))
        {
            return dispatch.TorrentHashOrItemId;
        }

        return await ClientShowsHashUnderThisNameAsync(dispatch, request.Hash.Trim(), cancellationToken)
            ? request.Hash.Trim()
            : null;
    }

    private async Task<bool> ClientShowsHashUnderThisNameAsync(
        DownloadDispatchItem dispatch,
        string hash,
        CancellationToken cancellationToken)
    {
        if (connectionsRepository is null || downloadClientRegistry is null)
        {
            return false;
        }

        try
        {
            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null || !downloadClientRegistry.TryGet(client.Protocol, out var implementation))
            {
                return false;
            }

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var snapshot = await implementation.GetSnapshotAsync(client, now, cancellationToken);
            if (snapshot is null)
            {
                return false;
            }

            bool SameName(string? name)
                => string.Equals(name, dispatch.ReleaseName, StringComparison.OrdinalIgnoreCase);
            bool SameId(string? id)
                => string.Equals(id?.Trim(), hash, StringComparison.OrdinalIgnoreCase);

            return snapshot.Queue.Any(item => SameId(item.Id) && SameName(item.ReleaseName))
                || snapshot.History.Any(item => (SameId(item.Id) || SameId(item.ExternalId)) && SameName(item.ReleaseName));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Webhook: could not ask {ClientId} whether it shows {Hash}.", dispatch.DownloadClientId, hash);
            return false;
        }
    }

    private async Task<DownloadClientWebhookResult> HandleFailureAsync(
        DownloadDispatchItem dispatch,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return new DownloadClientWebhookResult(true, dispatch.Id, "Dispatch already has import outcome; skipped duplicate webhook.");
        }

        await dispatchesRepository.RecordImportOutcomeAsync(
            dispatch.Id,
            importStatus: "failed",
            importedFilePath: null,
            importFailureCode: DownloadClientFailureCodes.ClientReported,
            importFailureMessage: request.FailureReason ?? "Download client reported failure.",
            cancellationToken,
            failure: IntegrationFailureFactory.FromLegacy(
                "download-client",
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                "download",
                "failed",
                request.FailureReason ?? "Download client reported failure.",
                code: DownloadClientFailureCodes.ClientReported,
                externalId: dispatch.Id));

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "webhook-failed",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = request.FailureReason,
                source = "webhook"
            }),
            cancellationToken);

        logger.LogWarning(
            "Webhook: dispatch {DispatchId} ({ReleaseName}) reported as failed by client: {Reason}.",
            dispatch.Id, dispatch.ReleaseName, request.FailureReason);

        return new DownloadClientWebhookResult(true, dispatch.Id, "Download failure recorded.");
    }

    private async Task<DownloadDispatchItem?> ResolveDispatchAsync(
        string clientId,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.DispatchId))
        {
            var dispatch = await dispatchesRepository.GetDispatchAsync(request.DispatchId, cancellationToken);
            if (dispatch is not null && string.Equals(dispatch.DownloadClientId, clientId, StringComparison.OrdinalIgnoreCase))
            {
                return dispatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Hash))
        {
            var dispatch = await dispatchesRepository.FindDispatchByHashAsync(clientId, request.Hash, cancellationToken);
            if (dispatch is not null) return dispatch;
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return await dispatchesRepository.FindDispatchByReleaseNameAsync(clientId, request.Name, cancellationToken);
        }

        return null;
    }

    private static string NormalizeEvent(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return "unknown";
        var lower = eventName.ToLowerInvariant();
        return lower switch
        {
            "completed" or "download.completed" or "torrent_completed" or "nzb_completed" or "finished" => "completed",
            "failed" or "download.failed" or "torrent_failed" or "nzb_failed" or "error" => "failed",
            _ => lower
        };
    }
}
