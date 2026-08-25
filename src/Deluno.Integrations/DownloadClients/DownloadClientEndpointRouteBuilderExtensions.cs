using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Connections.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Platform;
using Deluno.Platform.Data;

namespace Deluno.Integrations.DownloadClients;

public static class DownloadClientEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoDownloadClientIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/download-health", async (
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            int? pageSize,
            string? pageToken,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await platformRepository.ListDownloadHealthRecordsPageAsync(
                new PageRequest(pageSize ?? 30, pageToken), cancellationToken));
        });

        endpoints.MapGet("/api/download-clients/telemetry", async (
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var overview = await telemetryService.GetOverviewAsync(cancellationToken);
            return Results.Ok(overview);
        });

        // The stored counterpart to the live telemetry above: what the speed
        // has been, rather than what it is this second. Kept separate because
        // the two answer different questions and have very different costs.
        endpoints.MapGet("/api/download-clients/throughput", async (
            int? hours,
            IDownloadThroughputRepository throughputRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Bounded by the sampler's own retention: asking for a week cannot
            // conjure history that was never kept.
            var window = Math.Clamp(hours ?? 6, 1, 48);
            var since = timeProvider.GetUtcNow().AddHours(-window);
            var samples = await throughputRepository.ListSamplesAsync(since, cancellationToken);
            return Results.Ok(new DownloadThroughputWindow(window, samples));
        });

        endpoints.MapPost("/api/download-clients/{clientId}/queue/actions", async (
            string clientId,
            HttpContext httpContext,
            [FromBody] DownloadClientActionRequest request,
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
            IDownloadHealthRepository platformRepository,
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

        endpoints.MapPost("/api/download-clients/{clientId}/categories/check", async (
            string clientId,
            HttpContext httpContext,
            DownloadClientCategoryCheckRequest request,
            IConnectionsRepository connectionsRepository,
            IDownloadClientRegistry downloadClientRegistry,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var category = request.Category?.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["category"] = ["Enter the category name used by the download client."]
                });
            }

            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                return Results.NotFound();
            }

            if (!downloadClientRegistry.TryGet(client.Protocol, out var downloadClient))
            {
                return Results.Ok(new DownloadClientCategoryCheckResult(
                    client.Id,
                    client.Name,
                    category,
                    DownloadClientCategoryStatuses.Unsupported,
                    $"Deluno does not have a category checker for {client.Protocol} yet.",
                    Supported: false,
                    Found: false));
            }

            return Results.Ok(await downloadClient.CheckCategoryAsync(client, category, cancellationToken));
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
