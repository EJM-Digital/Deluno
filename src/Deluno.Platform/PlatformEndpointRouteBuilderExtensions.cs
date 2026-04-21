using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Platform;

public static class PlatformEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/settings");

        settings.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var snapshot = await repository.GetAsync(cancellationToken);
            return Results.Ok(snapshot);
        });

        settings.MapPut(string.Empty, async (
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

        libraries.MapGet(string.Empty, async (
            IPlatformSettingsRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListLibrariesAsync(cancellationToken);
            var automation = await jobs.ListLibraryAutomationStatesAsync(cancellationToken);
            return Results.Ok(items.Select(item => MergeLibraryState(item, automation)));
        });

        libraries.MapPost(string.Empty, async (
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

        endpoints.MapPut("/api/libraries/{id}/automation", async (
            string id,
            UpdateLibraryAutomationRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateLibraryAutomation(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateLibraryAutomationAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        endpoints.MapPost("/api/libraries/{id}/search-now", async (
            string id,
            IPlatformSettingsRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var library = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                return Results.NotFound();
            }

            var requested = await jobs.RequestLibrarySearchAsync(ToPlanItem(library), cancellationToken);
            return requested ? Results.Accepted() : Results.NotFound();
        });

        libraries.MapDelete("{id}", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteLibraryAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        var connections = endpoints.MapGroup("/api/connections");

        connections.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListConnectionsAsync(cancellationToken);
            return Results.Ok(items);
        });

        connections.MapPost(string.Empty, async (
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

        connections.MapDelete("{id}", async (
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

    private static Dictionary<string, string[]> ValidateLibraryAutomation(UpdateLibraryAutomationRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (request.SearchIntervalHours is <= 0)
        {
            errors["searchIntervalHours"] = ["Choose how often Deluno should check this library."];
        }

        if (request.RetryDelayHours is <= 0)
        {
            errors["retryDelayHours"] = ["Choose how long Deluno should wait before trying again."];
        }

        if (request.MaxItemsPerRun is <= 0)
        {
            errors["maxItemsPerRun"] = ["Choose how many titles Deluno should work through at a time."];
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

    private static LibraryItem MergeLibraryState(
        LibraryItem item,
        IReadOnlyDictionary<string, LibraryAutomationStateItem> automation)
    {
        if (!automation.TryGetValue(item.Id, out var state))
        {
            return item;
        }

        return item with
        {
            AutomationStatus = state.Status,
            SearchRequested = state.SearchRequested,
            LastSearchedUtc = state.LastCompletedUtc,
            NextSearchUtc = state.NextSearchUtc
        };
    }

    private static LibraryAutomationPlanItem ToPlanItem(LibraryItem library)
    {
        return new LibraryAutomationPlanItem(
            LibraryId: library.Id,
            LibraryName: library.Name,
            MediaType: library.MediaType,
            AutoSearchEnabled: library.AutoSearchEnabled,
            MissingSearchEnabled: library.MissingSearchEnabled,
            UpgradeSearchEnabled: library.UpgradeSearchEnabled,
            SearchIntervalHours: library.SearchIntervalHours,
            RetryDelayHours: library.RetryDelayHours,
            MaxItemsPerRun: library.MaxItemsPerRun);
    }
}
