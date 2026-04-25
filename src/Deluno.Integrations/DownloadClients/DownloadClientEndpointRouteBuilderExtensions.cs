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
            DownloadClientActionRequest request,
            IPlatformSettingsRepository platformRepository,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetryService.ExecuteActionAsync(clientId, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/grab", async (
            string clientId,
            HttpContext httpContext,
            DownloadClientGrabRequest request,
            IPlatformSettingsRepository platformRepository,
            IDownloadClientGrabService grabService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await grabService.GrabAsync(clientId, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        return endpoints;
    }
}
