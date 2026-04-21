using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/settings");

        settings.MapGet("/", async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut("/", async (
            UpdatePlatformSettingsRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateSettings(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var snapshot = await repository.SaveAsync(request, cancellationToken);
            return Results.Ok(snapshot);
        });

        var libraries = endpoints.MapGroup("/api/libraries");

        libraries.MapGet("/", async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListLibrariesAsync(cancellationToken);
            return Results.Ok(items);
        });

        libraries.MapPost("/", async (
            CreateLibraryRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateLibrary(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateLibraryAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        libraries.MapDelete("/{id}", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteLibraryAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        var connections = endpoints.MapGroup("/api/connections");

        connections.MapGet("/", async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListConnectionsAsync(cancellationToken);
            return Results.Ok(items);
        });

        connections.MapPost("/", async (
            CreateConnectionRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateConnection(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateConnectionAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        connections.MapDelete("/{id}", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteConnectionAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateSettings(UpdatePlatformSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.AppInstanceName))
        {
            errors["appInstanceName"] = ["A library name is required."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateLibrary(CreateLibraryRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this library a name."];
        }

        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            errors["rootPath"] = ["Choose a folder for this library."];
        }

        var mediaType = request.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is not ("movies" or "tv" or "tv shows" or "tvshows"))
        {
            errors["mediaType"] = ["Choose Movies or TV Shows."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateConnection(CreateConnectionRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this connection a name."];
        }

        if (string.IsNullOrWhiteSpace(request.ConnectionKind))
        {
            errors["connectionKind"] = ["Choose what kind of connection this is."];
        }

        return errors;
    }
}
