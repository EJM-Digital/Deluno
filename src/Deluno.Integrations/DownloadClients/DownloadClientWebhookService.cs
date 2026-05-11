using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientWebhookService(
    IDownloadDispatchesRepository dispatchesRepository,
    ILogger<DownloadClientWebhookService> logger)
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
        if (dispatch.DetectedUtc is not null)
        {
            return new DownloadClientWebhookResult(true, dispatch.Id, "Dispatch already detected; skipped duplicate webhook.");
        }

        await dispatchesRepository.RecordDetectionAsync(
            dispatch.Id,
            torrentHashOrItemId: request.Hash ?? dispatch.TorrentHashOrItemId,
            downloadedBytes: request.SizeBytes,
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
            importFailureCode: "client-reported-failure",
            importFailureMessage: request.FailureReason ?? "Download client reported failure.",
            cancellationToken);

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
