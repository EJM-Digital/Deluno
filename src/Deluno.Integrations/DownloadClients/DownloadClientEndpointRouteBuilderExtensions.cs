using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Security;

namespace Deluno.Integrations.DownloadClients;

public static class DownloadClientEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoDownloadClientIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/download-health", async (
            HttpContext httpContext,
            IPlatformSettingsRepository platformRepository,
            int? take,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await platformRepository.ListDownloadHealthRecordsAsync(take ?? 30, cancellationToken));
        });

        endpoints.MapGet("/api/download-clients/telemetry", async (
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var overview = await telemetryService.GetOverviewAsync(cancellationToken);
            return Results.Ok(overview);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/queue/actions", async (
            string clientId,
            HttpContext httpContext,
            [FromBody] DownloadClientActionRequest request,
            IPlatformSettingsRepository platformRepository,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetryService.ExecuteActionAsync(clientId, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        endpoints.MapGet("/api/download-clients/{clientId}/queue/{queueItemId}/cleanup-preview", async (
            string clientId,
            string queueItemId,
            HttpContext httpContext,
            IPlatformSettingsRepository platformRepository,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var preview = await telemetryService.PreviewCleanupAsync(clientId, queueItemId, cancellationToken);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/queue/{queueItemId}/health/{kind}/ignore", async (
            string clientId,
            string queueItemId,
            string kind,
            HttpContext httpContext,
            [FromBody] IgnoreDownloadHealthFindingRequest request,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var record = await platformRepository.IgnoreDownloadHealthFindingAsync(clientId, queueItemId, kind, request.DurationDays, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/grab", async (
            string clientId,
            HttpContext httpContext,
            [FromBody] DownloadClientGrabRequest request,
            IPlatformSettingsRepository platformRepository,
            IDownloadClientGrabService grabService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await grabService.GrabAsync(clientId, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/webhook", async (
            string clientId,
            DownloadClientWebhookRequest request,
            IDownloadClientWebhookService webhookService,
            CancellationToken cancellationToken) =>
        {
            var result = await webhookService.HandleAsync(clientId, request, cancellationToken);
            return result.Accepted ? Results.Ok(result) : Results.NotFound(result);
        });

        return endpoints;
    }
}

public sealed record IgnoreDownloadHealthFindingRequest(int DurationDays = 7);
