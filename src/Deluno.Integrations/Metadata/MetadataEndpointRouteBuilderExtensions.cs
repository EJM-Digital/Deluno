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

        var broker = metadata.MapGroup("/broker");

        broker.MapGet("/status", async (
            HttpContext httpContext,
            TmdbMetadataProvider provider,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var appStatus = await provider.GetDirectStatusAsync(cancellationToken);
            return Results.Ok(new MetadataBrokerStatusResponse(
                "deluno-broker",
                appStatus.IsConfigured,
                "local-direct",
                appStatus.IsConfigured
                    ? "Local broker-compatible endpoint is backed by direct TMDb lookup. Hosted broker can implement the same /metadata/search contract later."
                    : "Local broker-compatible endpoint is available, but direct TMDb lookup needs a key before it can return provider metadata."));
        });

        broker.MapGet("/search", async (
            HttpContext httpContext,
            string? query,
            string? mediaType,
            int? year,
            string? providerId,
            TmdbMetadataProvider provider,
            IPlatformSettingsRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var lookup = new MetadataLookupRequest(query, mediaType, year, providerId);
            var results = await provider.SearchDirectAsync(lookup, cancellationToken);
            return Results.Ok(new MetadataBrokerSearchResponse(
                "deluno-broker",
                "local-direct",
                results.Count,
                results));
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

public sealed record MetadataBrokerStatusResponse(
    string Provider,
    bool IsConfigured,
    string Mode,
    string Message);

public sealed record MetadataBrokerSearchResponse(
    string Provider,
    string Mode,
    int ResultCount,
    IReadOnlyList<MetadataSearchResult> Results);
