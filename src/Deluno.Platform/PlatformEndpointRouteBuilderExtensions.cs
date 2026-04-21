using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using System.Net.Http;
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

        endpoints.MapPost("/api/libraries/{id}/import-existing", async (
            string id,
            IExistingLibraryImportService importService,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var result = await importService.ImportLibraryAsync(id, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.import.existing",
                $"Deluno scanned {result.LibraryName} and brought in {result.ImportedCount} existing item{(result.ImportedCount == 1 ? "" : "s")}.",
                null,
                null,
                "library",
                result.LibraryId,
                cancellationToken);

            return Results.Ok(result);
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

        var indexers = endpoints.MapGroup("/api/indexers");

        indexers.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIndexersAsync(cancellationToken);
            return Results.Ok(items);
        });

        indexers.MapPost(string.Empty, async (
            CreateIndexerRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateIndexer(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIndexerAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        indexers.MapDelete("{id}", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteIndexerAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        indexers.MapPost("{id}/test", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var item = (await repository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(indexer => string.Equals(indexer.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var (healthStatus, message) = await TestIndexerAsync(item, cancellationToken);
            var result = await repository.UpdateIndexerHealthAsync(id, healthStatus, message, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        var downloadClients = endpoints.MapGroup("/api/download-clients");

        downloadClients.MapGet(string.Empty, async (IPlatformSettingsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadClientsAsync(cancellationToken);
            return Results.Ok(items);
        });

        downloadClients.MapPost(string.Empty, async (
            CreateDownloadClientRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateDownloadClient(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateDownloadClientAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        downloadClients.MapDelete("{id}", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var removed = await repository.DeleteDownloadClientAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        endpoints.MapGet("/api/libraries/{id}/routing", async (
            string id,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var routing = await repository.GetLibraryRoutingAsync(id, cancellationToken);
            return routing is null ? Results.NotFound() : Results.Ok(routing);
        });

        endpoints.MapPut("/api/libraries/{id}/routing", async (
            string id,
            UpdateLibraryRoutingRequest request,
            IPlatformSettingsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateLibraryRouting(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var routing = await repository.SaveLibraryRoutingAsync(id, request, cancellationToken);
            return routing is null ? Results.NotFound() : Results.Ok(routing);
        });

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

    private static Dictionary<string, string[]> ValidateDownloadClient(CreateDownloadClientRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this download client a name."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateLibraryRouting(UpdateLibraryRoutingRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in request.Sources ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.IndexerId))
            {
                errors["sources"] = ["Choose a source before saving library routing."];
                break;
            }
        }

        foreach (var client in request.DownloadClients ?? [])
        {
            if (string.IsNullOrWhiteSpace(client.DownloadClientId))
            {
                errors["downloadClients"] = ["Choose a download client before saving library routing."];
                break;
            }
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

    private static Dictionary<string, string[]> ValidateIndexer(CreateIndexerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Give this indexer a name."];
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            errors["baseUrl"] = ["Add the address Deluno should use for this indexer."];
        }

        return errors;
    }

    private static async Task<(string healthStatus, string message)> TestIndexerAsync(
        IndexerItem item,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out var uri))
        {
            return ("attention", "The address is not valid yet.");
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                return ("ready", $"Reached {uri.Host} successfully.");
            }

            return ("attention", $"Reached {uri.Host}, but it returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return ("attention", ex.Message);
        }
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
