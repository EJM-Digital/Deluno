using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using Deluno.Infrastructure.Http;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Deluno.Connections.Usenet;
using Deluno.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
        var connections = endpoints.MapGroup("/api/connections")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        var indexers = endpoints.MapGroup("/api/indexers")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        var proxies = endpoints.MapGroup("/api/indexer-proxies")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        indexers.MapGet(string.Empty, async (
            [FromServices] IConnectionsRepository repository,
            [FromServices] IIndexerCallLimiter callLimiter,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListIndexersAsync(cancellationToken);
            // #977: the Health section needs to say how many searches/grabs
            // have been used against this indexer's own cap and when it
            // resets. IIndexerCallLimiter.Peek reads the in-memory window
            // without consuming a call, so this is safe to compute on every
            // list read.
            var now = DateTimeOffset.UtcNow;
            var withLimitStatus = items
                .Select(item => item with
                {
                    QueryLimitStatus = callLimiter.Peek(item, IndexerCallKind.Query, now),
                    GrabLimitStatus = callLimiter.Peek(item, IndexerCallKind.Grab, now)
                })
                .ToArray();
            return Results.Ok(withLimitStatus);
        });

        indexers.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateIndexerRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IHealthCheckRepository healthChecks,
            [FromServices] IIndexerProxyHttpClientFactory proxyHttpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexer(request);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "proxyId", Deluno.Infrastructure.Storage.ReferenceKind.IndexerProxy,
                request.ProxyId, null, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateIndexerAsync(request, cancellationToken);
            // A name the person gave is not replaced by the aggregator's (#1248).
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                item = await NameFromItsAggregatorAsync(item, repository, proxyHttpClientFactory, cancellationToken);
            }

            // #1134: a test run in the drawer before the add is kept with the
            // indexer, so the list says what the person just saw rather than
            // "Untested". Only a status Deluno itself produces is kept.
            if (request.TestedHealth is { HealthStatus: { } testedStatus } tested
                && testedStatus is "healthy" or "degraded" or "unreachable" or "failing" or "timeout")
            {
                await repository.UpdateIndexerHealthAsync(
                    item.Id,
                    testedStatus,
                    tested.Message ?? string.Empty,
                    tested.FailureCategory,
                    tested.LatencyMs,
                    cancellationToken);
                item = (await repository.ListIndexersAsync(cancellationToken)).FirstOrDefault(row => string.Equals(row.Id, item.Id, StringComparison.OrdinalIgnoreCase)) ?? item;
            }
            await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
            // #909: a newly added indexer's reachability is worth showing on
            // Health right away rather than waiting up to fifteen minutes.
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Indexer, cancellationToken);
            return Results.Ok(item);
        });

        indexers.MapPost("test", async (
            HttpContext httpContext,
            [FromBody] CreateIndexerRequest request,
            [FromQuery] string? id,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IIntegrationResiliencePolicy resiliencePolicy,
            [FromServices] IIndexerQueryStatsRepository statsRepository,
            [FromServices] IIndexerProxyHttpClientFactory proxyHttpClientFactory,
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

            // Testing an edit before saving it (#626): a blank secret means
            // "keep the saved one", as it does on save, so the test uses it.
            var saved = string.IsNullOrWhiteSpace(id)
                ? null
                : (await repository.ListIndexersAsync(cancellationToken)).FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                request = request with
                {
                    ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? saved.ApiKey : request.ApiKey,
                    DefinitionSecrets = MergeSecrets(saved.DefinitionSecrets, request.DefinitionSecrets)
                };
            }

            var now = DateTimeOffset.UtcNow;
            var draft = new IndexerItem(
                "draft",
                request.Name?.Trim() ?? "Draft indexer",
                IndexerProtocols.Normalize(request.Protocol),
                IndexerPrivacy.Normalize(request.Privacy),
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
                RequestIntervalSeconds = request.RequestIntervalSeconds,
                MinimumAgeMinutes = request.MinimumAgeMinutes,
                RetentionDays = request.RetentionDays,
                MaximumSizeMb = request.MaximumSizeMb,
                MinimumSeeders = request.MinimumSeeders,
                PreferIndexerFlags = request.PreferIndexerFlags,
                RequiredWords = request.RequiredWords,
                IgnoredWords = request.IgnoredWords,
                MultiLanguages = request.MultiLanguages,
                VipExpiresUtc = request.VipExpiresUtc,
                QueryLimit = request.QueryLimit,
                GrabLimit = request.GrabLimit,
                LimitsUnit = request.LimitsUnit,
                PreferMagnetUrl = request.PreferMagnetUrl,
                SeasonPackSearchMode = request.SeasonPackSearchMode,
                AvailabilityDelayDays = request.AvailabilityDelayDays,
                RssEnabled = request.RssEnabled,
                AutomaticSearchEnabled = request.AutomaticSearchEnabled,
                InteractiveSearchEnabled = request.InteractiveSearchEnabled,
                DefinitionId = request.DefinitionId,
                DefinitionSettings = request.DefinitionSettings,
                DefinitionSecrets = request.DefinitionSecrets,
                ProxyId = request.ProxyId
            };

            var started = Stopwatch.GetTimestamp();
            using var proxiedClient = await CreateIndexerTestHttpClientAsync(draft, repository, proxyHttpClientFactory, cancellationToken);
            var health = await TestIndexerWithResilienceAsync(draft, resiliencePolicy, httpContext.RequestServices.GetService<ITrackerIndexerTester>(), proxiedClient, cancellationToken);
            await RecordAuthTelemetryAsync(statsRepository, draft, health, ElapsedMilliseconds(started), cancellationToken);
            // #755: a generic Newznab/Torznab indexer's own t=caps response,
            // fetched alongside the t=search health test (never instead of
            // it) so categories/search modes can pre-fill the drawer. A
            // caps failure never blocks the add - null here just means
            // nothing to pre-fill.
            var caps = await FetchIndexerCapsAsync(draft, proxiedClient, cancellationToken);
            return Results.Ok(new
            {
                healthStatus = health.HealthStatus,
                message = health.Message,
                failureCategory = health.FailureCategory,
                failure = health.Failure,
                latencyMs = ElapsedMilliseconds(started),
                caps = caps is null ? null : new { categories = caps.Categories, mediaScope = caps.MediaScope }
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
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IHealthCheckRepository healthChecks,
            [FromServices] IIndexerProxyHttpClientFactory proxyHttpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateIndexerUpdate(request);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, errors, "proxyId", Deluno.Infrastructure.Storage.ReferenceKind.IndexerProxy,
                request.ProxyId, null, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var before = (await repository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(indexer => string.Equals(indexer.Id, id, StringComparison.OrdinalIgnoreCase));
            var item = await repository.UpdateIndexerAsync(id, request, cancellationToken);
            if (item is not null)
            {
                // Only an address or key that changed is worth asking the aggregator about again;
                // an enable/pause toggle or a priority change makes no request (#1076).
                var renamed = !string.IsNullOrWhiteSpace(request.Name)
                    && !string.Equals(request.Name.Trim(), before?.Name, StringComparison.OrdinalIgnoreCase);
                if (!renamed && (before is null || AggregatorIndexerNames.LookupNeeded(before, item)))
                {
                    item = await NameFromItsAggregatorAsync(item, repository, proxyHttpClientFactory, cancellationToken);
                }

                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
                await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Indexer, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });


        indexers.MapPost("{id}/test", async (
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

            var item = (await repository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(indexer => string.Equals(indexer.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var result = await RunStoredIndexerTestAsync(item, httpContext.RequestServices, automatic: false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        indexers.MapPost("{id}/reset-circuit", async (
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

            var item = await repository.ResetIndexerCircuitAsync(id, cancellationToken);
            if (item is not null)
            {
                // The indexer_sources row is only half the picture: the
                // resilience policy keeps its own circuit, in memory and
                // persisted in integration_circuit_states, keyed by
                // "indexer:{id}:...". That circuit is what a search actually
                // consults, and a reset that only touched the row left it
                // open — the row said "reset manually" while every search kept
                // silently skipping the indexer (#606).
                await resiliencePolicy.CloseCircuitsAsync("indexer", item.Id, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        proxies.MapGet(string.Empty, async ([FromServices] IConnectionsRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListProxiesAsync(cancellationToken);
            return Results.Ok(items);
        });

        proxies.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateProxyRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateProxy(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.CreateProxyAsync(request, cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("IndexerProxy", item.Id, cancellationToken);
            return Results.Ok(item);
        });

        proxies.MapPut("{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateProxyRequest request,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.UpdateProxyAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("IndexerProxy", item.Id, cancellationToken);
            }

            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        proxies.MapDelete("{id}", async (
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

            var removed = await repository.DeleteProxyAsync(id, cancellationToken);
            if (removed)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("IndexerProxy", id, cancellationToken);
                // An indexer that was assigned to this proxy now runs a
                // direct connection; the Indexers list reflects that.
                await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", "*", cancellationToken);
            }

            return removed ? Results.Ok() : Results.NotFound();
        });

        var downloadClients = endpoints.MapGroup("/api/download-clients")
            .RequireAuthorization(DelunoAuthorizationPolicies.Queue);

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
            [FromServices] IHealthCheckRepository healthChecks,
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
            await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.DownloadClient, cancellationToken);
            return Results.Ok(item);
        });

        // The shared Remote Path Mappings screen (#809): one list across every
        // host, rather than only what one client's drawer shows. Reuses the
        // same repository call the per-client route already makes, with no
        // client id to scope it, which is exactly the "list everything"
        // behaviour ListDownloadClientPathMappingsAsync already has.
        downloadClients.MapGet("path-mappings", async (
            [FromServices] IConnectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListDownloadClientPathMappingsAsync(null, cancellationToken);
            return Results.Ok(items);
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
            [FromQuery] string? id,
            [FromServices] IConnectionsRepository repository,
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

            // Testing an edit before saving it (#626): a blank password means
            // "keep the saved one", as it does on save.
            if (!string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(request.Password))
            {
                var saved = (await repository.ListDownloadClientsAsync(cancellationToken))
                    .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (saved is not null)
                {
                    request = request with { Password = saved.Secret };
                }
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
                request.UseSsl,
                string.IsNullOrWhiteSpace(request.UrlBase) ? null : request.UrlBase.Trim(),
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
                failure = health.Failure,
                latencyMs = ElapsedMilliseconds(started)
            });
        });

        // Deleting a download client takes its downloads with it (#568).
        //
        // The poller has no idea a client has gone: it went on working through
        // that client's downloads, which can never be detected now, and raising
        // a case for each reading "SABnzbd accepted this download and then never
        // showed it" about a client nobody has any more.
        downloadClients.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] IConnectionsRepository repository,
            [FromServices] IRealtimeEventPublisher realtimeEventPublisher,
            [FromServices] IDeletedThingCleanup cleanup,
            [FromServices] IEnumerable<IDownloadClientDeletedHook> deletedHooks,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Read before it goes, so what is said names the client.
            var client = (await repository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            var removed = await repository.DeleteDownloadClientAsync(id, cancellationToken);
            if (removed)
            {
                await cleanup.DownloadClientDeletedAsync(id, client?.Name ?? string.Empty, cancellationToken);
                if (client is not null)
                {
                    foreach (var hook in deletedHooks)
                    {
                        await hook.DownloadClientDeletedAsync(client.Protocol, client.Host, cancellationToken);
                    }
                }
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
            [FromServices] IHealthCheckRepository healthChecks,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var updateErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (request.Protocol is not null && !DownloadClientProtocolCatalog.IsDispatchable(request.Protocol))
            {
                updateErrors["protocol"] = [$"Choose a supported download client protocol: {DownloadClientProtocolCatalog.SupportedProtocols}."];
            }

            AddDownloadClientRangeErrors(updateErrors, request.Port, request.Priority);
            if (updateErrors.Count > 0)
            {
                return Results.ValidationProblem(updateErrors);
            }

            var item = await repository.UpdateDownloadClientAsync(id, request, cancellationToken);
            if (item is not null)
            {
                await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", item.Id, cancellationToken);
                // #909: a changed category, path or address is exactly what
                // the folder-chain and save-path checks depend on.
                await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.DownloadClient, cancellationToken);
                await healthChecks.RequestImmediateRunAsync(HealthCheckCategories.Library, cancellationToken);
            }
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        downloadClients.MapPost("{id}/test", async (
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

            var item = (await repository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(client => string.Equals(client.Id, id, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return Results.NotFound();
            }

            var result = await RunStoredDownloadClientTestAsync(item, httpContext.RequestServices, automatic: false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // #1098: every connection's latest real check (the System screen's Checked column), and Test all,
        // which tests a few at a time and pushes each result as ConnectionChecked as it finishes.
        connections.MapGet("checks", async (
            [FromServices] IConnectionCheckRecorder recorder,
            CancellationToken cancellationToken) => Results.Ok(await recorder.ListAsync(cancellationToken)));

        // #1117: the version each connection last reported, and the newer stable release when one is out. Read from
        // what the checks stored and what the release feeds answered earlier: this never calls a connection or a feed.
        connections.MapGet("versions", async (
            [FromServices] IConnectionVersionStore store,
            [FromServices] ILatestReleaseService latestReleases,
            CancellationToken cancellationToken) =>
        {
            var items = (await store.ListAsync(cancellationToken)).Select(row =>
            {
                var latest = row.Project is null ? null : latestReleases.Peek(row.Project);
                var update = latest is not null && ReleaseVersions.IsOutOfDate(row.Version, latest.Version);
                return new ConnectionVersionItem(row.Kind, row.Id, row.Version, latest?.Version, update, update ? latest!.Url : null,
                    latest is null && row.Project is not null ? latestReleases.LimitResetsUtc(row.Project) : null,
                    latest is null && row.Project is not null ? latestReleases.CheckProblem(row.Project) : null);
            });
            return Results.Ok(items);
        });

        connections.MapPost("test-all", async (
            [FromServices] IConnectionHealthTester tester,
            CancellationToken cancellationToken) =>
        {
            var results = await tester.TestAllAsync(cancellationToken);
            return Results.Ok(new { tested = results.Count, results });
        });

        connections.MapPost("{kind}/{id}/test", async (
            string kind,
            string id,
            [FromServices] IConnectionHealthTester tester,
            CancellationToken cancellationToken) =>
        {
            var result = await tester.TestAsync(kind, id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateDownloadClient(CreateDownloadClientRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (!DownloadClientProtocolCatalog.IsDispatchable(request.Protocol))
        {
            errors["protocol"] = [$"Choose a supported download client protocol: {DownloadClientProtocolCatalog.SupportedProtocols}."];
        }

        AddDownloadClientRangeErrors(errors, request.Port, request.Priority);
        return errors;
    }

    /// <summary>The drawer's own limits, held by the server too: a port is 1 to 65535 and a priority is 1 to 50.</summary>
    private static void AddDownloadClientRangeErrors(Dictionary<string, string[]> errors, int? port, int? priority)
    {
        if (port is not null && (port.Value < 1 || port.Value > 65535))
        {
            errors["port"] = ["The port must be a number from 1 to 65535."];
        }

        if (priority is not null && (priority.Value < 1 || priority.Value > 50))
        {
            errors["priority"] = ["The priority must be a number from 1 to 50."];
        }
    }

    private static IReadOnlyDictionary<string, string>? MergeSecrets(
        IReadOnlyDictionary<string, string>? saved,
        IReadOnlyDictionary<string, string>? typed)
    {
        if (saved is null || saved.Count == 0)
        {
            return typed;
        }

        var merged = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in typed ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static Dictionary<string, string[]> ValidateIndexer(CreateIndexerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            errors["baseUrl"] = ["Add the address Deluno should use for this indexer."];
        }
        else if (!IndexerApiEndpoint.IsUsable(request.BaseUrl))
        {
            // Refused here rather than saved and then marked degraded (#627).
            errors["baseUrl"] = ["That is not a web address Deluno can use. It should look like https://api.example.com."];
        }

        if (IndexerProtocols.IsTracker(request.Protocol) && string.IsNullOrWhiteSpace(request.DefinitionId))
        {
            errors["definitionId"] = ["Choose which tracker this is."];
        }

        AddRequestIntervalError(errors, request.RequestIntervalSeconds, clearRequested: false);
        AddAcquisitionSettingsErrors(
            errors,
            request.MinimumAgeMinutes,
            request.RetentionDays,
            request.MaximumSizeMb,
            request.PreferIndexerFlags,
            request.AvailabilityDelayDays,
            request.MinimumSeeders,
            request.RequiredWords,
            request.IgnoredWords,
            request.MultiLanguages);
        AddQueryGrabLimitErrors(errors, request.QueryLimit, request.GrabLimit);

        if (request.SeasonPackSeedForHours is not null)
        {
            AddNonNegativeError(errors, "seasonPackSeedForHours", request.SeasonPackSeedForHours, "Season-pack seed time cannot be negative.");
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateProxy(CreateProxyRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.ProxyType) || !ProxyTypes.IsKnown(request.ProxyType))
        {
            errors["proxyType"] = ["Choose HTTP, SOCKS4, SOCKS5 or FlareSolverr/Byparr."];
        }

        if (string.IsNullOrWhiteSpace(request.Address))
        {
            errors["address"] = ["Add the address Deluno should send requests through."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateIndexerUpdate(UpdateIndexerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        AddRequestIntervalError(errors, request.RequestIntervalSeconds, request.ClearRequestInterval == true);
        if (request.ClearSeasonPackSeedForHours != true && request.SeasonPackSeedForHours is not null)
        {
            AddNonNegativeError(errors, "seasonPackSeedForHours", request.SeasonPackSeedForHours, "Season-pack seed time cannot be negative.");
        }
        if (request.ClearMinimumAge != true && request.MinimumAgeMinutes is not null)
        {
            AddNonNegativeError(errors, "minimumAgeMinutes", request.MinimumAgeMinutes, "Minimum age cannot be negative.");
        }

        if (request.ClearRetention != true && request.RetentionDays is not null)
        {
            AddNonNegativeError(errors, "retentionDays", request.RetentionDays, "Retention cannot be negative.");
        }

        if (request.ClearMaximumSize != true && request.MaximumSizeMb is not null)
        {
            AddNonNegativeError(errors, "maximumSizeMb", request.MaximumSizeMb, "Maximum size cannot be negative.");
        }

        if (request.ClearAvailabilityDelay != true && request.AvailabilityDelayDays is not null)
        {
            AddNonNegativeError(errors, "availabilityDelayDays", request.AvailabilityDelayDays, "Availability delay cannot be negative.");
        }

        if (request.ClearMinimumSeeders != true && request.MinimumSeeders is not null)
        {
            AddNonNegativeError(errors, "minimumSeeders", request.MinimumSeeders, "Minimum seeders cannot be negative.");
        }

        if (request.ClearPreferIndexerFlags != true && request.PreferIndexerFlags is { Length: > 500 })
        {
            errors["preferIndexerFlags"] = ["Indexer flags must be 500 characters or fewer."];
        }

        if (request.ClearRequiredWords != true && request.RequiredWords is { Length: > 500 })
        {
            errors["requiredWords"] = ["Required words must be 500 characters or fewer."];
        }

        if (request.ClearIgnoredWords != true && request.IgnoredWords is { Length: > 500 })
        {
            errors["ignoredWords"] = ["Ignored words must be 500 characters or fewer."];
        }

        if (request.ClearMultiLanguages != true && request.MultiLanguages is { Length: > 200 })
        {
            errors["multiLanguages"] = ["Multi-language meaning must be 200 characters or fewer."];
        }

        if (request.ClearQueryLimit != true && request.QueryLimit is not null)
        {
            AddNonNegativeError(errors, "queryLimit", request.QueryLimit, "Query limit cannot be negative.");
        }

        if (request.ClearGrabLimit != true && request.GrabLimit is not null)
        {
            AddNonNegativeError(errors, "grabLimit", request.GrabLimit, "Grab limit cannot be negative.");
        }
        return errors;
    }

    private static void AddAcquisitionSettingsErrors(
        Dictionary<string, string[]> errors,
        int? minimumAgeMinutes,
        int? retentionDays,
        int? maximumSizeMb,
        string? preferIndexerFlags,
        int? availabilityDelayDays,
        int? minimumSeeders = null,
        string? requiredWords = null,
        string? ignoredWords = null,
        string? multiLanguages = null)
    {
        AddNonNegativeError(errors, "minimumAgeMinutes", minimumAgeMinutes, "Minimum age cannot be negative.");
        AddNonNegativeError(errors, "retentionDays", retentionDays, "Retention cannot be negative.");
        AddNonNegativeError(errors, "maximumSizeMb", maximumSizeMb, "Maximum size cannot be negative.");
        AddNonNegativeError(errors, "availabilityDelayDays", availabilityDelayDays, "Availability delay cannot be negative.");
        AddNonNegativeError(errors, "minimumSeeders", minimumSeeders, "Minimum seeders cannot be negative.");
        if (preferIndexerFlags is { Length: > 500 })
        {
            errors["preferIndexerFlags"] = ["Indexer flags must be 500 characters or fewer."];
        }

        if (requiredWords is { Length: > 500 })
        {
            errors["requiredWords"] = ["Required words must be 500 characters or fewer."];
        }

        if (ignoredWords is { Length: > 500 })
        {
            errors["ignoredWords"] = ["Ignored words must be 500 characters or fewer."];
        }

        if (multiLanguages is { Length: > 200 })
        {
            errors["multiLanguages"] = ["Multi-language meaning must be 200 characters or fewer."];
        }
    }

    private static void AddQueryGrabLimitErrors(Dictionary<string, string[]> errors, int? queryLimit, int? grabLimit)
    {
        AddNonNegativeError(errors, "queryLimit", queryLimit, "Query limit cannot be negative.");
        AddNonNegativeError(errors, "grabLimit", grabLimit, "Grab limit cannot be negative.");
    }

    private static void AddNonNegativeError(
        Dictionary<string, string[]> errors,
        string key,
        int? value,
        string message)
    {
        if (value is < 0)
        {
            errors[key] = [message];
        }
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
        => DownloadClientProtocolCatalog.NormalizeOrThrow(value);

    /// <summary>
    /// The one real test of a stored indexer: the Test button, Test all and the regular check all run this,
    /// so the stored health, the pushed ConnectionChecked event and the notifications agree (#1098).
    /// <paramref name="automatic"/> keeps a background check quiet: it tells a person only when the answer changed.
    /// </summary>
    public static async Task<IndexerTestResult?> RunStoredIndexerTestAsync(
        IndexerItem item,
        IServiceProvider services,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IConnectionsRepository>();
        var realtimeEventPublisher = services.GetRequiredService<IRealtimeEventPublisher>();
        var resiliencePolicy = services.GetRequiredService<IIntegrationResiliencePolicy>();
        var statsRepository = services.GetRequiredService<IIndexerQueryStatsRepository>();
        var notificationService = services.GetRequiredService<IOutboundNotificationService>();
        var notificationPreferences = services.GetRequiredService<INotificationService>();
        var proxyHttpClientFactory = services.GetRequiredService<IIndexerProxyHttpClientFactory>();

        var previousHealthStatus = item.HealthStatus;
        var started = Stopwatch.GetTimestamp();
        using var proxiedClient = await CreateIndexerTestHttpClientAsync(item, repository, proxyHttpClientFactory, cancellationToken);
        var health = await TestIndexerWithResilienceAsync(item, resiliencePolicy, services.GetService<ITrackerIndexerTester>(), proxiedClient, cancellationToken);
        await RecordAuthTelemetryAsync(statsRepository, item, health, ElapsedMilliseconds(started), cancellationToken);
        item = await NameFromItsAggregatorAsync(item, repository, proxyHttpClientFactory, cancellationToken);
        var elapsed = ElapsedMilliseconds(started);
        var result = await repository.UpdateIndexerHealthAsync(
            item.Id,
            health.HealthStatus,
            health.Message,
            health.FailureCategory,
            elapsed,
            cancellationToken,
            health.Failure);
        RecordIntegrationHealthMetric("indexer", health.HealthStatus);
        if (result is null)
        {
            return null;
        }

        await realtimeEventPublisher.PublishHealthChangedAsync(
            item.Name,
            health.HealthStatus == "healthy" ? "healthy" : "degraded",
            health.Message,
            cancellationToken);
        await realtimeEventPublisher.PublishEntityChangedAsync("Indexer", item.Id, cancellationToken);
        await RecordCheckAsync(services, ConnectionKinds.Indexer, item.Id, item.Name, health, elapsed, cancellationToken);

        var changed = !string.Equals(previousHealthStatus, health.HealthStatus, StringComparison.OrdinalIgnoreCase);
        if (health.HealthStatus != "healthy")
        {
            if ((!automatic || changed)
                && (await notificationPreferences.GetPreferencesAsync(cancellationToken)).ProviderHealthChangedEnabled)
            {
                await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
                    "health.degraded",
                    $"Indexer degraded: {item.Name}",
                    health.Message,
                    null,
                    cancellationToken), "health.degraded");
            }
        }
        else
        {
            await DispatchHealthRestoredIfRecoveredAsync(
                notificationService,
                notificationPreferences,
                $"Indexer {item.Name}",
                previousHealthStatus,
                health.HealthStatus,
                health.Message,
                cancellationToken);
        }

        return result;
    }

    /// <summary>The one real test of a stored download client; see <see cref="RunStoredIndexerTestAsync"/>.</summary>
    public static async Task<IndexerTestResult?> RunStoredDownloadClientTestAsync(
        DownloadClientItem item,
        IServiceProvider services,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IConnectionsRepository>();
        var realtimeEventPublisher = services.GetRequiredService<IRealtimeEventPublisher>();
        var resiliencePolicy = services.GetRequiredService<IIntegrationResiliencePolicy>();
        var notificationService = services.GetRequiredService<IOutboundNotificationService>();
        var notificationPreferences = services.GetRequiredService<INotificationService>();

        var previousHealthStatus = item.HealthStatus;
        var started = Stopwatch.GetTimestamp();
        var health = await TestDownloadClientWithResilienceAsync(item, resiliencePolicy, cancellationToken);
        var elapsed = ElapsedMilliseconds(started);
        var result = await repository.UpdateDownloadClientHealthAsync(
            item.Id,
            health.HealthStatus,
            health.Message,
            health.FailureCategory,
            elapsed,
            cancellationToken,
            health.Failure);
        RecordIntegrationHealthMetric("download-client", health.HealthStatus);
        if (result is null)
        {
            return null;
        }

        await realtimeEventPublisher.PublishHealthChangedAsync(
            item.Name,
            health.HealthStatus == "healthy" ? "healthy" : "degraded",
            health.Message,
            cancellationToken);
        await realtimeEventPublisher.PublishEntityChangedAsync("DownloadClient", item.Id, cancellationToken);
        await RecordCheckAsync(services, ConnectionKinds.DownloadClient, item.Id, item.Name, health, elapsed, cancellationToken);

        var changed = !string.Equals(previousHealthStatus, health.HealthStatus, StringComparison.OrdinalIgnoreCase);
        if (health.HealthStatus != "healthy")
        {
            if ((!automatic || changed)
                && (await notificationPreferences.GetPreferencesAsync(cancellationToken)).ProviderHealthChangedEnabled)
            {
                await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
                    "health.degraded",
                    $"Download client degraded: {item.Name}",
                    health.Message,
                    null,
                    cancellationToken), "health.degraded");
            }
        }
        else
        {
            await DispatchHealthRestoredIfRecoveredAsync(
                notificationService,
                notificationPreferences,
                $"Download client {item.Name}",
                previousHealthStatus,
                health.HealthStatus,
                health.Message,
                cancellationToken);
        }

        return result;
    }

    private static async Task RecordCheckAsync(
        IServiceProvider services,
        string kind,
        string id,
        string name,
        IntegrationHealthCheckResult health,
        int elapsedMs,
        CancellationToken cancellationToken)
    {
        var recorder = services.GetService<IConnectionCheckRecorder>();
        if (recorder is not null)
        {
            await recorder.RecordAsync(kind, id, name, health.HealthStatus == "healthy", elapsedMs, health.Message, cancellationToken);
        }
    }

    /// <summary>
    /// The client an indexer's own Test (t=search) and caps (t=caps)
    /// requests go out on (#976): routed through its assigned HTTP/SOCKS
    /// proxy when it has one, or a plain direct client otherwise -- the same
    /// routing a search or RSS read uses.
    /// </summary>
    private static async Task<HttpClient> CreateIndexerTestHttpClientAsync(
        IndexerItem item,
        IConnectionsRepository repository,
        IIndexerProxyHttpClientFactory? proxyHttpClientFactory,
        CancellationToken cancellationToken)
    {
        ProxyItem? proxy = null;
        if (proxyHttpClientFactory is not null && !string.IsNullOrWhiteSpace(item.ProxyId))
        {
            proxy = await IndexerProxyResolver.ResolveAsync(item, repository, cancellationToken);
        }

        var client = proxy is not null && proxyHttpClientFactory is not null
            ? proxyHttpClientFactory.CreateClient(proxy)
            : new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler());
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private static async Task<IntegrationHealthCheckResult> TestIndexerWithResilienceAsync(
        IndexerItem item,
        IIntegrationResiliencePolicy resiliencePolicy,
        ITrackerIndexerTester? trackerTester,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                BuildIndexerResilienceKey(item),
                "health-test",
                // MaxAttempts: 1 because a person is waiting. The default is 3,
                // which turned a failing Test into a 25-second silent spinner
                // that then reported an 8-second timeout and one attempt - three
                // numbers, none of which agreed. Retrying belongs to background
                // work, where nobody is watching; DownloadClientGrabService and
                // DownloadClientTelemetryService both say so explicitly.
                MaxAttempts: 1,
                FailureThreshold: 2,
                ServiceType: "indexer",
                ServiceId: item.Id,
                ServiceName: item.Name),
            async token =>
            {
                var (healthStatus, message, failureCategory) = await TestIndexerAsync(item, trackerTester, httpClient, token);
                return HealthResult("indexer", item.Id, item.Name, "health-test", healthStatus, message, failureCategory);
            },
            ClassifyIntegrationHealth,
            cancellationToken);

        // A person just proved the indexer works. Its Test key is not
        // necessarily the same key a background search used to open the
        // circuit (Test includes the protocol, search does not), so closing
        // only the Test's own key can leave the search's circuit open even
        // though the person who tested it thinks it is fixed. Close every
        // circuit recorded for this indexer, whatever suffix opened it.
        if (result.Value is { HealthStatus: "healthy" })
        {
            await resiliencePolicy.CloseCircuitsAsync("indexer", item.Id, cancellationToken);
        }

        return result.CircuitOpen
            ? IntegrationHealthCheckResult.CircuitOpen(
                "indexer",
                item.Id,
                item.Name,
                result.RetryAfterUtc)
            : result.Value is not null
                ? WithActualAttempts(result.Value, result.Attempts)
                : HealthResult(
                "indexer",
                item.Id,
                item.Name,
                "health-test",
                "unreachable",
                result.FailureMessage ?? "Indexer test failed.",
                "connectivity",
                result.Attempts);
    }

    private static async Task<IntegrationHealthCheckResult> TestDownloadClientWithResilienceAsync(
        DownloadClientItem item,
        IIntegrationResiliencePolicy resiliencePolicy,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                BuildDownloadClientResilienceKey(item),
                "health-test",
                // MaxAttempts: 1 because a person is waiting. The default is 3,
                // which turned a failing Test into a 25-second silent spinner
                // that then reported an 8-second timeout and one attempt - three
                // numbers, none of which agreed. Retrying belongs to background
                // work, where nobody is watching; DownloadClientGrabService and
                // DownloadClientTelemetryService both say so explicitly.
                MaxAttempts: 1,
                FailureThreshold: 2,
                ServiceType: "download-client",
                ServiceId: item.Id,
                ServiceName: item.Name),
            async token =>
            {
                var (healthStatus, message, failureCategory) = await TestDownloadClientAsync(item, token);
                return HealthResult("download-client", item.Id, item.Name, "health-test", healthStatus, message, failureCategory);
            },
            ClassifyIntegrationHealth,
            cancellationToken);

        // Same reasoning as the indexer test above: a proven-healthy client
        // should close every circuit recorded under its id, not just the one
        // this particular Test call happened to key itself under.
        if (result.Value is { HealthStatus: "healthy" })
        {
            await resiliencePolicy.CloseCircuitsAsync("download-client", item.Id, cancellationToken);
        }

        return result.CircuitOpen
            ? IntegrationHealthCheckResult.CircuitOpen(
                "download-client",
                item.Id,
                item.Name,
                result.RetryAfterUtc)
            : result.Value is not null
                ? WithActualAttempts(result.Value, result.Attempts)
                : HealthResult(
                "download-client",
                item.Id,
                item.Name,
                "health-test",
                "unreachable",
                result.FailureMessage ?? "Download client test failed.",
                "connectivity",
                result.Attempts);
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
        ITrackerIndexerTester? trackerTester,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (!item.IsEnabled)
        {
            return ("disabled", "Disabled until you turn it on.", null);
        }

        if (item.SecretUnreadable)
        {
            // Asking with a key that cannot be read here would only fail in a confusing way (#1094).
            return ("degraded", item.SecretProblem ?? Deluno.Security.SecretUnreadable.Message(), "auth");
        }

        if (IndexerProtocols.IsTracker(item.Protocol))
        {
            return trackerTester is null
                ? ("degraded", "This Deluno cannot search trackers.", "configuration")
                : await trackerTester.TestAsync(item, cancellationToken);
        }

        var testUrl = BuildIndexerTestUrl(item);
        if (!WebAddress.TryParse(testUrl, out var uri))
        {
            return ("degraded", "The address is not valid yet.", "configuration");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // As searches identify themselves; NZBGeek refuses a request that does not (#625).
            request.Headers.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Torznab and Newznab report a rejected key as an <error> element
                // with HTTP 200, so the status code alone cannot be trusted here.
                if (Deluno.Contracts.IndexerApiError.TryRead(body) is { } indexerError)
                {
                    return ("degraded", $"Reached {uri.Host}, but it refused the request: {indexerError.Description}", indexerError.IsCredentialFailure ? "auth" : "protocol");
                }

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
        // An address saved without https:// still works (#627).
        var baseUrl = IndexerApiEndpoint.NormalizeBaseUrl(item.BaseUrl) ?? item.BaseUrl;
        if (!WebAddress.TryParse(baseUrl, out var uri))
        {
            return baseUrl;
        }

        if (string.Equals(item.Protocol, "rss", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        // The same /api the searches use (#624), or a custom path (#740).
        uri = new Uri(IndexerApiEndpoint.For(item.BaseUrl, item.ApiPath));

        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        var apiKey = string.IsNullOrWhiteSpace(item.ApiKey) ? string.Empty : $"&apikey={Uri.EscapeDataString(item.ApiKey)}";

        // t=search, not t=caps, for the health test itself. Most Torznab and
        // Newznab servers serve their capabilities to anybody, so testing
        // t=caps asked a question the API key could not fail — the same
        // hole SABnzbd's health probe had. A one-row search is the cheapest
        // call that actually presents the credential, and it is what a real
        // query does. t=caps is still called separately (#755), just never
        // in place of this credential-presenting test.
        return $"{uri}{separator}t=search&limit=1{apiKey}";
    }

    /// <summary>
    /// #1076: an indexer behind Prowlarr or Jackett takes its tracker's name from the aggregator
    /// when it is saved or tested. Any failure leaves the address-derived name and never fails the
    /// save or the test.
    /// </summary>
    private static async Task<IndexerItem> NameFromItsAggregatorAsync(
        IndexerItem item,
        IConnectionsRepository repository,
        IIndexerProxyHttpClientFactory? proxyHttpClientFactory,
        CancellationToken cancellationToken)
    {
        if (ConnectionNames.AggregatorOf(item.BaseUrl) is null)
        {
            return item;
        }

        try
        {
            // Its own client that never follows a redirect: the key goes only to the address the
            // person typed, and a redirect just means "no name" (#1076).
            ProxyItem? proxy = null;
            if (proxyHttpClientFactory is not null && !string.IsNullOrWhiteSpace(item.ProxyId))
            {
                proxy = await IndexerProxyResolver.ResolveAsync(item, repository, cancellationToken);
            }

            var webProxy = IndexerProxyWebProxy.Build(proxy);
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync,
                AllowAutoRedirect = false,
                Proxy = webProxy,
                UseProxy = webProxy is not null,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };
            var discovered = await AggregatorIndexerNames.DiscoverAsync(item, client, cancellationToken);
            return discovered is null ? item : await repository.SetDiscoveredIndexerNameAsync(item.Id, discovered, cancellationToken) ?? item;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return item;
        }
    }

    /// <summary>What a generic Newznab/Torznab indexer's own t=caps response says it supports (#755).</summary>
    public sealed record IndexerCapsResult(string? Categories, string? MediaScope);

    private static string? BuildIndexerCapsUrl(IndexerItem item)
    {
        if (string.Equals(item.Protocol, "rss", StringComparison.OrdinalIgnoreCase)
            || IndexerProtocols.IsTracker(item.Protocol))
        {
            return null;
        }

        var baseUrl = IndexerApiEndpoint.NormalizeBaseUrl(item.BaseUrl) ?? item.BaseUrl;
        if (!WebAddress.IsValid(baseUrl))
        {
            return null;
        }

        var uri = new Uri(IndexerApiEndpoint.For(item.BaseUrl, item.ApiPath));
        var separator = string.IsNullOrWhiteSpace(uri.Query) ? "?" : "&";
        var apiKey = string.IsNullOrWhiteSpace(item.ApiKey) ? string.Empty : $"&apikey={Uri.EscapeDataString(item.ApiKey)}";
        return $"{uri}{separator}t=caps{apiKey}";
    }

    /// <summary>
    /// #755: calls a generic Newznab/Torznab indexer's own t=caps endpoint
    /// when adding or testing it. Any failure - unreachable, not
    /// implemented, not XML - is swallowed and reported as "nothing to
    /// pre-fill" rather than surfaced as a test failure; the t=search test
    /// above is what decides whether the indexer itself is healthy.
    /// </summary>
    private static async Task<IndexerCapsResult?> FetchIndexerCapsAsync(IndexerItem item, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var capsUrl = BuildIndexerCapsUrl(item);
        if (capsUrl is null || !WebAddress.TryParse(capsUrl, out var uri))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(IndexerApiEndpoint.UserAgent);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 400)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Deluno.Contracts.IndexerApiError.TryRead(body) is not null ? null : ParseIndexerCaps(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a Torznab/Newznab t=caps response's category ids and which
    /// search modes it says it supports (#755). Internal, not private, so
    /// tests can feed it a caps body directly without a live indexer.
    /// </summary>
    public static IndexerCapsResult? ParseIndexerCaps(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var categoryIds = document.Descendants()
            .Where(element => element.Name.LocalName is "category" or "subcat")
            .Select(element => element.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categoryIds.Length == 0)
        {
            return null;
        }

        var searching = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "searching");
        bool Supports(string elementName)
            => searching?.Elements().Any(element =>
                element.Name.LocalName == elementName
                && string.Equals(element.Attribute("available")?.Value, "yes", StringComparison.OrdinalIgnoreCase)) == true;

        var supportsMovies = Supports("movie-search");
        var supportsTv = Supports("tv-search");
        var mediaScope = supportsMovies && supportsTv
            ? "both"
            : supportsMovies
                ? "movies"
                : supportsTv
                    ? "tv"
                    : null;

        return new IndexerCapsResult(string.Join(",", categoryIds), mediaScope);
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

    private static async Task RecordAuthTelemetryAsync(
        IIndexerQueryStatsRepository statsRepository,
        IndexerItem indexer,
        IntegrationHealthCheckResult health,
        int elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await statsRepository.RecordBatchAsync(
                [new IndexerQueryLogEntry(
                    IndexerId: indexer.Id,
                    IndexerName: indexer.Name,
                    QueryText: "caps",
                    Categories: indexer.Categories,
                    MediaType: indexer.MediaScope,
                    QueryKind: "auth",
                    Outcome: health.HealthStatus == "healthy" ? "matched" : "failed",
                    ElapsedMilliseconds: elapsedMilliseconds,
                    CandidateCount: 0,
                    CreatedUtc: DateTimeOffset.UtcNow,
                    ErrorMessage: health.HealthStatus == "healthy" ? null : health.Message)],
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Health testing must remain useful when the optional diagnostics
            // write is temporarily unavailable.
        }
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

    /// <summary>The same test a person runs from the client drawer, for callers that set a client up themselves (#506).</summary>
    public static async Task<(string healthStatus, string message, string? failureCategory)> TestDownloadClientAsync(
        DownloadClientItem item,
        CancellationToken cancellationToken)
    {
        if (!item.IsEnabled)
        {
            return ("disabled", "Disabled until you turn it on.", null);
        }

        if (item.SecretUnreadable)
        {
            return ("degraded", item.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("password"), "auth");
        }

        // A test is the moment someone asks "is this set up right?", so it has
        // to answer about usability, not only reachability. A legacy protocol
        // like "torrent" is perfectly reachable and can still receive nothing;
        // saying healthy here deferred the truth to the first real grab (#292).
        if (!DownloadClientProtocolCatalog.IsDispatchable(item.Protocol))
        {
            return ("degraded", $"Deluno cannot send downloads to a '{item.Protocol}' client. Change it to one of: {DownloadClientProtocolCatalog.SupportedProtocols}.", "configuration");
        }

        var uri = ResolveDownloadClientEndpoint(item);
        if (uri is null)
        {
            return ("degraded", "Add the client address before testing.", "configuration");
        }

        return await RunWithSlowRetryAsync(
            timeout => TestDownloadClientOnceAsync(item, uri, timeout, cancellationToken),
            DownloadClientFirstTestTimeout,
            DownloadClientRetryTestTimeout,
            cancellationToken);
    }

    /// <summary>First budget for a connection test; a busy new PC can be slower than this once.</summary>
    public static readonly TimeSpan DownloadClientFirstTestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The longer budget for the one retry after a timeout.</summary>
    public static readonly TimeSpan DownloadClientRetryTestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The one connection-test rule shared by the drawer's Test button and connecting the
    /// installed apps (#1060): a test that runs out of time is tried once more with a longer
    /// budget before the client is called unreachable, and if it answers then it is healthy
    /// with a plain note that it was slow. A refused connection, a wrong login or any other
    /// answer is a real answer and is never retried.
    /// </summary>
    public static async Task<(string healthStatus, string message, string? failureCategory)> RunWithSlowRetryAsync(
        Func<TimeSpan, Task<(string healthStatus, string message, string? failureCategory)>> attempt,
        TimeSpan firstTimeout,
        TimeSpan retryTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await attempt(firstTimeout);
        }
        catch (Exception ex) when (IsTimeout(ex, cancellationToken))
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ("unreachable", ex.Message, "connectivity");
        }

        try
        {
            var (status, message, category) = await attempt(retryTimeout);
            return status == "healthy"
                ? (status, $"{message} It was slow to answer: the first try timed out after {(int)firstTimeout.TotalSeconds} seconds and the second try passed.", category)
                : (status, message, category);
        }
        catch (Exception ex) when (IsTimeout(ex, cancellationToken))
        {
            return ("unreachable", $"It did not answer within {(int)retryTimeout.TotalSeconds} seconds, tried twice. It may be busy or still starting; test it again in a minute.", "connectivity");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ("unreachable", ex.Message, "connectivity");
        }
    }

    private static bool IsTimeout(Exception ex, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && (ex is TimeoutException || ex is TaskCanceledException || ex.InnerException is TimeoutException);

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestDownloadClientOnceAsync(
        DownloadClientItem item, Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
        => item.Protocol.ToLowerInvariant() switch
        {
            "qbittorrent" => await TestQbittorrentAsync(item, uri, cancellationToken, timeout),
            "sabnzbd" => await TestSabnzbdAsync(item, uri, cancellationToken, timeout),
            "transmission" => await TestTransmissionAsync(item, uri, cancellationToken, timeout),
            "deluge" => await TestDelugeAsync(item, uri, cancellationToken, timeout),
            "nzbget" => await TestNzbGetAsync(item, uri, cancellationToken, timeout),
            "utorrent" => await TestUTorrentAsync(item, uri, cancellationToken, timeout),
            "rtorrent" => await TestRtorrentAsync(item, uri, cancellationToken, timeout),
            // Unreachable: the dispatchable guard above covers every other value.
            _ => ("degraded", $"Deluno cannot send downloads to a '{item.Protocol}' client.", "configuration")
        };

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestQbittorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = timeout };
        if (string.IsNullOrWhiteSpace(item.Username) && !string.IsNullOrWhiteSpace(item.Secret))
        {
            // #1354: no username means the secret is an API key, sent as a bearer token on every call and never as a login,
            // exactly as QbittorrentDownloadClient does.
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", item.Secret);
        }
        else if (!string.IsNullOrWhiteSpace(item.Username) || !string.IsNullOrWhiteSpace(item.Secret))
        {
            // The same sign-in every later call uses (#1061), so a test that passes cannot be followed by a refusal.
            var login = await QbittorrentSignIn.SignInAsync(client, item.Username, item.Secret, cancellationToken);
            if (!login.SignedIn)
            {
                // #1251: a refused login reads in plain words, with where to check it.
                var qbitKind = string.IsNullOrWhiteSpace(item.Username) ? DownloadClientCredential.ApiKey : DownloadClientCredential.UsernameAndPassword;
                if (login.Banned)
                {
                    return ("degraded", DownloadClientCredentialText.Banned("qBittorrent"), "auth");
                }

                return login.Rejected || DownloadClientCredentialText.IsRefusal(login.Status)
                    ? ("degraded", DownloadClientCredentialText.Refused("qBittorrent", qbitKind), "auth")
                    : ("degraded", $"qBittorrent rejected the login with {(int)login.Status}.", "auth");
            }
        }

        using var response = await client.GetAsync("api/v2/app/version", cancellationToken);
        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to qBittorrent at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("qBittorrent", response.StatusCode, string.IsNullOrWhiteSpace(item.Username) ? DownloadClientCredential.ApiKey : DownloadClientCredential.UsernameAndPassword);
    }

    /// <summary>The shared Usenet reading (<see cref="UsenetProviderCheck"/>); null when the client could not be asked, which never turns a pass into a failure.</summary>
    private static async Task<bool?> ReadSabnzbdHasProviderAsync(HttpClient client, Uri uri, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(uri, $"api?mode=get_config&section=servers&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return UsenetProviderCheck.SabnzbdHasProvider(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<bool?> ReadNzbGetHasProviderAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostSizedJsonAsync(new Uri(uri, "jsonrpc"), new NzbGetRequest("config", []), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return UsenetProviderCheck.NzbGetHasProvider(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestSabnzbdAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(item.Secret))
        {
            return ("degraded", "SABnzbd API key is missing.", "auth");
        }

        using var client = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = timeout };

        // mode=queue, not mode=version. SABnzbd answers mode=version to anybody
        // — no key, wrong key, any key — so testing it asked a question that
        // could not fail, and a client configured with a typo'd API key reported
        // Healthy right up until the first grab returned 403. mode=queue is what
        // SabnzbdDownloadClient itself calls, so the test now exercises the same
        // credential on the same path as the real work.
        var url = new Uri(uri, $"api?mode=queue&output=json&apikey={Uri.EscapeDataString(item.Secret)}");
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return HealthFromStatusCode("SABnzbd", response.StatusCode, DownloadClientCredential.ApiKey);
        }

        // #1142: SABnzbd with no news server answers every call happily and downloads nothing.
        return await ReadSabnzbdHasProviderAsync(client, uri, item.Secret, cancellationToken) == false
            ? ("degraded", UsenetProviderText.NoProvider, "configuration")
            : ("healthy", $"Connected to SABnzbd at {uri.Host}:{uri.Port}.", null);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestTransmissionAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var client = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = timeout };
        AddBasicAuth(client, item);
        var endpoint = new Uri(uri, "transmission/rpc");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = SizedJson.Create(new TransmissionRequest("session-get", new Dictionary<string, object>()))
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", values.FirstOrDefault());
            using var retry = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = SizedJson.Create(new TransmissionRequest("session-get", new Dictionary<string, object>())),
                Headers = { { "X-Transmission-Session-Id", values.FirstOrDefault() ?? string.Empty } }
            }, cancellationToken);
            return retry.IsSuccessStatusCode
                ? ("healthy", $"Connected to Transmission at {uri.Host}:{uri.Port}.", null)
                : HealthFromStatusCode("Transmission", retry.StatusCode, DownloadClientCredential.UsernameAndPassword);
        }

        return response.IsSuccessStatusCode
            ? ("healthy", $"Connected to Transmission at {uri.Host}:{uri.Port}.", null)
            : HealthFromStatusCode("Transmission", response.StatusCode, DownloadClientCredential.UsernameAndPassword);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestDelugeAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var client = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = timeout };
        var login = new DelugeRequest("auth.login", [item.Secret ?? string.Empty]);
        using var response = await client.PostSizedJsonAsync(new Uri(uri, "json"), login, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return HealthFromStatusCode("Deluge", response.StatusCode, DownloadClientCredential.Password);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("true", StringComparison.OrdinalIgnoreCase)
            ? ("healthy", $"Connected to Deluge at {uri.Host}:{uri.Port}.", null)
            : ("degraded", DownloadClientCredentialText.Refused("Deluge", DownloadClientCredential.Password), "auth");
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestNzbGetAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var client = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = timeout };
        AddBasicAuth(client, item);
        using var response = await client.PostSizedJsonAsync(
            new Uri(uri, "jsonrpc"),
            new NzbGetRequest("version", []),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return HealthFromStatusCode("NZBGet", response.StatusCode, DownloadClientCredential.UsernameAndPassword);
        }

        // #1142: NZBGet cannot download until a real news server is set, so the test says so too.
        return await ReadNzbGetHasProviderAsync(client, uri, cancellationToken) == false
            ? ("degraded", UsenetProviderText.NoProvider, "configuration")
            : ("healthy", $"Connected to NZBGet at {uri.Host}:{uri.Port}.", null);
    }

    private static async Task<(string healthStatus, string message, string? failureCategory)> TestUTorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, CookieContainer = new CookieContainer(), Credentials = BuildCredential(item) };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = timeout };
        // #1362: GetAsync, not GetStringAsync, so a refused login is read as one and not thrown as "401" and filed as unreachable.
        using var tokenResponse = await client.GetAsync("gui/token.html", cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return HealthFromStatusCode("uTorrent", tokenResponse.StatusCode, DownloadClientCredential.UsernameAndPassword);
        }

        var html = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        return html.Contains("<div", StringComparison.OrdinalIgnoreCase)
            ? ("healthy", $"Connected to uTorrent at {uri.Host}:{uri.Port}.", null)
            : ("degraded", "uTorrent token endpoint did not return the expected response.", "unexpected-response");
    }

    /// <summary>
    /// #803: a minimal XML-RPC call (system.client_version, which takes no
    /// arguments and every rTorrent build answers) — enough to prove the
    /// endpoint is reachable and speaking rTorrent's RPC, without needing the
    /// adapter's own fuller codec here.
    /// </summary>
    private static async Task<(string healthStatus, string message, string? failureCategory)> TestRtorrentAsync(DownloadClientItem item, Uri uri, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = timeout };
        AddBasicAuth(client, item);
        const string requestXml = "<?xml version=\"1.0\"?><methodCall><methodName>system.client_version</methodName><params></params></methodCall>";
        using var content = new StringContent(requestXml, Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync(uri, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // #1362: a refused login is said as that, like every other client.
            return IsAuthenticationFailure(response.StatusCode)
                ? HealthFromStatusCode("rTorrent", response.StatusCode, DownloadClientCredential.UsernameAndPassword)
                : ("degraded", $"rTorrent returned {(int)response.StatusCode} at {uri.Host}:{uri.Port}.", "unreachable");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("<fault>", StringComparison.OrdinalIgnoreCase)
            ? ("degraded", "rTorrent's RPC endpoint answered with a fault. Check the address and that it fronts rTorrent's XML-RPC over HTTP.", "unexpected-response")
            : body.Contains("<methodResponse>", StringComparison.OrdinalIgnoreCase)
                ? ("healthy", $"Connected to rTorrent at {uri.Host}:{uri.Port}.", null)
                : ("degraded", "The address did not answer with an XML-RPC response. Check it points at rTorrent's own RPC endpoint (often /RPC2).", "unexpected-response");
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
        HttpStatusCode statusCode,
        DownloadClientCredential? credential = null)
        => IsAuthenticationFailure(statusCode)
            ? ("degraded", credential is { } kind
                ? DownloadClientCredentialText.Refused(integrationName, kind)
                : $"{integrationName} rejected authentication with {(int)statusCode}.", "auth")
            : IntegrationResiliencePolicy.IsTransientHttpStatusCode(statusCode)
                ? ("unreachable", $"{integrationName} returned transient HTTP {(int)statusCode}.", "http-transient")
                : ("degraded", $"{integrationName} returned {(int)statusCode}.", "http");

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static int ElapsedMilliseconds(long startTimestamp)
        => (int)Math.Max(0, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    /// <summary>
    /// Radarr/Sonarr document "On Health Issue" and "On Health Restored" as
    /// two separate outbound triggers (#859). A manual Test only ever knew
    /// about the first: whenever the result wasn't healthy it fired
    /// "health.degraded" (every test, not just the first), and nothing ever
    /// announced the provider recovering. This adds the missing half —
    /// "health.restored" on a genuine unhealthy-to-healthy transition — gated
    /// by the single Provider Health Changed toggle shared by every provider
    /// type Deluno tests this way.
    /// </summary>
    private static async Task DispatchHealthRestoredIfRecoveredAsync(
        IOutboundNotificationService notificationService,
        INotificationService notificationPreferences,
        string providerName,
        string previousHealthStatus,
        string newHealthStatus,
        string? message,
        CancellationToken cancellationToken)
    {
        var wasHealthy = string.Equals(previousHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        var isHealthy = string.Equals(newHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (wasHealthy || !isHealthy)
        {
            return;
        }

        var preferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
        if (!preferences.ProviderHealthChangedEnabled)
        {
            return;
        }

        await NotificationOutboundDispatch.GuardAsync(async () => await notificationService.DispatchAsync(
            "health.restored",
            $"{providerName} is healthy again",
            message ?? $"{providerName} recovered.",
            null,
            cancellationToken), "health.restored");
    }

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

    /// <summary>
    /// Makes the reported attempt count match what the resilience policy
    /// actually did.
    ///
    /// <para>The delegate builds its own result and defaults to one attempt.
    /// When it <em>returns</em> a failure rather than throwing,
    /// <c>result.Value</c> is non-null and that default was reported verbatim —
    /// so a test that had tried three times still said <c>attempts: 1</c>. The
    /// policy is the only thing that knows the real number, so it wins.</para>
    /// </summary>
    private static IntegrationHealthCheckResult WithActualAttempts(
        IntegrationHealthCheckResult value,
        int attempts)
        => value.Failure is null || value.Failure.Attempts == attempts
            ? value
            : value with { Failure = value.Failure with { Attempts = attempts } };

    private static IntegrationHealthCheckResult HealthResult(
        string serviceType,
        string serviceId,
        string serviceName,
        string operation,
        string healthStatus,
        string message,
        string? failureCategory,
        int attempts = 1)
        => new(
            healthStatus,
            message,
            failureCategory,
            failureCategory is null
                ? null
                : IntegrationFailureFactory.FromLegacy(
                    serviceType,
                    serviceId,
                    serviceName,
                    operation,
                    failureCategory,
                    message,
                    attempts: attempts));

    private sealed record IntegrationHealthCheckResult(
        string HealthStatus,
        string Message,
        string? FailureCategory,
        IntegrationFailure? Failure = null)
    {
        public static IntegrationHealthCheckResult CircuitOpen(
            string serviceType,
            string serviceId,
            string serviceName,
            DateTimeOffset? retryAfterUtc)
        {
            var message = retryAfterUtc is null
                ? "Deluno paused this integration test after repeated failures."
                : $"Deluno paused this integration test after repeated failures. It will retry after {retryAfterUtc.Value:O}.";
            return new IntegrationHealthCheckResult(
                "unreachable",
                message,
                "circuit-open",
                IntegrationFailureFactory.CircuitOpen(
                    serviceType,
                    serviceId,
                    serviceName,
                    "health-test",
                    retryAfterUtc,
                    message));
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
