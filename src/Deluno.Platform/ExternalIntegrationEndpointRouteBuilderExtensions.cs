using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Quality.Presets;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Realtime;
using Deluno.Security.Contracts;
using Deluno.Security;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform;

public static class ExternalIntegrationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoExternalIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var integrations = endpoints.MapGroup("/api/integrations")
            .RequireAuthorization(DelunoAuthorizationPolicies.Imports);

        // What Deluno is currently holding back, and for how long. A throttle
        // that works is indistinguishable from a hang unless somebody can see
        // it, which is why this exists rather than only a log line.
        integrations.MapGet("/outbound-throttle", async (
            HttpContext httpContext,
            [FromServices] IOutboundRequestThrottle throttle,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var now = timeProvider.GetUtcNow();
            var hosts = throttle.Describe()
                .Select(state => new
                {
                    state.Host,
                    state.Waiting,
                    state.GrantedCount,
                    state.RefusedCount,
                    TotalWaitedSeconds = Math.Round(state.TotalWaited.TotalSeconds, 1),
                    NextPermitInSeconds = Math.Max(0, Math.Round((state.NextPermitUtc - now).TotalSeconds, 1))
                })
                .ToArray();

            return Results.Ok(new { hosts });
        });

        integrations.MapGet("/external/manifest", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            ILibrariesRepository librariesRepository,
            IConnectionsRepository connectionsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var settings = await repository.GetAsync(cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var connections = await connectionsRepository.ListConnectionsAsync(cancellationToken);

            var manifest = new ExternalIntegrationManifest(
                Product: "Deluno",
                Version: Deluno.Contracts.DelunoApiVersion.Current,
                InstanceName: settings.AppInstanceName,
                Capabilities:
                [
                    "movies",
                    "tv",
                    "indexers",
                    "download-clients",
                    "library-routing",
                    "destination-rules",
                    "metadata",
                    "media-probing",
                    "pre-import-processing",
                    "activity-feed",
                    "signalr"
                ],
                RecommendedCategories: new Dictionary<string, string>
                {
                    ["movies"] = "deluno-movies",
                    ["tv"] = "deluno-tv",
                    ["anime"] = "deluno-anime",
                    ["movies4k"] = "deluno-movies-4k",
                    ["tv4k"] = "deluno-tv-4k"
                },
                Libraries: libraries.Select(library => new ExternalLibraryManifest(
                    library.Id,
                    library.Name,
                    library.MediaType,
                    library.RootPath,
                    library.DownloadsPath,
                    library.QualityProfileName,
                    library.ImportWorkflow,
                    library.ProcessorName,
                    library.ProcessorOutputPath,
                    library.ProcessorTimeoutMinutes,
                    library.ProcessorFailureMode,
                    library.MissingSearchEnabled,
                    library.UpgradeSearchEnabled,
                    library.MaxItemsPerRun,
                    library.AutomationStatus)).ToArray(),
                Indexers: indexers.Select(indexer => new ExternalIndexerManifest(
                    indexer.Id,
                    indexer.Name,
                    indexer.Protocol,
                    indexer.MediaScope,
                    indexer.Priority,
                    indexer.IsEnabled,
                    indexer.HealthStatus)).ToArray(),
                DownloadClients: clients.Select(client => new ExternalDownloadClientManifest(
                    client.Id,
                    client.Name,
                    client.Protocol,
                    client.MoviesCategory ?? "deluno-movies",
                    client.TvCategory ?? "deluno-tv",
                    client.CategoryTemplate,
                    client.Priority,
                    client.IsEnabled,
                    client.HealthStatus)).ToArray(),
                Connections: connections.Select(connection => new ExternalConnectionManifest(
                    connection.Id,
                    connection.Name,
                    connection.ConnectionKind,
                    connection.Role,
                    connection.EndpointUrl,
                    connection.IsEnabled)).ToArray());

            return Results.Ok(manifest);
        });

        integrations.MapGet("/external/health", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            ILibrariesRepository librariesRepository,
            IConnectionsRepository connectionsRepository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var settings = await repository.GetAsync(cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var queue = await jobs.ListAsync(50, cancellationToken);

            return Results.Ok(new ExternalHealthResponse(
                InstanceName: settings.AppInstanceName,
                Status: "online",
                LibraryCount: libraries.Count,
                EnabledIndexerCount: indexers.Count(item => item.IsEnabled),
                EnabledDownloadClientCount: clients.Count(item => item.IsEnabled),
                ActiveJobCount: queue.Count(item => string.Equals(item.Status, "running", StringComparison.OrdinalIgnoreCase)),
                ProblemCount:
                    indexers.Count(item => item.IsEnabled && !string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)) +
                    clients.Count(item => item.IsEnabled && !string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)),
                CheckedUtc: DateTimeOffset.UtcNow));
        });

        integrations.MapGet("/external/snapshot", async (
            HttpContext httpContext,
            IPlatformSettingsRepository repository,
            ILibrariesRepository librariesRepository,
            IConnectionsRepository connectionsRepository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var settings = await repository.GetAsync(cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var queue = await jobs.ListAsync(200, cancellationToken);
            var jobsByType = queue.GroupBy(j => j.JobType).ToDictionary(g => g.Key, g => g.Count());

            var unhealthyIndexers = indexers
                .Where(item => item.IsEnabled && !string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
                .Select(item => new { item.Id, item.Name, item.HealthStatus, healthMessage = item.LastHealthMessage })
                .ToArray();
            var unhealthyClients = clients
                .Where(item => item.IsEnabled && !string.Equals(item.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
                .Select(item => new { item.Id, item.Name, item.HealthStatus, healthMessage = item.LastHealthMessage })
                .ToArray();

            return Results.Ok(new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                instance = new { settings.AppInstanceName },
                health = new
                {
                    status = unhealthyIndexers.Length + unhealthyClients.Length == 0 ? "healthy" : "degraded",
                    problemCount = unhealthyIndexers.Length + unhealthyClients.Length,
                    unhealthyIndexers,
                    unhealthyClients
                },
                indexers = indexers.Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.IsEnabled,
                    item.MediaScope,
                    item.HealthStatus,
                    healthMessage = item.LastHealthMessage,
                    item.ConsecutiveFailures,
                    rateLimited = item.RateLimitedUntilUtc.HasValue && item.RateLimitedUntilUtc > DateTimeOffset.UtcNow
                }),
                downloadClients = clients.Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.IsEnabled,
                    item.Protocol,
                    item.HealthStatus,
                    healthMessage = item.LastHealthMessage
                }),
                libraries = libraries.Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.MediaType,
                    item.AutoSearchEnabled,
                    item.AutomationStatus,
                    item.SearchWindowStartHour,
                    item.SearchWindowEndHour
                }),
                jobQueue = new
                {
                    total = queue.Count,
                    running = queue.Count(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)),
                    pending = queue.Count(j => string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)),
                    failed = queue.Count(j => string.Equals(j.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(j.Status, "dead-letter", StringComparison.OrdinalIgnoreCase)),
                    byType = jobsByType
                }
            });
        });

        integrations.MapGet("/external/queue", async (
            HttpContext httpContext,
            int? take,
            string? mediaType,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var dispatches = await jobs.ListDownloadDispatchesAsync(Math.Clamp(take ?? 50, 1, 200), mediaType, cancellationToken);
            var queue = await jobs.ListAsync(Math.Clamp(take ?? 50, 1, 200), cancellationToken);
            return Results.Ok(new ExternalQueueResponse(queue, dispatches));
        });

        integrations.MapGet("/external/activity", async (
            HttpContext httpContext,
            int? take,
            IActivityFeedRepository activityFeed,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var activity = await activityFeed.ListActivityAsync(Math.Clamp(take ?? 100, 1, 500), null, null, cancellationToken);
            return Results.Ok(activity);
        });

        integrations.MapPost("/external/trigger-refresh", async (
            HttpContext httpContext,
            [FromBody] ExternalTriggerRefreshRequest request,
            ILibrariesRepository repository,
            IJobQueueRepository jobs,
            IActivityFeedRepository activityFeed,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var libraries = await repository.ListLibrariesAsync(cancellationToken);
            var selected = libraries
                .Where(library =>
                    string.IsNullOrWhiteSpace(request.MediaType) ||
                    string.Equals(library.MediaType, NormalizeMediaScope(request.MediaType), StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var library in selected)
            {
                await jobs.RequestLibrarySearchAsync(new LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalHours: library.SearchIntervalHours,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun,
                    SearchWindowStartHour: library.SearchWindowStartHour,
                    SearchWindowEndHour: library.SearchWindowEndHour), cancellationToken);
            }

            await activityFeed.RecordActivityAsync(
                "integration",
                $"An external app requested refresh for {selected.Length} librar{(selected.Length == 1 ? "y" : "ies")}.",
                JsonSerializer.Serialize(new { request.MediaType, request.Reason, libraries = selected.Select(item => item.Id) }),
                null,
                "integration",
                "external",
                cancellationToken);

            return Results.Ok(new { enqueued = selected.Length });
        });

        integrations.MapGet("/processors/handoffs", async (
            HttpContext httpContext,
            string? libraryId,
            int? take,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;
            return Results.Ok(await repository.ListProcessorHandoffsAsync(libraryId, take ?? 50, cancellationToken));
        });

        integrations.MapPost("/processors/handoffs/{id}/retry", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IActivityFeedRepository activityFeed,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var handoff = await repository.GetProcessorHandoffAsync(id, cancellationToken);
            if (handoff is null) return Results.NotFound();
            if (handoff.Status is not ("failed" or "timed-out"))
            {
                return Results.Conflict(new
                {
                    message = "Only a failed or timed-out processor hand-off can be tried again. Deluno will not resubmit an active or already completed hand-off."
                });
            }

            var connection = await repository.FindProcessorConnectionByNameAsync(handoff.ProcessorName, cancellationToken);
            if (connection is not { IsEnabled: true })
            {
                return Results.Conflict(new
                {
                    message = "The processor connection for this hand-off is unavailable. Restore and test the connection before trying the hand-off again."
                });
            }

            var retried = await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                "waiting",
                null,
                null,
                null,
                cancellationToken);
            if (retried is null) return Results.NotFound();

            await activityFeed.RecordActivityAsync(
                "processing.retry-requested",
                $"Deluno will try {handoff.ReleaseName} with {connection.Name} again using the same hand-off ID.",
                JsonSerializer.Serialize(new
                {
                    HandoffId = handoff.Id,
                    handoff.LibraryId,
                    ConnectionId = connection.Id,
                    connection.Name,
                    PreviousStatus = handoff.Status,
                    IdempotencyKey = handoff.Id
                }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            return Results.Accepted($"/api/integrations/processors/handoffs?libraryId={Uri.EscapeDataString(handoff.LibraryId)}", retried);
        });

        integrations.MapGet("/processors/connections", async (
            HttpContext httpContext,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            return Results.Ok(await repository.ListProcessorConnectionsAsync(cancellationToken));
        });

        integrations.MapPost("/processors/connections", async (
            HttpContext httpContext,
            [FromBody] CreateProcessorConnectionRequest request,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var errors = ValidateProcessorConnection(request.Name, request.SubmissionUrl, request.AuthHeaderName);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await repository.CreateProcessorConnectionAsync(request, cancellationToken));
        });

        integrations.MapPut("/processors/connections/{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateProcessorConnectionRequest request,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var errors = ValidateProcessorConnection(request.Name, request.SubmissionUrl, request.AuthHeaderName);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var updated = await repository.UpdateProcessorConnectionAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        integrations.MapDelete("/processors/connections/{id}", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            return await repository.DeleteProcessorConnectionAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        integrations.MapPost("/processors/connections/{id}/test", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IProcessorConnectionService processorConnections,
            IActivityFeedRepository activityFeed,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var connection = await repository.GetProcessorConnectionAsync(id, cancellationToken);
            if (connection is null) return Results.NotFound();
            var result = await processorConnections.TestAsync(connection, cancellationToken);
            await repository.RecordProcessorConnectionHealthAsync(connection.Id, result.Status, result.Message, cancellationToken);
            await activityFeed.RecordActivityAsync(
                "processor.connection.tested",
                $"{connection.Name}: {result.Message}",
                JsonSerializer.Serialize(new { connection.Id, connection.Name, connection.Provider, result.Status, result.StatusCode, result.LatencyMs }),
                null,
                "processor-connection",
                connection.Id,
                cancellationToken);
            return Results.Ok(result);
        });

        integrations.MapPost("/processors/events", async (
            HttpContext httpContext,
            [FromBody] ProcessorEventRequest request,
            IProcessorRepository repository,
            ILibrariesRepository librariesRepository,
            IActivityFeedRepository activityFeed,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null)
            {
                return scopeDenied;
            }

            var errors = ValidateProcessorEvent(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var status = NormalizeProcessorStatus(request.Status);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = !string.IsNullOrWhiteSpace(request.LibraryId)
                ? libraries.FirstOrDefault(item => string.Equals(item.Id, request.LibraryId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Processor events must name the Deluno library that owns this hand-off. Deluno will not guess an import destination."]
                });
            }

            if (!string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new
                {
                    message = $"{library.Name} is configured for standard import, so Deluno will not accept a processor completion event for it."
                });
            }

            var processorName = library.ProcessorName ?? (string.IsNullOrWhiteSpace(request.ProcessorName)
                ? "External processor"
                : request.ProcessorName.Trim());
            var entityType = string.IsNullOrWhiteSpace(request.EntityType) ? "processor" : request.EntityType.Trim();
            var entityId = string.IsNullOrWhiteSpace(request.EntityId) ? null : request.EntityId.Trim();
            var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
            var mediaType = library.MediaType;

            if (status == "completed" && !ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(request.OutputPath, library.ProcessorOutputPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["outputPath"] = ["Completed output must be inside this library's configured clean output folder. Deluno did not queue an import."]
                });
            }

            var handoff = await repository.FindProcessorHandoffAsync(
                library.Id,
                request.HandoffId,
                request.SourcePath,
                cancellationToken);
            if (handoff is null)
            {
                return Results.Conflict(new
                {
                    message = "Deluno could not match this processor event to a waiting hand-off. Use the handoff ID from GET /api/integrations/processors/handoffs or the exact source path Deluno recorded. No import was queued."
                });
            }

            if (!string.IsNullOrWhiteSpace(request.SourcePath) &&
                !string.Equals(NormalizeProcessorPath(request.SourcePath), NormalizeProcessorPath(handoff.SourcePath), StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sourcePath"] = ["The supplied source path does not match the processor hand-off. Deluno did not queue an import."]
                });
            }

            await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                status,
                status == "completed" ? request.OutputPath : null,
                null,
                status == "failed" ? message : null,
                cancellationToken);

            var activity = await activityFeed.RecordActivityAsync(
                "processing",
                $"{processorName} marked {entityId ?? "media item"} as {status}{(message is null ? "." : $": {message}")}",
                JsonSerializer.Serialize(new
                {
                    request.LibraryId,
                    HandoffId = handoff.Id,
                    MediaType = mediaType,
                    EntityType = entityType,
                    EntityId = entityId,
                    request.SourcePath,
                    request.OutputPath,
                    Status = status,
                    Message = message,
                    ProcessorName = processorName
                }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            JobQueueItem? importJob = null;
            if (status == "completed" && !string.IsNullOrWhiteSpace(request.OutputPath))
            {
                var resolvedMediaType = library.MediaType;
                var outputPath = request.OutputPath.Trim();
                var title = string.IsNullOrWhiteSpace(entityId)
                    ? Path.GetFileNameWithoutExtension(outputPath)
                    : entityId;
                var importPayload = new
                {
                    preview = new
                    {
                        sourcePath = outputPath,
                        fileName = Path.GetFileName(outputPath),
                        mediaType = resolvedMediaType,
                        title,
                        year = (int?)null,
                        genres = Array.Empty<string>(),
                        tags = new[] { "processed" },
                        studio = (string?)null,
                        originalLanguage = (string?)null
                    },
                    transferMode = "auto",
                    overwrite = false,
                    allowCopyFallback = true,
                    forceReplacement = false
                };

                importJob = await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "filesystem.import.execute",
                        Source: "processor",
                        PayloadJson: JsonSerializer.Serialize(importPayload),
                        RelatedEntityType: resolvedMediaType == "tv" ? "series" : "movie",
                        RelatedEntityId: null,
                        IdempotencyKey: $"processor-output:{library.Id}:{Path.GetFullPath(outputPath).ToLowerInvariant()}"),
                    cancellationToken);

                await repository.UpdateProcessorHandoffAsync(
                    handoff.Id,
                    "completed",
                    outputPath,
                    importJob.Id,
                    null,
                    cancellationToken);

                await activityFeed.RecordActivityAsync(
                    "processing.completed.import-queued",
                    $"{processorName} produced a cleaned file; Deluno queued it for import.",
                    JsonSerializer.Serialize(new
                    {
                        request.LibraryId,
                        HandoffId = handoff.Id,
                        MediaType = resolvedMediaType,
                        EntityType = entityType,
                        EntityId = entityId,
                        request.SourcePath,
                        OutputPath = outputPath,
                        JobId = importJob.Id
                    }),
                    importJob.Id,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
            }

            return Results.Json(new { status, handoffId = handoff.Id, activityId = activity.Id, importJobId = importJob?.Id }, statusCode: StatusCodes.Status202Accepted);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateProcessorEvent(ProcessorEventRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var status = NormalizeProcessorStatus(request.Status);

        if (status == "completed" && string.IsNullOrWhiteSpace(request.OutputPath))
        {
            errors["outputPath"] = ["Completed processor events must include the cleaned output path."];
        }

        if (status == "failed" && string.IsNullOrWhiteSpace(request.Message))
        {
            errors["message"] = ["Failed processor events should explain what went wrong."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateProcessorConnection(
        string? name,
        string? submissionUrl,
        string? authHeaderName)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Give this processor connection a name."];
        }

        if (!Uri.TryCreate(submissionUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["submissionUrl"] = ["Use a valid http or https processor webhook URL."];
        }

        if (!string.IsNullOrWhiteSpace(authHeaderName) && authHeaderName.Any(char.IsWhiteSpace))
        {
            errors["authHeaderName"] = ["Authentication header names cannot contain spaces."];
        }

        return errors;
    }

    private static string NormalizeProcessorPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
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

    private static string NormalizeProcessorStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "accepted" => "accepted",
            "started" or "processing" => "started",
            "completed" or "processed" or "ready" => "completed",
            "failed" or "error" => "failed",
            _ => "accepted"
        };

    /// <summary>
    /// Queues the next slice of an import run.
    ///
    /// The dedupe key carries the run's current position, so a continuation and
    /// a resume sweep that both decide the same slice is next collapse into one
    /// job, while a genuine continuation is never mistaken for the job that is
    /// already running.

}

public sealed record ExternalIntegrationManifest(
    string Product,
    string Version,
    string InstanceName,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> RecommendedCategories,
    IReadOnlyList<ExternalLibraryManifest> Libraries,
    IReadOnlyList<ExternalIndexerManifest> Indexers,
    IReadOnlyList<ExternalDownloadClientManifest> DownloadClients,
    IReadOnlyList<ExternalConnectionManifest> Connections);

public sealed record ExternalLibraryManifest(
    string Id,
    string Name,
    string MediaType,
    string RootPath,
    string? DownloadsPath,
    string? QualityProfileName,
    string ImportWorkflow,
    string? ProcessorName,
    string? ProcessorOutputPath,
    int ProcessorTimeoutMinutes,
    string ProcessorFailureMode,
    bool MissingSearchEnabled,
    bool UpgradeSearchEnabled,
    int MaxItemsPerRun,
    string AutomationStatus);

public sealed record ExternalIndexerManifest(
    string Id,
    string Name,
    string Protocol,
    string MediaScope,
    int Priority,
    bool IsEnabled,
    string HealthStatus);

public sealed record ExternalDownloadClientManifest(
    string Id,
    string Name,
    string Protocol,
    string MoviesCategory,
    string TvCategory,
    string? CategoryTemplate,
    int Priority,
    bool IsEnabled,
    string HealthStatus);

public sealed record ExternalConnectionManifest(
    string Id,
    string Name,
    string ConnectionKind,
    string Role,
    string? EndpointUrl,
    bool IsEnabled);

public sealed record ExternalHealthResponse(
    string InstanceName,
    string Status,
    int LibraryCount,
    int EnabledIndexerCount,
    int EnabledDownloadClientCount,
    int ActiveJobCount,
    int ProblemCount,
    DateTimeOffset CheckedUtc);

public sealed record ExternalQueueResponse(
    IReadOnlyList<JobQueueItem> Jobs,
    IReadOnlyList<DownloadDispatchItem> Dispatches);

public sealed record ExternalTriggerRefreshRequest(
    string? MediaType,
    string? Reason);
