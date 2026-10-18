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
using Deluno.Libraries;
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
                InstanceName: Deluno.Contracts.DelunoHostName.Current,
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
                    "external-file-changed",
                    // A tool may ask Deluno to download a title again (#540).
                    "external-redownload",
                    "pre-import-processing",
                    ProcessorCapabilities.RejectRegrab,
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
                    library.AutomationStatus,
                    library.CleanupMode,
                    library.RemoveEmptySourceFolders)).ToArray(),
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
                InstanceName: Deluno.Contracts.DelunoHostName.Current,
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
                await jobs.RequestLibrarySearchAsync(
                    LibraryActionEndpointRouteBuilderExtensions.ToPlanItem(library), cancellationToken);
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
            var handoffs = await repository.ListProcessorHandoffsAsync(libraryId, take ?? 50, cancellationToken);

            // Beside each hand-off, whether its processor has heard what became
            // of the file (#580), so a person can see which are still owed.
            var outcomes = (await repository.ListProcessorOutcomesAsync(handoffs.Select(item => item.Id).ToArray(), cancellationToken))
                .ToDictionary(item => item.HandoffId, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(handoffs
                .Select(item => outcomes.TryGetValue(item.Id, out var outcome) ? item with { Outcome = outcome } : item)
                .ToArray());
        }).WithMetadata(new DelunoImportsScopeMayReadAttribute());

        // Say again what became of the file, from the start (#580). The
        // scheduled pass already does this for anything still owed; this is for
        // a person who has fixed what stopped it — a secret, a processor that was
        // off for longer than a week — or who wants to see it heard now.
        integrations.MapPost("/processors/handoffs/{id}/outcome/send-again", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IProcessorConnectionService processorConnections,
            IActivityFeedRepository activityFeed,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var record = await repository.GetProcessorOutcomeAsync(id, cancellationToken);
            if (record is null)
            {
                return Results.NotFound(new
                {
                    error = "nothing_recorded",
                    message = "Deluno has nothing recorded to tell the processor about this hand-off. An outcome is recorded when its import finishes."
                });
            }

            var sent = await ProcessorOutcomeDelivery.SendAgainAsync(
                repository,
                processorConnections,
                activityFeed,
                record,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return Results.Ok(new { message = ProcessorOutcomeDelivery.Describe(sent).Message, outcome = sent });
        });

        integrations.MapPost("/processors/handoffs/{id}/retry", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IActivityFeedRepository activityFeed,
            IProcessorHandoffSender? sender,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var handoff = await repository.GetProcessorHandoffAsync(id, cancellationToken);
            if (handoff is null) return Results.NotFound();
            if (!ProcessorHandoffStatuses.CanReleaseOriginal(handoff.Status))
            {
                return Results.Conflict(new
                {
                    message = "Only a hand-off the processor did not finish can be tried again. Deluno will not resubmit an active or already completed hand-off."
                });
            }

            // Offered only while the hand-off has no result (#1155). Once its
            // output has been collected or imported, the hand-off is closed:
            // sending the file again only makes the processor clean it twice.
            if (!string.IsNullOrWhiteSpace(handoff.OutputPath) || !string.IsNullOrWhiteSpace(handoff.ImportJobId) ||
                (await repository.GetProcessorOutcomeAsync(handoff.Id, cancellationToken))?.Outcome == ProcessorOutcomeRecord.Imported)
            {
                return Results.Conflict(new
                {
                    message = $"{handoff.ReleaseName} already has its processed file, so there is nothing to send again."
                });
            }

            var connection = await repository.FindProcessorConnectionAsync(handoff.ProcessorConnectionId, handoff.ProcessorName, cancellationToken);
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

            // "Deluno will try it again" is written only once the send goes ahead, or when it is left for the planner;
            // a refusal says why instead.
            Task SayWillTryAsync() => activityFeed.RecordActivityAsync(
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

            // The person pressed a button, so the send starts now, through the planner's own send (#1253). The planner
            // skips a hand-off being sent, so it goes out once. Without a sender the planner sends it on its next pass.
            if (sender is not null)
            {
                var send = await sender.SendNowAsync(handoff.Id, SayWillTryAsync, cancellationToken);
                if (send.Kind == ProcessorHandoffSendKind.Waiting)
                {
                    await SayWillTryAsync();
                }

                if (send.Kind == ProcessorHandoffSendKind.Refused)
                {
                    // Deluno's own check said no, and nothing was sent: it goes back to what it was, not left reading
                    // as waiting.
                    var now = await repository.GetProcessorHandoffAsync(handoff.Id, cancellationToken);
                    if (now is not null && string.Equals(now.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase))
                    {
                        await repository.UpdateProcessorHandoffAsync(handoff.Id, handoff.Status, null, null, handoff.FailureMessage, cancellationToken);
                    }

                    await activityFeed.RecordActivityAsync(
                        "processing.retry-refused",
                        $"Deluno did not try {handoff.ReleaseName} again. {send.Message}",
                        JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, ConnectionId = connection.Id, connection.Name }),
                        null,
                        "processor-handoff",
                        handoff.Id,
                        cancellationToken);

                    return Results.Conflict(new { message = send.Message });
                }

                if (send.Kind == ProcessorHandoffSendKind.ProcessorRefused)
                {
                    return Results.Conflict(new { message = send.Message });
                }

                retried = await repository.GetProcessorHandoffAsync(handoff.Id, cancellationToken) ?? retried;
            }
            else
            {
                await SayWillTryAsync();
            }

            return Results.Accepted($"/api/integrations/processors/handoffs?libraryId={Uri.EscapeDataString(handoff.LibraryId)}", retried);
        });

        // #914/#929: giving up ends the wait, but the processor may still be
        // doing something sensible with the file - a person who has seen the
        // reason may just want longer, without resubmitting anything. Unlike
        // /retry, this does not tell the processor about the file again; it
        // only resumes watching it, so the next pass reads a fresh answer.
        integrations.MapPost("/processors/handoffs/{id}/keep-waiting", async (
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
            if (!ProcessorHandoffStatuses.CanReleaseOriginal(handoff.Status))
            {
                return Results.Conflict(new
                {
                    message = "Only a hand-off Deluno stopped waiting on can be given a new limit. This one is still with the processor or has already finished."
                });
            }

            var resumed = await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                ProcessorHandoffStatuses.Started,
                null,
                null,
                null,
                cancellationToken);
            if (resumed is null) return Results.NotFound();

            await activityFeed.RecordActivityAsync(
                "processing.kept-waiting",
                $"You asked Deluno to keep waiting for {handoff.ReleaseName}. It will watch the hand-off again from here.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, PreviousStatus = handoff.Status }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            return Results.Ok(new { message = $"Deluno will keep waiting for {handoff.ReleaseName}.", handoff = resumed });
        });

        // Waiting is the processor's to end, until a person says otherwise (#566).
        //
        // Deluno waits while a processor says it is queued, scheduled or working,
        // however long that takes, and gives up only after the library's limit
        // measured from the last change or from the processor answering again -
        // so restarting the processor starts that wait over. On the rig four
        // hand-offs sat at "queued" for two days for work Weir no longer had,
        // and no screen could end one, because retry, import-original and
        // dismiss are all offered only once a hand-off has ended.
        integrations.MapPost("/processors/handoffs/{id}/stop-waiting", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IProcessorConnectionService processorConnections,
            IActivityFeedRepository activityFeed,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var handoff = await repository.GetProcessorHandoffAsync(id, cancellationToken);
            if (handoff is null) return Results.NotFound();
            if (!ProcessorHandoffStatuses.IsWithProcessor(handoff.Status))
            {
                return Results.Conflict(new
                {
                    message = "This hand-off has already ended, so there is nothing left to wait for."
                });
            }

            // What the processor was told is decided in one place, because
            // removing a title stops these waits too (#563) and the two must not
            // drift apart.
            var stopped = await ProcessorHandoffCancellation.StopWaitingAsync(
                repository,
                processorConnections,
                activityFeed,
                handoff,
                processorName => $"You stopped waiting for {processorName}",
                cancellationToken);
            if (stopped.Busy) return Results.Conflict(new { message = stopped.Message });
            if (stopped.Ended is null) return Results.NotFound();

            return Results.Ok(new { message = stopped.Message, handoff = stopped.Ended });
        });

        // A hand-off that ended badly stays on Transfers as something needing a
        // person until they say otherwise. Retry and Import original both need
        // something still to exist - a connection, a library, a file - and on the
        // rig a removed processor's hand-offs had none of them, so they could
        // never be cleared.
        integrations.MapPost("/processors/handoffs/{id}/dismiss", async (
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
            if (!ProcessorHandoffStatuses.CanDismiss(handoff.Status, handoff.FailureMessage))
            {
                return Results.Conflict(new
                {
                    message = "Only a hand-off that ended without a processed file can be dismissed. This one is still with the processor or has already finished."
                });
            }

            // The reason it ended stays on the record: dismissing says it needs
            // nobody, not that it did not happen.
            var dismissed = await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                ProcessorHandoffStatuses.Dismissed,
                null,
                null,
                handoff.FailureMessage,
                cancellationToken);
            if (dismissed is null) return Results.NotFound();

            await activityFeed.RecordActivityAsync(
                "processing.dismissed",
                $"{handoff.ReleaseName}'s hand-off to {handoff.ProcessorName ?? "the processor"} was dismissed.",
                JsonSerializer.Serialize(new
                {
                    HandoffId = handoff.Id,
                    handoff.LibraryId,
                    PreviousStatus = handoff.Status,
                    handoff.FailureMessage
                }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            return Results.Ok(dismissed);
        });

        // Import the untouched download instead of waiting for a processed copy
        // (#511). Offered wherever the processor did not finish: it failed, went
        // quiet, stalled, lost the hand-off or was told to stop.
        integrations.MapPost("/processors/handoffs/{id}/import-original", async (
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
            if (handoff is null) return Results.NotFound(new { error = "handoff_not_found", message = $"Deluno has no hand-off with the id {id}." });
            if (!ProcessorHandoffStatuses.CanReleaseOriginal(handoff.Status))
            {
                return Results.Conflict(new
                {
                    message = "The original can only be imported once the processor has stopped working on it. Deluno will not import a file the processor may still deliver."
                });
            }

            var released = await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                ProcessorHandoffStatuses.Released,
                null,
                null,
                $"Importing the original of {handoff.ReleaseName}, unprocessed, as requested.",
                cancellationToken);
            if (released is null) return Results.NotFound(new { error = "handoff_not_found", message = $"Deluno has no hand-off with the id {id}." });

            await activityFeed.RecordActivityAsync(
                "processing.released",
                $"Deluno will import the original of {handoff.ReleaseName} without processing, as requested.",
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, handoff.SourcePath, PreviousStatus = handoff.Status }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            return Results.Accepted($"/api/integrations/processors/handoffs?libraryId={Uri.EscapeDataString(handoff.LibraryId)}", released);
        });

        // "Give up and search for another release" (#914, #929): a real
        // action, not just words in a recovery case's recommendation. The
        // title comes from the download dispatch the hand-off's own
        // client/queue-item pair names -- the same way a Queue "mark as bad"
        // finds it -- so the reused ReplacementSearchQueuer points the new
        // search at the exact title (or, for a season-pack hand-off, exactly
        // the episodes it covered) rather than the whole library's next cycle.
        integrations.MapPost("/processors/handoffs/{id}/search-again", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IActivityFeedRepository activityFeed,
            IDownloadDispatchesRepository dispatchesRepository,
            IBlockedReleaseRepository blockedReleases,
            IJobScheduler jobScheduler,
            ILibrariesRepository librariesRepository,
            CancellationToken cancellationToken,
            IGivenUpDownloadRemoval? givenUpDownloads = null) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var handoff = await repository.GetProcessorHandoffAsync(id, cancellationToken);
            if (handoff is null) return Results.NotFound();
            if (!ProcessorHandoffStatuses.NeedsPerson(handoff.Status, handoff.FailureMessage))
            {
                return Results.Conflict(new
                {
                    message = "A replacement search can only be started once this hand-off has ended badly."
                });
            }

            var dispatch = await dispatchesRepository.FindDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken);
            if (dispatch is null || !ReplacementSearchQueuer.Supports(dispatch.EntityType))
            {
                return Results.Conflict(new
                {
                    message = "Deluno does not know which title this release was for, so it cannot search for a replacement."
                });
            }

            // #956: a processor that never received the file has a hand-off
            // problem, not a bad release (the same rule as #880 and #919) --
            // so a Lost hand-off ends and searches again without blocklisting
            // anything. Every other give-up ending still blocklists using the
            // #874 key: the release's hash/id and its name, on every indexer.
            // #1186: a download that was gone before the processor reached it says nothing about the release.
            var isSourceGone = string.Equals(handoff.Status, ProcessorHandoffStatuses.SourceGone, StringComparison.OrdinalIgnoreCase);
            var isLost = string.Equals(handoff.Status, ProcessorHandoffStatuses.Lost, StringComparison.OrdinalIgnoreCase);
            if (!isLost && !isSourceGone)
            {
                await blockedReleases.BlockAsync(
                    new BlockedRelease(
                        Guid.NewGuid().ToString("n"),
                        BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                        dispatch.ReleaseName,
                        dispatch.IndexerName,
                        dispatch.MediaType,
                        dispatch.EntityId,
                        handoff.ReleaseName,
                        ImportFailurePolicy.ProcessorRejected,
                        $"Gave up on {handoff.ProcessorName ?? "the processor"} and searched for another release.",
                        dispatch.TorrentHashOrItemId ?? handoff.QueueItemId,
                        dispatch.DownloadClientId,
                        dispatch.DownloadClientName,
                        DateTimeOffset.MinValue,
                        Origin: BlockedReleaseOrigins.Manual),
                    cancellationToken);
            }

            // The given-up download is still in the client, which keeps the title "on its way" and made the new
            // search skip itself for hours. It goes first: out of the client with its files (only a download Deluno
            // sent), its record closed. The search is a person's own and then runs at once.
            var removal = givenUpDownloads is null
                ? new GivenUpDownloadRemovalResult(false, null)
                : await givenUpDownloads.RemoveAsync(dispatch, cancellationToken);
            var removedNote = removal.Removed
                ? " Removed it from the download client with its files."
                : removal.Message is { Length: > 0 } why ? $" It could not be removed from the download client ({why}), so the search may wait until it is." : string.Empty;

            var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            await ReplacementSearchQueuer.EnqueueAsync(jobScheduler, dispatch, library?.Name ?? dispatch.LibraryId, cancellationToken);

            var message = isSourceGone
                ? $"The download of {handoff.ReleaseName} was gone before {handoff.ProcessorName ?? "the processor"} could clean it, so Deluno did not blocklist it. Started a new search.{removedNote}"
                : isLost
                ? $"{handoff.ProcessorName ?? "The processor"} never received {handoff.ReleaseName}, so Deluno did not blocklist it. Ended the hand-off and started a new search.{removedNote}"
                : $"You gave up on {handoff.ReleaseName}: blocklisted the release and started a new search.{removedNote}";
            var ended = await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                ProcessorHandoffStatuses.Dismissed,
                null,
                null,
                message,
                cancellationToken);
            if (ended is null) return Results.NotFound();

            await activityFeed.RecordActivityAsync(
                "processing.search-again",
                message,
                JsonSerializer.Serialize(new { HandoffId = handoff.Id, handoff.LibraryId, handoff.ReleaseName, PreviousStatus = handoff.Status }),
                null,
                "processor-handoff",
                handoff.Id,
                cancellationToken);

            return Results.Ok(new { message, handoff = ended });
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
            IProcessorConnectionService processorConnections,
            IProcessorRepository repository,
            [FromServices] IConnectionVersionStore? versions,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var errors = ValidateProcessorConnection(request.Name, request.SubmissionUrl, request.AuthHeaderName, request.HealthCheckUrl);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var machineName = await processorConnections.GetMachineNameAsync(request.SubmissionUrl ?? string.Empty, request.AuthHeaderName, request.Secret, cancellationToken);
            var productName = await processorConnections.GetProductNameAsync(request.SubmissionUrl ?? string.Empty, request.AuthHeaderName, request.Secret, cancellationToken);
            var created = await repository.CreateProcessorConnectionAsync(request with { MachineName = machineName, ProductName = productName }, cancellationToken);
            // A connection just made says which version it runs now (#1181).
            await Deluno.Connections.Versions.ConnectionVersionRecording.RememberVersionAsync(
                versions, ConnectionKinds.Processor, created.Id, ProcessorProjects.ProjectFor(created), await processorConnections.ReadVersionAsync(created, cancellationToken), cancellationToken);
            return Results.Ok(created);
        });

        integrations.MapPut("/processors/connections/{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateProcessorConnectionRequest request,
            IProcessorConnectionService processorConnections,
            IProcessorRepository repository,
            [FromServices] IConnectionVersionStore? versions,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var errors = ValidateProcessorConnection(request.Name, request.SubmissionUrl, request.AuthHeaderName, request.HealthCheckUrl);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var current = await repository.GetProcessorConnectionAsync(id, cancellationToken);
            var machineName = current is null ? null : await processorConnections.GetMachineNameAsync(request.SubmissionUrl ?? current.SubmissionUrl, request.AuthHeaderName ?? current.AuthHeaderName, string.IsNullOrWhiteSpace(request.Secret) ? current.Secret : request.Secret, cancellationToken);
            var productName = current is null ? null : await processorConnections.GetProductNameAsync(request.SubmissionUrl ?? current.SubmissionUrl, request.AuthHeaderName ?? current.AuthHeaderName, string.IsNullOrWhiteSpace(request.Secret) ? current.Secret : request.Secret, cancellationToken);
            var updated = await repository.UpdateProcessorConnectionAsync(id, request with { MachineName = machineName, ProductName = productName }, cancellationToken);
            if (updated is not null)
            {
                // A connection saved again (a new address or credential, a reinstalled processor) reports its version at once (#1181).
                await Deluno.Connections.Versions.ConnectionVersionRecording.RememberVersionAsync(
                    versions, ConnectionKinds.Processor, updated.Id, ProcessorProjects.ProjectFor(updated), await processorConnections.ReadVersionAsync(updated, cancellationToken), cancellationToken);
            }

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        // Deleting a processor takes the waiting with it (#567).
        //
        // A hand-off finds its processor by name, so a deleted connection means
        // "cannot be asked" - and after the library's limit, six hours by
        // default, every file still with it both times out and raises a case
        // naming the processor that was deliberately deleted. The files are
        // dropped first, while there is still something to ask, and the cases
        // about them settle with the connection.
        integrations.MapDelete("/processors/connections/{id}", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            IDeletedThingCleanup cleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var connection = await repository.GetProcessorConnectionAsync(id, cancellationToken);
            if (connection is null) return Results.NotFound();

            // Before the row goes, while there is still something to ask.
            await cleanup.ProcessorDeletedAsync(connection.Id, connection.Name, cancellationToken);

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
            IOutboundNotificationService notificationService,
            INotificationService notificationPreferences,
            [FromServices] IConnectionCheckRecorder? checks,
            [FromServices] IConnectionVersionStore? versions,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var connection = await repository.GetProcessorConnectionAsync(id, cancellationToken);
            if (connection is null) return Results.NotFound();
            var previousHealthStatus = connection.HealthStatus;
            var result = await processorConnections.TestAsync(connection, cancellationToken);
            await repository.RecordProcessorConnectionHealthAsync(connection.Id, result.Status, result.Message, cancellationToken);
            // The processor service stores its version whenever it reads the processor's health (#1200).
            if (result.IsReachable)
            {
                connection = await ProcessorAutoNames.RenameIfGeneratedAsync(connection, repository, processorConnections, cancellationToken);
            }
            if (checks is not null)
            {
                await checks.RecordAsync(ConnectionKinds.Processor, connection.Id, connection.Name, result.IsReachable, result.LatencyMs, result.Message, cancellationToken);
            }
            await activityFeed.RecordActivityAsync(
                "processor.connection.tested",
                $"{connection.Name}: {result.Message}",
                JsonSerializer.Serialize(new { connection.Id, connection.Name, connection.Provider, result.Status, result.StatusCode, result.LatencyMs }),
                null,
                "processor-connection",
                connection.Id,
                cancellationToken);
            await DispatchProcessorHealthChangeAsync(
                notificationService,
                notificationPreferences,
                $"Processor {connection.Name}",
                previousHealthStatus,
                result.Status,
                result.Message,
                cancellationToken);
            return Results.Ok(result);
        });

        // Optional path pairs for one processor connection (#962), the same
        // "Deluno sees" / "processor sees" shape download clients already
        // offer at /api/connections/download-clients/{id}/path-mappings.
        integrations.MapGet("/processors/connections/{id}/path-mappings", async (
            string id,
            HttpContext httpContext,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            return Results.Ok(await repository.ListProcessorPathMappingsAsync(id, cancellationToken));
        });

        integrations.MapPost("/processors/connections/{id}/path-mappings", async (
            string id,
            HttpContext httpContext,
            [FromBody] CreateProcessorPathMappingRequest request,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(request.DelunoPath) || string.IsNullOrWhiteSpace(request.ProcessorPath))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pathMapping"] = ["Both the Deluno path and the processor path are required."]
                });
            }

            var item = await repository.CreateProcessorPathMappingAsync(id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        integrations.MapDelete("/processors/connections/{id}/path-mappings/{mappingId}", async (
            string id,
            string mappingId,
            HttpContext httpContext,
            IProcessorRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var removed = await repository.DeleteProcessorPathMappingAsync(id, mappingId, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // The only way anything in a processor output folder is ever removed
        // by Deluno itself (#921): a safety-net item a person chose to delete
        // from System -> Health, never automatic. Scoped to one direct child
        // of the folder the library actually reads processed output from, so
        // neither a stray "../" nor a stale request can reach anything else.
        integrations.MapPost("/processors/output/delete", async (
            HttpContext httpContext,
            [FromBody] ProcessorOutputItemRequest request,
            ILibrariesRepository librariesRepository,
            IHealthCheckRepository healthCheckRepository,
            IActivityFeedRepository activityFeed,
            IRecycleBinService recycleBin,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var allLibraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = allLibraries
                .FirstOrDefault(item => string.Equals(item.Id, request.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null || string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
            {
                return Results.NotFound(new { message = "That library, or its processor output folder, no longer exists." });
            }

            string root;
            string fullPath;
            try
            {
                root = Path.GetFullPath(library.ProcessorOutputPath);
                fullPath = Path.GetFullPath(request.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { message = "That is not a usable path." });
            }

            if (!string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Deluno only removes an item directly inside a processor output folder this way." });
            }

            // #1325: while a folder overlaps a library, Clear does nothing; and it never touches a library's root,
            // anything inside one, or a folder that holds one, whatever the output folder is set to.
            if (LibraryFolderLayout.CleanupBlockedReason(allLibraries) is { } blocked)
            {
                return Results.Conflict(new { message = blocked });
            }

            if (LibraryFolderLayout.ReachesALibrary(allLibraries, fullPath) || LibraryFolderLayout.ReachesALibrary(allLibraries, root))
            {
                return Results.BadRequest(new { message = "That is inside a library, so Deluno will not remove it. Library media never leaves through Clear." });
            }

            var name = Path.GetFileName(fullPath);
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                return Results.NotFound(new { message = "That is already gone." });
            }

            // Never deleted outright: the item goes to the recycle bin, where it can be restored.
            var stored = await recycleBin.StoreStrayOutputAsync(library, fullPath, cancellationToken);
            if (stored is null)
            {
                return Results.Conflict(new { message = $"Deluno could not move {name} to the recycle bin, so it was left where it is." });
            }

            var message = stored.IsDirectory
                ? $"Moved the folder {name} from the {library.Name} library's processor output folder to the recycle bin."
                : $"Moved {name} from the {library.Name} library's processor output folder to the recycle bin.";

            await healthCheckRepository.RemoveAsync($"processor-output:{library.Id}:{name.ToLowerInvariant()}", cancellationToken);
            await activityFeed.RecordActivityAsync(
                "processing.output.deleted",
                message,
                JsonSerializer.Serialize(new { LibraryId = library.Id, LibraryName = library.Name, Path = fullPath }),
                null,
                "library",
                library.Id,
                cancellationToken);
            return Results.Ok(new { message });
        });

        integrations.MapPost("/processors/events", async (
            HttpContext httpContext,
            [FromBody] ProcessorEventRequest request,
            IProcessorRepository repository,
            ILibrariesRepository librariesRepository,
            IActivityFeedRepository activityFeed,
            IJobScheduler jobScheduler,
            IProcessorFailureReportHandler failureReports,
            IProcessorOutputImportQueue outputImports,
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

            // Validation above refuses a status Deluno does not understand.
            var status = NormalizeProcessorStatus(request.Status)!;
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

            if (!ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                return Results.Conflict(new
                {
                    message = $"{library.Name} is configured for standard import, so Deluno will not accept a processor completion event for it."
                });
            }

            // The processor reports every path in its own filesystem namespace.
            // Translate its sourcePath, outputPath and outputFiles back to the
            // namespace Deluno already recorded the hand-off in (#962) before
            // anything below compares or imports them. With no mapping pairs
            // configured for this connection, every path passes through
            // unchanged.
            var mappingConnection = !string.IsNullOrWhiteSpace(library.ProcessorConnectionId) || !string.IsNullOrWhiteSpace(library.ProcessorName)
                ? await repository.FindProcessorConnectionAsync(library.ProcessorConnectionId, library.ProcessorName, cancellationToken)
                : null;
            IReadOnlyList<ProcessorPathMappingItem> pathMappings = mappingConnection is not null
                ? await repository.ListProcessorPathMappingsAsync(mappingConnection.Id, cancellationToken)
                : [];
            if (pathMappings.Count > 0)
            {
                request = request with
                {
                    SourcePath = ProcessorPathMappingTranslator.ToDeluno(request.SourcePath, pathMappings),
                    OutputPath = ProcessorPathMappingTranslator.ToDeluno(request.OutputPath, pathMappings),
                    OutputFiles = request.OutputFiles?
                        .Select(file => ProcessorPathMappingTranslator.ToDeluno(file, pathMappings))
                        .OfType<string>()
                        .ToArray()
                };
            }

            if (string.IsNullOrWhiteSpace(request.OutputPath) &&
                ProcessorOutputPathPolicy.OutputPathFromFiles(request.OutputFiles) is { } derivedOutputPath)
            {
                request = request with { OutputPath = derivedOutputPath };
            }

            var processorName = library.ProcessorName ?? (string.IsNullOrWhiteSpace(request.ProcessorName)
                ? "External processor"
                : request.ProcessorName.Trim());
            var entityType = string.IsNullOrWhiteSpace(request.EntityType) ? "processor" : request.EntityType.Trim();
            var entityId = string.IsNullOrWhiteSpace(request.EntityId) ? null : request.EntityId.Trim();
            var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
            var mediaType = library.MediaType;

            // Partial success: the processor gives up on the rest of a hand-off
            // but names the files it did finish. Those still belong in the
            // library - refusing them because the batch as a whole failed would
            // throw away work the processor already did (#619).
            var partialImport = status == "failed" &&
                request.OutputFiles is { Count: > 0 } &&
                !string.IsNullOrWhiteSpace(request.OutputPath);
            var importsOutput = status == "completed" || partialImport;

            // Resolved before the output-path checks below, purely so a
            // rejection can say which hand-off it was about and tell a person
            // right away (#891) instead of only via the multi-day stall
            // timeout that judges a hand-off nothing has been heard about.
            var handoff = await repository.FindProcessorHandoffAsync(
                library.Id,
                request.HandoffId,
                request.SourcePath,
                cancellationToken);

            // A release sent again gives its source path a new hand-off id, while the processor still holds
            // the old one and reports against it (#1208). That report belongs to the older send: it is settled
            // there, answered as already handled, and never touches the hand-off that replaced it, which settles
            // from its own report. It is not a 409: nothing is wrong with the report.
            if (handoff is null && !string.IsNullOrWhiteSpace(request.HandoffId) &&
                await repository.FindSupersededProcessorHandoffAsync(library.Id, request.HandoffId, cancellationToken) is { } older)
            {
                // A completed report that carries the output, for a send that was replaced while the processor had
                // not started it, is the processor's answer for this very file (#1232): the live send for the same
                // source path is still waiting for it, so the live send takes it. One import, and both sends are
                // settled: the older one here, the live one by the import it queues below.
                var live = status == "completed" && !string.IsNullOrWhiteSpace(request.OutputPath)
                    ? await repository.GetProcessorHandoffAsync(older.SupersededById, cancellationToken)
                    : null;
                if (live is not null &&
                    string.Equals(live.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase) &&
                    ProcessorHandoffStatuses.IsWithProcessor(live.Status))
                {
                    await repository.SettleSupersededProcessorHandoffAsync(older.Id, status, cancellationToken);
                    await activityFeed.RecordActivityAsync(
                        "processing.superseded-adopted",
                        $"{processorName} finished {live.ReleaseName} for the send Deluno had replaced when the release was sent again. It is the same file, so Deluno used it for the new send.",
                        JsonSerializer.Serialize(new { request.LibraryId, HandoffId = live.Id, ReplacedHandoffId = older.Id, request.OutputPath, ProcessorName = processorName }),
                        null,
                        "processor-handoff",
                        live.Id,
                        cancellationToken);
                    request = request with { HandoffId = live.Id };
                    handoff = live;
                }
                else
                {
                    if (await repository.SettleSupersededProcessorHandoffAsync(older.Id, status, cancellationToken))
                    {
                        await activityFeed.RecordActivityAsync(
                            "processing.superseded-settled",
                            $"{processorName} reported on an earlier send of {older.ReleaseName} ({status}). That send was replaced when the release was sent again, so nothing changed: the new send settles from its own report.",
                            JsonSerializer.Serialize(new { request.LibraryId, HandoffId = older.Id, SupersededById = older.SupersededById, ReportStatus = status, ProcessorName = processorName }),
                            null,
                            "processor-handoff",
                            older.SupersededById,
                            cancellationToken);
                    }

                    return Results.Json(
                        new { status = "superseded", handoffId = older.Id, alreadyHandled = true },
                        statusCode: StatusCodes.Status202Accepted);
                }
            }

            if (importsOutput && !ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(request.OutputPath, library.ProcessorOutputPath))
            {
                if (handoff is not null)
                {
                    await TellOutputMisplacedOnceAsync(
                        activityFeed,
                        handoff,
                        library,
                        $"{processorName} said {handoff.ReleaseName} was ready at {request.OutputPath}, but that is outside the {library.Name} library's configured clean output folder. Check the processor's path mapping, then have it send the event again.",
                        cancellationToken);
                }

                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["outputPath"] = ["Completed output must be inside this library's configured clean output folder. Deluno did not queue an import."]
                });
            }

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

            // A send a person removed and asked for again (#1242). The processor had started it and reports it after the
            // release was sent again but before Deluno made the new send's hand-off, so there is no superseded record
            // to find it by yet. The report belongs to the newer send, which settles from its own: it is answered as
            // already handled, never a 409, and nothing about this ended hand-off changes. The same when Deluno itself
            // closed the hand-off by taking the download out of its client: nobody told the processor, a resend may
            // follow seconds after this report (CI run 39104138771: six), and Deluno ended the wait on purpose, so a
            // finished report is not a mistake to refuse. A hand-off nobody sent again, and that was not closed by a
            // removal, keeps the "too late" answer below.
            if (!ProcessorHandoffStatuses.AcceptsLateOutput(handoff.Status) &&
                !string.Equals(handoff.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase) &&
                ((string.Equals(handoff.Status, ProcessorHandoffStatuses.Dismissed, StringComparison.OrdinalIgnoreCase)
                    && ProcessorHandoffRemovalReasons.IsOne(handoff.FailureMessage)) ||
                 await outputImports.WasSentAgainAsync(handoff, cancellationToken)))
            {
                return Results.Json(
                    new { status = "superseded", handoffId = handoff.Id, alreadyHandled = true },
                    statusCode: StatusCodes.Status202Accepted);
            }

            // The event names the hand-off and the file as two separate claims,
            // and being inside the output folder only says the processor wrote
            // where it was told. A file that is recognisably another download's
            // is refused rather than imported under this one's title (#573).
            if (importsOutput &&
                !ProcessorOutputPathPolicy.IsOutputAbout(request.OutputPath, library.ProcessorOutputPath, handoff.SourcePath))
            {
                var claimedBy = (await repository.ListProcessorHandoffsAsync(library.Id, 200, cancellationToken))
                    .FirstOrDefault(other => other.Id != handoff.Id &&
                        ProcessorOutputPathPolicy.IsOutputAbout(request.OutputPath, library.ProcessorOutputPath, other.SourcePath));
                if (claimedBy is not null)
                {
                    // #891: told right away rather than left to the stall
                    // timeout, the same as the misplaced-output case above.
                    await TellOutputMisplacedOnceAsync(
                        activityFeed,
                        handoff,
                        library,
                        $"{processorName} said {handoff.ReleaseName} was ready at {request.OutputPath}, but that file is {claimedBy.ReleaseName}, not {handoff.ReleaseName}. Deluno did not import it.",
                        cancellationToken);

                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["outputPath"] = [$"That file is {claimedBy.ReleaseName}, not {handoff.ReleaseName}. Deluno did not queue an import."]
                    });
                }
            }

            // A hand-off is judged by its main video. The processor skips or refuses small extras (a 12 MB
            // featurette under its own size floor) and reports the hand-off as partly failed; when every main
            // video was delivered the hand-off succeeded, and the extras are recorded in its details.
            string? extrasNote = null;
            string[] extrasLeftOut = [];
            if (partialImport && ProcessorMainVideoJudgement.Judge(handoff.SourcePath, request.OutputFiles, processorName) is { MainVideoDelivered: true } judged)
            {
                status = "completed";
                partialImport = false;
                extrasNote = judged.Note;
                extrasLeftOut = judged.Extras.ToArray();
            }

            // The same, when the processor reports the skipped extra on its own after the hand-off was completed:
            // the film is delivered and imported, so a later "failed" for one small file does not undo it.
            if (status == "failed" && !importsOutput &&
                string.Equals(handoff.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase) &&
                ProcessorMainVideoJudgement.Judge(handoff.SourcePath, ProcessorMainVideoJudgement.OutputsAt(handoff.OutputPath), processorName).MainVideoDelivered)
            {
                var skipped = $"{processorName} left a small extra file of {handoff.ReleaseName} out{(message is null ? "." : $": {message}")} The main video was delivered and imported, so the hand-off stays complete.";
                await activityFeed.RecordActivityAsync(
                    "processing.extras-left-out",
                    skipped,
                    JsonSerializer.Serialize(new { request.LibraryId, HandoffId = handoff.Id, Message = message, ProcessorName = processorName }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                return Results.Json(
                    new { status = "completed", handoffId = handoff.Id, alreadyHandled = true, extrasLeftOut = true },
                    statusCode: StatusCodes.Status202Accepted);
            }

            // A report already acted on is answered the same way and does
            // nothing new, so a processor that repeats itself - or a worker that
            // restarts mid-send - does not import the same file twice. The job
            // queue cannot answer this on its own: its duplicate rule
            // deliberately ignores completed jobs, so that a release can be
            // imported again later on purpose.
            if (status == "completed" &&
                string.Equals(handoff.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase) &&
                handoff.OutputPath is { Length: > 0 } &&
                string.Equals(NormalizeProcessorPath(handoff.OutputPath ?? string.Empty), NormalizeProcessorPath(request.OutputPath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { status = "completed", handoffId = handoff.Id, alreadyHandled = true, importJobId = handoff.ImportJobId },
                    statusCode: StatusCodes.Status202Accepted);
            }

            // Deluno is the main stop against duplicate processing (#1155). A
            // processor that sends an output for a download whose file Deluno
            // has already imported -- a safety-net skip that reuses an output
            // already collected, a second pass over a file it cleaned through
            // its own scan -- is answered like any report already acted on, and
            // its copy is deleted. Nothing is blocklisted, removed from the
            // client or searched for.
            var known = handoff;
            async Task<IResult> AnswerAlreadyImportedAsync()
            {
                var deletedCopy = ProcessorOutputPathPolicy.DeleteSecondCopy(request.OutputPath, library.ProcessorOutputPath, known.SourcePath, await librariesRepository.ListLibrariesAsync(cancellationToken));
                var closing = !string.Equals(known.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase);
                if (closing)
                {
                    await repository.UpdateProcessorHandoffAsync(known.Id, ProcessorHandoffStatuses.Completed, request.OutputPath, null, null, cancellationToken);
                }

                if (closing || deletedCopy)
                {
                    await activityFeed.RecordActivityAsync(
                        "processing.output.already-imported",
                        deletedCopy
                            ? $"{processorName} sent a second copy of {known.ReleaseName}, which Deluno had already imported. Deluno deleted the second copy."
                            : $"{processorName} reported {known.ReleaseName}, which Deluno had already imported. There was nothing to import.",
                        JsonSerializer.Serialize(new { request.LibraryId, HandoffId = known.Id, request.OutputPath, Deleted = deletedCopy }),
                        null,
                        "processor-handoff",
                        known.Id,
                        cancellationToken);
                }

                return Results.Json(
                    new { status = "completed", handoffId = known.Id, alreadyHandled = true, alreadyImported = true },
                    statusCode: StatusCodes.Status202Accepted);
            }

            if (importsOutput && await outputImports.WasAlreadyImportedAsync(handoff, cancellationToken))
            {
                return await AnswerAlreadyImportedAsync();
            }

            // An ending is an ending. Deluno stopped waiting for this file, or a
            // person did, and a report arriving afterwards must not quietly
            // start an import for a hand-off that has been closed and cleared.
            if (importsOutput && !ProcessorHandoffStatuses.IsWithProcessor(handoff.Status))
            {
                return Results.Conflict(new
                {
                    // A second completion of one already completed is not a late
                    // report: it is a processor reporting file by file, and saying
                    // so is what lets it be put right (#619).
                    message = string.Equals(handoff.Status, ProcessorHandoffStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                        ? $"{handoff.ReleaseName} was already completed with {handoff.OutputPath}, so this second completion was not acted on. A hand-off is completed once, naming every output (OutputPath the folder, OutputFiles each file)."
                        : $"Deluno stopped waiting for {handoff.ReleaseName}, which ended {handoff.Status}, so this report is too late. No import was queued.",
                    handoffId = handoff.Id,
                    handoff.Status
                });
            }

            // A processor that deleted its copy is waiting on this answer before
            // it counts the deletion as safe (#497). A repeat of a report already
            // acted on is answered the same way and does nothing new: a worker
            // restart must not refuse twice or search twice.
            var disposition = status == "failed" ? ProcessorDispositions.Normalize(request.Disposition) : null;
            if (disposition == ProcessorDispositions.Rejected)
            {
                if (string.Equals(handoff.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        new { status = "rejected", handoffId = handoff.Id, alreadyHandled = true, message = handoff.FailureMessage },
                        statusCode: StatusCodes.Status202Accepted);
                }

                var outcome = await failureReports.HandleRejectedAsync(
                    new ProcessorFailureReport(library, handoff, processorName, message ?? "No reason given.", request.FailureClass, request.SourceRemoved),
                    cancellationToken);
                if (!outcome.Acted)
                {
                    await activityFeed.RecordActivityAsync(
                        "processing.rejected.not-acted",
                        outcome.Message,
                        JsonSerializer.Serialize(new { request.LibraryId, HandoffId = handoff.Id, handoff.ReleaseName, Message = message }),
                        null,
                        "processor-handoff",
                        handoff.Id,
                        cancellationToken);
                    return Results.Conflict(new { message = outcome.Message, handoffId = handoff.Id });
                }

                await repository.UpdateProcessorHandoffAsync(handoff.Id, "rejected", null, null, outcome.Message, cancellationToken);
                var rejectedActivity = await activityFeed.RecordActivityAsync(
                    "processing.rejected",
                    outcome.Message,
                    JsonSerializer.Serialize(new
                    {
                        request.LibraryId,
                        HandoffId = handoff.Id,
                        handoff.ReleaseName,
                        Message = message,
                        request.FailureClass,
                        outcome.BlockedReleaseId,
                        outcome.SearchJobId,
                        ProcessorName = processorName
                    }),
                    outcome.SearchJobId,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                return Results.Json(
                    new { status = "rejected", handoffId = handoff.Id, activityId = rejectedActivity.Id, outcome.BlockedReleaseId, outcome.SearchJobId },
                    statusCode: StatusCodes.Status202Accepted);
            }

            // The download disappeared before the processor could work on it (#1186). There is no file to try
            // again and no release to blame: Deluno closes it when it removed the download itself or the title
            // has its file, and otherwise asks a person only for a new copy.
            if (status == "failed" && disposition is null && ProcessorFailureClasses.IsSourceGone(request.FailureClass))
            {
                if (handoff.Status is ProcessorHandoffStatuses.Completed or ProcessorHandoffStatuses.Released
                    or ProcessorHandoffStatuses.Dismissed or ProcessorHandoffStatuses.SourceGone)
                {
                    return Results.Json(
                        new { status = "failed", handoffId = handoff.Id, alreadyHandled = true },
                        statusCode: StatusCodes.Status202Accepted);
                }

                var gone = await failureReports.HandleSourceGoneAsync(
                    new ProcessorFailureReport(library, handoff, processorName, message ?? "The download is no longer there.", request.FailureClass),
                    cancellationToken);
                var goneMessage = ProcessorFailureClasses.SourceGoneMessage(processorName);
                await repository.UpdateProcessorHandoffAsync(
                    handoff.Id,
                    gone.Settled ? ProcessorHandoffStatuses.Dismissed : ProcessorHandoffStatuses.SourceGone,
                    null,
                    null,
                    goneMessage,
                    cancellationToken);
                var goneActivity = await activityFeed.RecordActivityAsync(
                    gone.Settled ? "processing.source-gone.settled" : "processing.source-gone",
                    gone.Settled ? $"{goneMessage} {gone.Reason}" : goneMessage,
                    JsonSerializer.Serialize(new
                    {
                        request.LibraryId,
                        HandoffId = handoff.Id,
                        handoff.ReleaseName,
                        request.FailureClass,
                        Message = message,
                        ProcessorName = processorName,
                        Settled = gone.Settled
                    }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                return Results.Json(
                    new { status = "failed", handoffId = handoff.Id, activityId = goneActivity.Id, settled = gone.Settled },
                    statusCode: StatusCodes.Status202Accepted);
            }

            // Another connection owns the file, so the processor did not take this hand-off (#1250). Nothing is wrong
            // with the release and the processor holds nothing: it is sent again after a back-off, and only after the
            // last try does it become one problem a person can clear. Never held, refused, blocklisted or touched.
            if (status == "failed" && disposition is null && ProcessorFailureClasses.IsNotTaken(request.FailureClass))
            {
                // A hand-off already ended (the person or Deluno closed it, the download was removed) stays as it is, and
                // a repeat of a report already acted on is not another try.
                if (!ProcessorHandoffStatuses.IsWithProcessor(handoff.Status)
                    || (string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
                        && ProcessorNotTaken.IsBusyMessage(handoff.FailureMessage)))
                {
                    return Results.Json(
                        new { status = "failed", handoffId = handoff.Id, alreadyHandled = true },
                        statusCode: StatusCodes.Status202Accepted);
                }

                var tries = ProcessorNotTaken.TriesSoFar(
                    await activityFeed.ListActivityAsync(200, "processor-handoff", handoff.Id, cancellationToken));
                var willRetry = tries < ProcessorNotTaken.RetryAfter.Count;
                var notTakenMessage = willRetry
                    ? ProcessorNotTaken.BusyMessage(processorName)
                    : ProcessorNotTaken.GaveUpMessage(processorName, handoff.ReleaseName);
                await repository.UpdateProcessorHandoffAsync(
                    handoff.Id,
                    willRetry ? ProcessorHandoffStatuses.Waiting : ProcessorHandoffStatuses.Failed,
                    null,
                    null,
                    notTakenMessage,
                    cancellationToken);
                var retryAfter = willRetry ? ProcessorNotTaken.RetryAfter[tries] : (TimeSpan?)null;
                var notTakenActivity = await activityFeed.RecordActivityAsync(
                    willRetry ? "processing.not-taken" : "processing.not-taken.gave-up",
                    notTakenMessage,
                    JsonSerializer.Serialize(new
                    {
                        request.LibraryId,
                        HandoffId = handoff.Id,
                        handoff.ReleaseName,
                        request.FailureClass,
                        Message = message,
                        ProcessorName = processorName,
                        Attempt = tries + 1,
                        RetryAfterMinutes = retryAfter?.TotalMinutes
                    }),
                    null,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);
                return Results.Json(
                    new
                    {
                        status = "failed",
                        handoffId = handoff.Id,
                        activityId = notTakenActivity.Id,
                        willRetry,
                        retryAfterMinutes = retryAfter?.TotalMinutes
                    },
                    statusCode: StatusCodes.Status202Accepted);
            }

            await repository.UpdateProcessorHandoffAsync(
                handoff.Id,
                status,
                importsOutput ? request.OutputPath : null,
                null,
                status == "failed" ? message : null,
                cancellationToken);

            if (disposition == ProcessorDispositions.Held)
            {
                await failureReports.HandleHeldAsync(
                    new ProcessorFailureReport(library, handoff, processorName, message ?? "No reason given.", request.FailureClass),
                    cancellationToken);
            }

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

            ProcessorOutputImport? importJob = null;
            if (importsOutput && !string.IsNullOrWhiteSpace(request.OutputPath))
            {
                // Named from the catalogue, into the library that grabbed it: the
                // same queue the output-folder watcher uses (#526).
                var resolvedMediaType = library.MediaType;
                var outputPath = request.OutputPath.Trim();
                importJob = await outputImports.QueueAsync(library, handoff, outputPath, "processor", cancellationToken);
                if (importJob.AlreadyImported)
                {
                    // This very file was imported by an earlier request (a resent release whose output the first send
                    // already had imported): answered like any report already acted on, never imported twice (#1226).
                    return await AnswerAlreadyImportedAsync();
                }

                // Every file the processor says it made has to be there to be
                // imported; one that is not is said, not passed over (#619).
                var missing = (request.OutputFiles ?? []).Where(file => !string.IsNullOrWhiteSpace(file) && !File.Exists(file.Trim())).Select(file => file.Trim()).ToArray();
                var notImported = importJob.NotImportedBecause is { } removed
                    ? $"{processorName} produced a cleaned file, but {removed}, so Deluno did not import it. The file is still at {outputPath}."
                    : missing.Length > 0
                        ? $"{processorName} named {missing.Length} of its {request.OutputFiles!.Count} output files that Deluno cannot find: {string.Join(", ", missing.Select(Path.GetFileName))}. Deluno imported what is there."
                        // The processor gave up on the rest, not on these: they
                        // still land in the library, and the processor's own
                        // count of what failed travels with them rather than
                        // being guessed at again (#619).
                        : partialImport
                            ? $"{message} Deluno imported the {request.OutputFiles!.Count} file(s) that {processorName} said succeeded."
                            : extrasNote;

                await repository.UpdateProcessorHandoffAsync(
                    handoff.Id,
                    partialImport ? "failed" : "completed",
                    outputPath,
                    importJob.JobId,
                    notImported,
                    cancellationToken);

                await activityFeed.RecordActivityAsync(
                    importJob.NotImportedBecause is not null ? "filesystem.import.not-imported"
                        : missing.Length > 0 ? "processing.outputs-missing"
                        : partialImport ? "processing.partial-import"
                        : extrasNote is not null ? "processing.extras-left-out"
                        : "processing.completed.import-queued",
                    notImported ?? $"{processorName} produced a cleaned file; Deluno queued it for import.",
                    JsonSerializer.Serialize(new
                    {
                        request.LibraryId,
                        HandoffId = handoff.Id,
                        MediaType = resolvedMediaType,
                        EntityType = entityType,
                        EntityId = entityId,
                        request.SourcePath,
                        OutputPath = outputPath,
                        JobId = importJob.JobId,
                        importJob.DispatchId,
                        ExtrasLeftOut = extrasLeftOut,
                        ProcessorMessage = extrasNote is null ? null : message
                    }),
                    importJob.JobId,
                    "processor-handoff",
                    handoff.Id,
                    cancellationToken);

                // Named in Activity above; also shown where a person can act on
                // it -- the title/season page and Health -- not only there (#930).
                if (missing.Length > 0)
                {
                    await failureReports.HandleOutputsMissingAsync(
                        new ProcessorMissingOutputReport(
                            library,
                            entityId,
                            processorName,
                            handoff.ReleaseName,
                            outputPath,
                            missing,
                            request.OutputFiles!.Count),
                        cancellationToken);
                }
            }

            return Results.Json(new { status, handoffId = handoff.Id, activityId = activity.Id, importJobId = importJob?.JobId }, statusCode: StatusCodes.Status202Accepted);
        }).AddEndpointFilter(ProcessorEventAnswerLog.LogAsync);

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateProcessorEvent(ProcessorEventRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var status = NormalizeProcessorStatus(request.Status);

        if (status is null)
        {
            errors["status"] = ["Deluno did not understand that status. Report accepted, started, completed or failed."];
            return errors;
        }

        if (status == "completed" && string.IsNullOrWhiteSpace(request.OutputPath) &&
            !(request.OutputFiles is { Count: > 0 } files && files.Any(file => !string.IsNullOrWhiteSpace(file))))
        {
            errors["outputPath"] = ["Completed processor events must include the cleaned output path."];
        }

        if (status == "failed" && string.IsNullOrWhiteSpace(request.Message))
        {
            errors["message"] = ["Failed processor events should explain what went wrong."];
        }

        // OutputFiles without an OutputPath is accepted (Weir reports a partly failed hand-off that way, golden
        // path rc.30.9): the folder is worked out from the files themselves once their paths are translated.

        // How much one report may say (#1023), before Deluno looks for any of it.
        foreach (var (field, messages) in ProcessorEventLimits.Check(request.OutputFiles))
        {
            errors[field] = messages;
        }

        return errors;
    }

    /// <summary>
    /// Processor connections were tested and their health recorded, but
    /// nothing ever told the outbound path about it — the
    /// "health.degraded"/"health.restored" pair (#859) existed only for
    /// indexers and download clients. This adds the same pair here, gated by
    /// the same single Provider Health Changed toggle every provider type
    /// shares.
    /// </summary>
    private static async Task DispatchProcessorHealthChangeAsync(
        IOutboundNotificationService notificationService,
        INotificationService notificationPreferences,
        string providerName,
        string previousHealthStatus,
        string newHealthStatus,
        string? message,
        CancellationToken cancellationToken)
    {
        var isHealthy = string.Equals(newHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (!isHealthy)
        {
            // Matches the indexer/download-client pattern: every failing Test
            // reports "health.degraded", not only a genuine transition.
            var preferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (!preferences.ProviderHealthChangedEnabled)
            {
                return;
            }

            await notificationService.DispatchAsync(
                "health.degraded",
                $"{providerName} degraded",
                message ?? $"{providerName} is unhealthy.",
                null,
                cancellationToken);
            return;
        }

        var wasHealthy = string.Equals(previousHealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (wasHealthy)
        {
            return;
        }

        var restoredPreferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
        if (!restoredPreferences.ProviderHealthChangedEnabled)
        {
            return;
        }

        await notificationService.DispatchAsync(
            "health.restored",
            $"{providerName} is healthy again",
            message ?? $"{providerName} recovered.",
            null,
            cancellationToken);
    }

    private static Dictionary<string, string[]> ValidateProcessorConnection(
        string? name,
        string? submissionUrl,
        string? authHeaderName,
        string? healthCheckUrl = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!Uri.TryCreate(submissionUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["submissionUrl"] = ["Use a valid http or https processor webhook URL."];
        }

        if (!string.IsNullOrWhiteSpace(authHeaderName) && authHeaderName.Any(char.IsWhiteSpace))
        {
            errors["authHeaderName"] = ["Authentication header names cannot contain spaces."];
        }

        if (!string.IsNullOrWhiteSpace(healthCheckUrl) &&
            (!Uri.TryCreate(healthCheckUrl.Trim(), UriKind.Absolute, out var healthUri) ||
             (healthUri.Scheme != Uri.UriSchemeHttp && healthUri.Scheme != Uri.UriSchemeHttps)))
        {
            errors["healthCheckUrl"] = ["Use a valid http or https address for the health check, or leave it empty."];
        }

        return errors;
    }

    /// <summary>
    /// #891: a processor event rejected because its output isn't where Deluno
    /// expects is told on Activity immediately, rather than only surfacing
    /// once the hand-off's ordinary stall timeout gives up on it (which can
    /// take up to a week). Nothing is blocklisted and no file is touched --
    /// this is only ever a notice. Told once per hand-off rather than on
    /// every retry of the same broken report.
    /// </summary>
    private static async Task TellOutputMisplacedOnceAsync(
        IActivityFeedRepository activityFeed,
        ProcessorHandoffItem handoff,
        LibraryItem library,
        string message,
        CancellationToken cancellationToken)
    {
        const string category = "processing.output.misplaced";
        var told = await activityFeed.ListActivityAsync(20, "processor-handoff", handoff.Id, cancellationToken);
        if (told.Any(activity => activity.Category == category))
        {
            return;
        }

        await activityFeed.RecordActivityAsync(
            category,
            message,
            JsonSerializer.Serialize(new { HandoffId = handoff.Id, library.Id, library.Name, handoff.ReleaseName }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
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

    /// <summary>
    /// The statuses a processor may report, and nothing else.
    ///
    /// <para>An unrecognised word used to be read as <c>accepted</c>, so a
    /// processor reporting something Deluno did not understand was recorded as
    /// having taken the file. Guessing on a processor's behalf is how a wrong
    /// answer becomes a wrong state (#573); an unknown word is refused instead.</para>
    /// </summary>
    private static string? NormalizeProcessorStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "accepted" => "accepted",
            "started" or "processing" => "started",
            "completed" or "processed" or "ready" => "completed",
            "failed" or "error" => "failed",
            _ => null
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
    string AutomationStatus,
    string CleanupMode = "keep-source",
    bool RemoveEmptySourceFolders = false);

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

/// <summary>One item Health names inside a library's processor output folder (#921).</summary>
public sealed record ProcessorOutputItemRequest(string LibraryId, string Path);

public sealed record ExternalQueueResponse(
    IReadOnlyList<JobQueueItem> Jobs,
    IReadOnlyList<DownloadDispatchItem> Dispatches);

public sealed record ExternalTriggerRefreshRequest(
    string? MediaType,
    string? Reason);
