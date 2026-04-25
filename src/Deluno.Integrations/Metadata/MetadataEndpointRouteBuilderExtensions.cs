using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Platform;
using Deluno.Platform.Data;

namespace Deluno.Integrations.Metadata;

public static class MetadataEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var metadata = endpoints.MapGroup("/api/metadata");

        metadata.MapGet("/status", async (
            HttpContext httpContext,
            IMetadataProvider provider,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var status = await provider.GetStatusAsync(cancellationToken);
            return Results.Ok(status);
        });

        metadata.MapGet("/search", async (
            HttpContext httpContext,
            string? query,
            string? mediaType,
            int? year,
            IMetadataProvider provider,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var request = new MetadataLookupRequest(query, mediaType, year, null);
            var results = await provider.SearchAsync(request, cancellationToken);
            return Results.Ok(results);
        });

        metadata.MapPost("/test", async (
            HttpContext httpContext,
            MetadataTestRequest request,
            IMetadataProvider provider,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var status = await provider.GetStatusAsync(cancellationToken);
            var lookup = new MetadataLookupRequest(
                string.IsNullOrWhiteSpace(request.Query) ? "The Matrix" : request.Query,
                string.IsNullOrWhiteSpace(request.MediaType) ? "movies" : request.MediaType,
                request.Year,
                request.ProviderId);
            var results = status.IsConfigured
                ? await provider.SearchAsync(lookup, cancellationToken)
                : [];

            return Results.Ok(new MetadataTestResponse(
                status.Provider,
                status.IsConfigured,
                status.Mode,
                status.Message,
                results.Count,
                results.Take(5).ToArray()));
        });

        return endpoints;
    }
}

public sealed record MetadataTestRequest(
    string? Query,
    string? MediaType,
    int? Year,
    string? ProviderId);

public sealed record MetadataTestResponse(
    string Provider,
    bool IsConfigured,
    string Mode,
    string Message,
    int ResultCount,
    IReadOnlyList<MetadataSearchResult> SampleResults);
