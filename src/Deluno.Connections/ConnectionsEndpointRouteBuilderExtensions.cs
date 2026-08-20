using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Notifications;
using Deluno.Realtime;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Connections;

/// <summary>
/// /api/indexers, /api/download-clients and /api/connections. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies
/// are unchanged apart from the repository type and explicit [FromServices].
/// /api/libraries/{id}/routing stays in Platform -- it reads and writes a
/// library's source/download-client links, which is Library-owned state
/// even though it references connection ids.
/// </summary>
public static class ConnectionsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoConnectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var connections = endpoints.MapGroup("/api/connections");

        var indexers = endpoints.MapGroup("/api/indexers");

        indexers.MapGet(string.Empty, async ([FromServices] IConnectionsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIndexersAsync(cancellationToken);
            return Results.Ok(items);
        });

        indexers.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateIndexerRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexer(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIndexerAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        indexers.MapPost("test", async (
            HttpContext httpContext,
            [FromBody] CreateIndexerRequest request,
            [FromServices] IIntegrationResiliencePolicy resiliencePolicy,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexer(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var now = DateTimeOffset.UtcNow;
            var draft = new IndexerItem(
                "draft",
                request.Name?.Trim() ?? "Draft indexer",
                NormalizeIndexerProtocol(request.Protocol),
                NormalizeIndexerPrivacy(request.Privacy),
                request.BaseUrl?.Trim() ?? string.Empty,
                request.ApiKey,
                request.Priority ?? 10,
                request.Categories?.Trim() ?? string.Empty,
                request.Tags?.Trim() ?? string.Empty,
                NormalizeMediaScope(request.MediaScope),
                request.IsEnabled,
                "testing",
                null,
                null,
                null,
                null,
                0,
                null,
                null,
                now,
                now)
            {
                RequestIntervalSeconds = request.RequestIntervalSeconds
            };

            var started = Stopwatch.GetTimestamp();
            var health = await TestIndexerWithResilienceAsync(draft, resiliencePolicy, cancellationToken);
            return Results.Ok(new
            {
                healthStatus = health.HealthStatus,
                message = health.Message,
                failureCategory = health.FailureCategory,
                latencyMs = ElapsedMilliseconds(started)
            });
        });

        indexers.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteIndexerAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        indexers.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateIndexerRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexerUpdate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.UpdateIndexerAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });


        indexers.MapPost("{id}/test", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IIntegrationResiliencePolicy resiliencePolicy,
            [FromServices] IOutboundNotificationService notificationService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = (await repository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(indexer => string.Equals(indexer.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var started = Stopwatch.GetTimestamp();
            var health = await TestIndexerWithResilienceAsync(item, resiliencePolicy, cancellationToken);
            var result = await repository.UpdateIndexerHealthAsync(id, health.HealthStatus, health.Message, health.FailureCategory, ElapsedMilliseconds(started), cancellationToken);
            RecordIntegrationHealthMetric("indexer", health.HealthStatus);
            if (result is not null)
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    item.Name,
                    health.HealthStatus == "healthy" ? "healthy" : "degraded",
                    health.Message,
                    cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", id, cancellationToken);

                if (health.HealthStatus != "healthy")
                {
                    await notificationService.DispatchAsync(
                        "health.degraded",
                        $"Indexer degraded: {item.Name}",
                        health.Message,
                        null,
                        cancellationToken);
                }
            }

            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        indexers.MapPost("{id}/reset-circuit", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.ResetIndexerCircuitAsync(id, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        var downloadClients = endpoints.MapGroup("/api/download-clients");

        downloadClients.MapGet(string.Empty, async ([FromServices] IConnectionsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadClientsAsync(cancellationToken);
            return Results.Ok(items);
        });

        downloadClients.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateDownloadClientRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDownloadClient(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateDownloadClientAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        downloadClients.MapGet("{id}/path-mappings", async (
            string id,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadClientPathMappingsAsync(id, cancellationToken);
            return Results.Ok(items);
        });

        downloadClients.MapPost("{id}/path-mappings", async (
            string id,
            HttpContext httpContext,
            [FromBody] CreateDownloadClientPathMappingRequest request,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(request.RemotePath) || string.IsNullOrWhiteSpace(request.LocalPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pathMapping"] = ["Both the client path and Deluno path are required."]
                });
            }

            var item = await repository.CreateDownloadClientPathMappingAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        downloadClients.MapDelete("{id}/path-mappings/{mappingId}", async (
            string id,
            string mappingId,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var removed = await repository.DeleteDownloadClientPathMappingAsync(id, mappingId, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        downloadClients.MapPost("{id}/path-mappings/{mappingId}/test", async (
            string id,
            string mappingId,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var mapping = (await repository.ListDownloadClientPathMappingsAsync(id, cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, mappingId, StringComparison.OrdinalIgnoreCase));
            if (mapping is null) return Results.NotFound();

            var reachable = Directory.Exists(mapping.LocalPath) || File.Exists(mapping.LocalPath);
            return Results.Ok(new
            {
                reachable,
                message = reachable
                    ? $"Deluno can reach {mapping.LocalPath}."
                    : $"Deluno cannot reach {mapping.LocalPath}. Check the local mount, share permissions, or the path visible to Deluno."
            });
        });

        downloadClients.MapPost("test", async (
            HttpContext httpContext,
            [FromBody] CreateDownloadClientRequest request,
            [FromServices] IIntegrationResiliencePolicy resiliencePolicy,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateDownloadClient(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var now = DateTimeOffset.UtcNow;
            var draft = new DownloadClientItem(
                "draft",
                request.Name?.Trim() ?? "Draft download client",
                NormalizeDownloadProtocol(request.Protocol),
                string.IsNullOrWhiteSpace(request.Host) ? null : request.Host.Trim(),
                request.Port,
                string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
                string.IsNullOrWhiteSpace(request.Password) ? null : request.Password,
                string.IsNullOrWhiteSpace(request.EndpointUrl) ? null : request.EndpointUrl.Trim(),
                string.IsNullOrWhiteSpace(request.MoviesCategory) ? null : request.MoviesCategory.Trim(),
                string.IsNullOrWhiteSpace(request.TvCategory) ? null : request.TvCategory.Trim(),
                string.IsNullOrWhiteSpace(request.CategoryTemplate) ? null : request.CategoryTemplate.Trim(),
                request.Priority ?? 10,
                request.IsEnabled,
                "testing",
                null,
                null,
                null,
                null,
                now,
                now);

            var started = Stopwatch.GetTimestamp();
            var health = await TestDownloadClientWithResilienceAsync(draft, resiliencePolicy, cancellationToken);
            return Results.Ok(new
            {
                healthStatus = health.HealthStatus,
                message = health.Message,
                failureCategory = health.FailureCategory,
                latencyMs = ElapsedMilliseconds(started)
            });
        });

        downloadClients.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteDownloadClientAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", id, cancellationToken);
            }
            return removed ? Results.NoContent() : Results.NotFound();
        });

        downloadClients.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateDownloadClientRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateDownloadClientAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        downloadClients.MapPost("{id}/test", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IIntegrationResiliencePolicy resiliencePolicy,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = (await repository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(client => string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var started = Stopwatch.GetTimestamp();
            var health = await TestDownloadClientWithResilienceAsync(item, resiliencePolicy, cancellationToken);
            var result = await repository.UpdateDownloadClientHealthAsync(id, health.HealthStatus, health.Message, health.FailureCategory, ElapsedMilliseconds(started), cancellationToken);
            RecordIntegrationHealthMetric("download-client", health.HealthStatus);
            if (result is not null)
            {
                await realtimeEventPublisher.PublishHealthChangedAsync(
                    item.Name,
                    health.HealthStatus == "healthy" ? "healthy" : "degraded",
                    health.Message,
                    cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", id, cancellationToken);
            }

            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        connections.MapGet(string.Empty, async ([FromServices] IConnectionsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListConnectionsAsync(cancellationToken);
            return Results.Ok(items);
        });

        connections.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateConnectionRequest request,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

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
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteConnectionAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
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

        AddRequestIntervalError(errors, request.RequestIntervalSeconds, clearRequested: false);

        return errors;
    }

    private static Dictionary<string, string[]> ValidateIndexerUpdate(UpdateIndexerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        AddRequestIntervalError(errors, request.RequestIntervalSeconds, request.ClearRequestInterval == true);
        return errors;
    }

    private static void AddRequestIntervalError(Dictionary<string, string[]> errors, int? requestIntervalSeconds, bool clearRequested)
    {
        if (clearRequested && requestIntervalSeconds is not null)
        {
            errors["requestIntervalSeconds"] = ["Choose either Deluno's default interval or a custom interval, not both."];
            return;
        }

        if (requestIntervalSeconds is < 2 or > 60)
        {
            errors["requestIntervalSeconds"] = ["The request interval must be between 2 and 60 seconds. Deluno will not query an indexer more often than once every 2 seconds."];
        }

        return;
    }

    private static string NormalizeIndexerProtocol(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "newznab" => "newznab",
            "rss" => "rss",
            _ => "torznab"
        };

    private static string NormalizeIndexerPrivacy(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "semi-private" => "semi-private",
            "usenet" => "usenet",
            _ => "private"
        };

    private static string NormalizeMediaScope(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "movies" => "movies",
            "movie" => "movies",
            "tv" => "tv",
            "series" => "tv",
            "shows" => "tv",
            _ => "both"
        };

    private static string NormalizeDownloadProtocol(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "sabnzbd" => "sabnzbd",
            "transmission" => "transmission",
            "deluge" => "deluge",
            "nzbget" => "nzbget",
            "utorrent" => "utorrent",
            _ => "qbittorrent"
        };

    private static async Task<IntegrationHealthCheckResult> TestIndexerWithResilienceAsync(
        IndexerItem item,
        IIntegrationResiliencePolicy resiliencePolicy,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                BuildIndexerResilienceKey(item),
                "indexer.health-test",
                FailureThreshold: 2),
            async token =>
            {
                var (healthStatus, message, failureCategory) = await TestIndexerAsync(item, token);
                return new IntegrationHealthCheckResult(healthStatus, message, failureCategory);
            },
            ClassifyIntegrationHealth,
            cancellationToken);

        return result.CircuitOpen
            ? IntegrationHealthCheckResult.CircuitOpen(result.RetryAfterUtc)
            : result.Value ?? new IntegrationHealthCheckResult("unreachable", result.FailureMessage ?? "Indexer test failed.", "connectivity");
    }

    private static async Task<IntegrationHealthCheckResult> TestDownloadClientWithResilienceAsync(
        DownloadClientItem item,
        IIntegrationResiliencePolicy resiliencePolicy,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                BuildDownloadClientResilienceKey(item),
                "download-client.health-test",
                FailureThreshold: 2),
            async token =>
            {
                var (healthStatus, message, failureCategory) = await TestDownloadClientAsync(item, token);
                return new IntegrationHealthCheckResult(healthStatus, message, failureCategory);
            },
            ClassifyIntegrationHealth,
            cancellationToken);

        return result.CircuitOpen
            ? IntegrationHealthCheckResult.CircuitOpen(result.RetryAfterUtc)
            : result.Value ?? new IntegrationHealthCheckResult("unreachable", result.FailureMessage ?? "Download client test failed.", "connectivity");
    }

    private static IntegrationResilienceOutcome ClassifyIntegrationHealth(IntegrationHealthCheckResult result)
    {
        if (string.Equals(result.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.HealthStatus, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return IntegrationResilienceOutcome.Success;
        }

        return result.FailureCategory is "connectivity" or "http-transient"
            ? IntegrationResilienceOutcome.RetryableFailure
            : IntegrationResilienceOutcome.NonRetryableFailure;
    }

    private static string BuildIndexerResilienceKey(IndexerItem item)
        => $"indexer:{item.Id}:{item.Protocol}:{SanitizeIntegrationAddress(item.BaseUrl)}";

    private static string BuildDownloadClientResilienceKey(DownloadClientItem item)
        => $"download-client:{item.Id}:{item.Protocol}:{SanitizeIntegrationAddress(item.EndpointUrl ?? $"{item.Host}:{item.Port}")}";

    private static string SanitizeIntegrationAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unconfigured";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return value.Split('?', 2)[0].Trim().ToLowerInvariant();
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestIndexerAsync(
        IndexerItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsEnabled)
        {
            return ("disabled", "Disabled until you turn it on.", null);
        }

        var testUrl = BuildIndexerTestUrl(item);
        if (!Uri.TryCreate(testUrl, UriKind.Absolute, out var uri))
        {
            return ("degraded", "The address is not valid yet.", "configuration");
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (LooksLikeIndexerResponse(item.Protocol, body))
                {
                    return ("healthy", $"Reached {uri.Host} and received a valid {FormatIndexerProtocol(item.Protocol)} response.", null);
                }

                return ("degraded", $"Reached {uri.Host}, but the response did not look like {FormatIndexerProtocol(item.Protocol)}.", "unexpected-response");
            }

            return IsAuthenticationFailure(response.StatusCode)
                ? ("degraded", $"Reached {uri.Host}, but authentication failed with {(int)response.StatusCode}.", "auth")
                : IntegrationResiliencePolicy.IsTransientHttpStatusCode(response.StatusCode)
                    ? ("unreachable", $"Reached {uri.Host}, but it returned transient HTTP {(int)response.StatusCode}.", "http-transient")
                    : ("degraded", $"Reached {uri.Host}, but it returned {(int)response.StatusCode}.", "http");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ("unreachable", ex.Message, "connectivity");
        }
    }

    private static string BuildIndexerTestUrl(IndexerItem item)
    {
        if (!Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out var uri))
        {
            return item.BaseUrl;
        }

        if (string.Equals(item.Protocol, "rss", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        var apiKey = string.IsNullOrWhiteSpace(item.ApiKey) ? string.Empty : $"&apikey={Uri.EscapeDataString(item.ApiKey)}";
        return $"{uri}{separator}t=caps{apiKey}";
    }

    private static bool LooksLikeIndexerResponse(string protocol, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (string.Equals(protocol, "rss", StringComparison.OrdinalIgnoreCase))
        {
            return body.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("<feed", StringComparison.OrdinalIgnoreCase);
        }

        return body.Contains("<caps", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("newznab", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("torznab", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatIndexerProtocol(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            "newznab" => "Newznab",
            "torznab" => "Torznab",
            "rss" => "RSS",
            _ => "indexer"
        };
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestDownloadClientAsync(
        DownloadClientItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsEnabled)
        {
            return ("disabled", "Disabled until you turn it on.", null);
        }

        var uri = ResolveDownloadClientEndpoint(item);
        if (uri is null)
        {
            return ("degraded", "Add the client address before testing.", "configuration");
        }

        try
        {
            return item.Protocol.ToLowerInvariant() switch
            {
                "qbittorrent" => await TestQbittorrentAsync(item, uri, cancellationToken),
                "sabnzbd" => await TestSabnzbdAsync(item, uri, cancellationToken),
                "transmission" => await TestTransmissionAsync(item, uri, cancellationToken),
                "deluge" => await TestDelugeAsync(item, uri, cancellationToken),
                "nzbget" => await TestNzbGetAsync(item, uri, cancellationToken),
                "utorrent" => await TestUTorrentAsync(item, uri, cancellationToken),
                _ => await TestGenericDownloadClientAsync(item, uri, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ("unreachable", ex.Message, "connectivity");
        }
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestQbittorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(item.Username) || !string.IsNullOrWhiteSpace(item.Secret))
        {
            using var login = await client.PostAsync(
                "api/v2/auth/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = item.Username ?? string.Empty,
                    ["password"] = item.Secret ?? string.Empty
                }),
                cancellationToken);
            if (!login.IsSuccessStatusCode)
            {
                return ("degraded", $"qBittorrent rejected the login with {(int)login.StatusCode}.", "auth");
            }
        }

        using var response = await client.GetAsync("api/v2/app/version", cancellationToken);
        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to qBittorrent at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("qBittorrent", response.StatusCode);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestSabnzbdAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Secret))
        {
            return ("degraded", "SABnzbd API key is missing.", "auth");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var url = new Uri(uri, $"api?mode=version&output=json&apikey={Uri.EscapeDataString(item.Secret)}");
        using var response = await client.GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to SABnzbd at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("SABnzbd", response.StatusCode);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestTransmissionAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        AddBasicAuth(client, item);
        var endpoint = new Uri(uri, "transmission/rpc");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new TransmissionRequest("session-get", new Dictionary<string, object>()))
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", values.FirstOrDefault());
            using var retry = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new TransmissionRequest("session-get", new Dictionary<string, object>())),
                Headers = { { "X-Transmission-Session-Id", values.FirstOrDefault() ?? string.Empty } }
            }, cancellationToken);
            return retry.IsSuccessStatusCode
                ? ("healthy", $"Connected to Transmission at {uri.Host}:{uri.Port}.", null)
                : HealthFromStatusCode("Transmission", retry.StatusCode);
        }

        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to Transmission at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("Transmission", response.StatusCode);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestDelugeAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var login = new DelugeRequest("auth.login", [item.Secret ?? string.Empty]);
        using var response = await client.PostAsJsonAsync(new Uri(uri, "json"), login, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return HealthFromStatusCode("Deluge", response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("true", StringComparison.OrdinalIgnoreCase)
            ? ("healthy", $"Connected to Deluge at {uri.Host}:{uri.Port}.", null)
            : ("degraded", "Deluge login failed. Check the Web UI password.", "auth");
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestNzbGetAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        AddBasicAuth(client, item);
        using var response = await client.PostAsJsonAsync(
            new Uri(uri, "jsonrpc"),
            new NzbGetRequest("version", []),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to NZBGet at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("NZBGet", response.StatusCode);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestUTorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = BuildCredential(item) };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8) };
        var html = await client.GetStringAsync("gui/token.html", cancellationToken);
        return html.Contains("<div", StringComparison.OrdinalIgnoreCase)
            ? ("healthy", $"Connected to uTorrent at {uri.Host}:{uri.Port}.", null)
            : ("degraded", "uTorrent token endpoint did not return the expected response.", "unexpected-response");
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestGenericDownloadClientAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await client.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
        {
            return ("healthy", $"Reached {item.Name} at {uri.Host}:{uri.Port}.", null);
        }

        return HealthFromStatusCode(item.Name, response.StatusCode);
    }

    private static Uri? ResolveDownloadClientEndpoint(DownloadClientItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(item.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(item.Host) || item.Port is null)
        {
            return null;
        }

        return Uri.TryCreate($"http://{item.Host}:{item.Port}/", UriKind.Absolute, out var generated)
            ? generated
            : null;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

    private static void AddBasicAuth(HttpClient client, DownloadClientItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Username) && string.IsNullOrWhiteSpace(item.Secret))
        {
            return;
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{item.Username ?? string.Empty}:{item.Secret ?? string.Empty}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static NetworkCredential? BuildCredential(DownloadClientItem item)
        => string.IsNullOrWhiteSpace(item.Username) && string.IsNullOrWhiteSpace(item.Secret)
            ? null
            : new NetworkCredential(item.Username ?? string.Empty, item.Secret ?? string.Empty);

    private static (string healthStatus, string message, string? failureCategory) HealthFromStatusCode(
        string integrationName,
        HttpStatusCode statusCode)
        => IsAuthenticationFailure(statusCode)
            ? ("degraded", $"{integrationName} rejected authentication with {(int)statusCode}.", "auth")
            : IntegrationResiliencePolicy.IsTransientHttpStatusCode(statusCode)
                ? ("unreachable", $"{integrationName} returned transient HTTP {(int)statusCode}.", "http-transient")
                : ("degraded", $"{integrationName} returned {(int)statusCode}.", "http");

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static int ElapsedMilliseconds(long startTimestamp)
        => (int)Math.Max(0, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    private sealed record TransmissionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object> Arguments);

    private sealed record DelugeRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params,
        [property: JsonPropertyName("id")] int Id = 1);

    private sealed record NzbGetRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params);

    private sealed record IntegrationHealthCheckResult(
        string HealthStatus,
        string Message,
        string? FailureCategory)
    {
        public static IntegrationHealthCheckResult CircuitOpen(DateTimeOffset? retryAfterUtc)
        {
            var message = retryAfterUtc is null
                ? "Deluno paused this integration test after repeated failures."
                : $"Deluno paused this integration test after repeated failures. It will retry after {retryAfterUtc.Value:O}.";
            return new IntegrationHealthCheckResult("unreachable", message, "circuit-open");
        }
    }

    private static void RecordIntegrationHealthMetric(string integrationType, string healthStatus)
    {
        if (healthStatus is "healthy" or "disabled" or "untested")
        {
            return;
        }

        DelunoObservability.IntegrationFailures.Add(
            1,
            new("integration.type", integrationType),
            new("health.status", healthStatus));
    }
}
