using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientGrabService(
    IConnectionsRepository connectionsRepository,
    IDownloadClientRegistry downloadClientRegistry,
    IIntegrationResiliencePolicy resiliencePolicy,
    IDownloadDispatchesRepository dispatchesRepository,
    IRealtimeEventPublisher realtimeEventPublisher,
    Deluno.Integrations.Search.Trackers.TrackerIndexers? trackerIndexers = null,
    ILibrariesRepository? librariesRepository = null,
    IHealthCheckRepository? healthCheckRepository = null,
    IActivityFeedRepository? activityFeedRepository = null,
    IOutboundNotificationService? notificationService = null,
    ILogger<DownloadClientGrabService>? logger = null,
    IIndexerCallLimiter? indexerCallLimiter = null,
    IIndexerAuthFailureRecorder? indexerAuthFailureRecorder = null)
    : IDownloadClientGrabService
{
    public async Task<DownloadClientGrabResult> GrabAsync(
        string clientId,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return Failed(clientId, request, "notFound", "Download client was not found.");
        }

        if (!client.IsEnabled)
        {
            return Failed(client.Id, request, "paused", "Download client is disabled.");
        }

        // A normal acquisition must never dispatch to a client that was only
        // saved rather than capability-tested.
        if (!string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                client.Id,
                request,
                "unready",
                "Download client has not passed a successful connection test. Test it before sending a release.");
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            return Failed(client.Id, request, "planned", "No downloadable URL was available for this release.");
        }

        // #750: an indexer's own grab cap, checked before any network call
        // this grab would make - a refused grab must never count as one.
        if (indexerCallLimiter is not null && !string.IsNullOrWhiteSpace(request.IndexerId))
        {
            var indexerForLimit = (await connectionsRepository.ListIndexersAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, request.IndexerId, StringComparison.OrdinalIgnoreCase));
            if (indexerForLimit is not null)
            {
                var limitResult = indexerCallLimiter.TryConsumeGrab(indexerForLimit, DateTimeOffset.UtcNow);
                if (!limitResult.Allowed)
                {
                    return Failed(
                        client.Id,
                        request,
                        "unavailable",
                        $"{indexerForLimit.Name}'s grab limit ({limitResult.Limit} per {limitResult.Unit}) is reached for now.",
                        category: "indexer");
                }
            }
        }

        // Before the client's circuit: a tracker that will not hand over a
        // release says nothing about the client.
        var resolved = await ResolveTrackerReleaseAsync(request, cancellationToken);
        if (resolved.Problem is { } problem)
        {
            return Failed(client.Id, request, "unavailable", problem, category: "indexer");
        }

        request = resolved.Request;
        request = await WithSaveFolderAsync(client, request, cancellationToken);

        await realtimeEventPublisher.PublishDispatchGrabAttemptAsync(
            request.DispatchId ?? "unknown",
            request.ReleaseName,
            client.Id,
            client.Name,
            cancellationToken);

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "grab"),
                "grab",
                MaxAttempts: 1,
                FailureThreshold: 3,
                ServiceType: "download-client",
                ServiceId: client.Id,
                ServiceName: client.Name),
            token => GrabCoreAsync(client, request, token),
            value => value.Succeeded
                ? IntegrationResilienceOutcome.Success
                : value.Status == "failed"
                    ? IntegrationResilienceOutcome.RetryableFailure
                    : IntegrationResilienceOutcome.NonRetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            await realtimeEventPublisher.PublishDispatchGrabCompletedAsync(
                request.DispatchId ?? "unknown",
                request.ReleaseName,
                client.Id,
                false,
                "Circuit breaker open",
                cancellationToken);

            return Failed(
                client.Id,
                request,
                "circuitOpen",
                "Deluno paused grabs for this client after repeated failures. Test the client connection before sending another release.",
                result.Failure);
        }

        var grabResult = result.Value ?? Failed(client.Id, request, "failed", result.FailureMessage ?? "Download client grab failed.");
        if (!grabResult.Succeeded && grabResult.Failure is null)
        {
            grabResult = grabResult with
            {
                Failure = result.Failure ?? IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    "grab",
                    grabResult.Status,
                    grabResult.Message,
                    grabResult.ResponseCode,
                    attempts: result.Attempts,
                    code: grabResult.FailureCode,
                    upstreamDetail: grabResult.ResponseJson)
            };
        }

        grabResult = WithoutARetryPromise(grabResult);

        await RecordUTorrentNoHashAsync(client, request, grabResult, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.DispatchId))
        {
            await dispatchesRepository.RecordGrabAsync(
                request.DispatchId,
                grabResult.Status,
                grabResult.ResponseCode,
                grabResult.Message,
                grabResult.Failure?.Code ?? grabResult.FailureCode,
                grabResult.ResponseJson,
                cancellationToken,
                failure: grabResult.Failure,
                externalId: grabResult.ExternalId);

            await realtimeEventPublisher.PublishDispatchGrabCompletedAsync(
                request.DispatchId,
                request.ReleaseName,
                client.Id,
                grabResult.Succeeded,
                grabResult.Message,
                cancellationToken);
        }

        if (grabResult.Succeeded)
        {
            await ConfirmSavePathAsync(client, request, cancellationToken);
        }

        return grabResult;
    }

    /// <summary>
    /// #1073: for a client with no category folders of its own, names the
    /// library's downloads folder on the grab. A failed lookup or a folder the
    /// libraries do not agree on leaves the grab exactly as it was.
    /// </summary>
    private async Task<DownloadClientGrabRequest> WithSaveFolderAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        if (librariesRepository is null
            || !downloadClientRegistry.TryGet(client.Protocol, out var implementation)
            || !implementation.SetsSaveFolderPerGrab)
        {
            return request;
        }

        try
        {
            var mappings = await connectionsRepository.ListDownloadClientPathMappingsAsync(client.Id, cancellationToken);
            var folder = await ClientSaveFolders.ResolveAsync(
                librariesRepository, client, request.MediaType, DownloadClientHelpers.ResolveCategory(client, request), mappings, cancellationToken);
            return folder?.ClientFolder is { } clientFolder ? request with { SaveFolder = clientFolder } : request;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "Could not work out {Client}'s save folder for {Release}; the client's own folder applies.", client.Name, request.ReleaseName);
            return request;
        }
    }

    /// <summary>
    /// #942: uTorrent's WebUI add-url/add-file answers only with a build
    /// number, not a refusal a dead link can be told apart by — so a grab
    /// that never surfaces a new hash is genuinely ambiguous, not a release
    /// Deluno can blocklist on a guess. The owner's decision (28 Sep 2026): say
    /// so in plain words and let the normal stall handling
    /// (<c>DownloadDispatchPollingService</c>'s grab-timeout path) settle it
    /// later, the same as it would for any other client whose download never
    /// shows.
    ///
    /// <para>A no-op for every other client, for a failed grab, for the
    /// "already held" duplicate-follow case (that already explains itself),
    /// and for the ordinary case where a hash did come back -- including the
    /// magnet-hash fallback <see cref="GrabCoreAsync"/> applies just above
    /// this call, which already answers the question for a magnet link.</para>
    /// </summary>
    private async Task RecordUTorrentNoHashAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        DownloadClientGrabResult grabResult,
        CancellationToken cancellationToken)
    {
        if (activityFeedRepository is null
            || !grabResult.Succeeded
            || grabResult.IsDuplicateFollow
            || !string.IsNullOrWhiteSpace(grabResult.ExternalId)
            || !string.Equals(client.Protocol, "utorrent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "download.grab.unconfirmed",
            $"{grabResult.ReleaseName} was sent to {client.Name}, but {client.Name} never showed a new download for it. Deluno is not blocklisting it on that guess alone -- it will keep watching and follow the usual rules if it never appears.",
            null,
            null,
            "download",
            request.DispatchId,
            cancellationToken);
    }

    /// <summary>
    /// Confirms, on this grab, that the category and save path the client
    /// actually used are the ones the library that requested it expects
    /// (#909). A category renamed in the client, or a remapped share, used to
    /// "succeed" here while the file landed where nothing was looking for it
    /// -- the next sign of trouble was an import that never happened, days
    /// later, with no link back to this grab. This is flagged immediately as
    /// a setup problem, not judged as a bad release.
    ///
    /// <para>Best-effort: this never fails or delays the grab itself, which
    /// has already succeeded by the time this runs.</para>
    /// </summary>
    private async Task ConfirmSavePathAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        // #1073: a grab Deluno gave its own folder went where it was told; the
        // client's category settings say nothing about it.
        if (librariesRepository is null || healthCheckRepository is null || string.IsNullOrWhiteSpace(request.Category)
            || !string.IsNullOrWhiteSpace(request.SaveFolder))
        {
            return;
        }

        try
        {
            if (!downloadClientRegistry.TryGet(client.Protocol, out var implementation))
            {
                return;
            }

            var libraries = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .Where(library => string.Equals(library.MediaType, request.MediaType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (libraries.Length == 0)
            {
                return;
            }

            var check = await implementation.CheckCategoryAsync(client, request.Category, cancellationToken);
            if (string.IsNullOrWhiteSpace(check.SavePath))
            {
                // The client did not say -- not every protocol reports a save
                // path per category, and that is not itself a problem.
                return;
            }

            var mappings = await connectionsRepository.ListDownloadClientPathMappingsAsync(client.Id, cancellationToken);
            var reportedPath = DownloadClientTelemetryService.TranslateRemotePath(check.SavePath, mappings);
            if (string.IsNullOrWhiteSpace(reportedPath))
            {
                return;
            }

            foreach (var library in libraries)
            {
                var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
                var routed = routing?.DownloadClients.Any(link =>
                    string.Equals(link.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        !string.IsNullOrWhiteSpace(link.Category) ? link.Category : (string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? client.TvCategory : client.MoviesCategory),
                        request.Category,
                        StringComparison.OrdinalIgnoreCase)) ?? false;
                if (!routed || string.IsNullOrWhiteSpace(library.DownloadsPath))
                {
                    continue;
                }

                var id = $"grab:{library.Id}:{client.Id}:{request.Category}";
                var name = $"{client.Name}'s save path for {library.Name}";

                var outcome = FolderChainService.SamePath(reportedPath, library.DownloadsPath)
                    ? HealthCheckOutcome.Ok(id, HealthCheckCategories.Grab, name, $"{client.Name} saved {request.ReleaseName} to {reportedPath}, the folder {library.Name} expects.")
                    : HealthCheckOutcome.Problem(
                        id, HealthCheckCategories.Grab, name,
                        $"{client.Name} saved {request.ReleaseName} to {reportedPath}, but {library.Name} expects {library.DownloadsPath}.",
                        cause: $"{client.Name}'s {request.Category} category no longer saves to the folder {library.Name} watches -- a category renamed in the client, or a remapped share, usually causes this.",
                        fix: $"Change the {request.Category} category's folder in {client.Name}, or update {library.Name}'s downloads folder to match.",
                        detailsJson: $$"""{"libraryId":"{{library.Id}}","clientId":"{{client.Id}}","category":"{{request.Category}}","reportedPath":"{{reportedPath}}","expectedPath":"{{library.DownloadsPath}}"}""");

                var transitions = await healthCheckRepository.UpsertResultsAsync([outcome], cancellationToken);
                var transition = transitions.FirstOrDefault();
                if (transition is null || transition.Transition == HealthCheckTransition.Unchanged)
                {
                    continue;
                }

                var started = transition.Transition == HealthCheckTransition.Started;
                if (activityFeedRepository is not null)
                {
                    await activityFeedRepository.RecordActivityAsync(
                        started ? "health.problem" : "health.resolved",
                        transition.Result.Message,
                        transition.Result.DetailsJson,
                        null,
                        "health-check",
                        transition.Result.Id,
                        cancellationToken);
                }

                if (started && notificationService is not null)
                {
                    await notificationService.DispatchAsync(
                        "health.problem",
                        name,
                        $"{transition.Result.Message} {transition.Result.Cause} {transition.Result.Fix}".Trim(),
                        transition.Result.DetailsJson,
                        cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogDebug(exception, "Could not confirm the save path for grab of {ReleaseName} to {ClientName}.", request.ReleaseName, client.Name);
        }
    }

    /// <summary>
    /// A grab that failed is not sent again by itself (#620), so it must not
    /// say it will be.
    ///
    /// <para>A timeout or an unreachable client is classed as retryable, and
    /// Queue and the provider drawer read that as "Retry scheduled". The retry
    /// was a <c>download.grab.retry</c> job that no handler and no lane ever
    /// picked up: it sat queued for good, and nothing was sent. Resending one
    /// release unattended is not something Deluno wants either, because it can
    /// grab a title a second time after the search cycle has already fetched it
    /// again. A paused client and a failure that needs a person keep their own
    /// states, because both are true.</para>
    /// </summary>
    public static DownloadClientGrabResult WithoutARetryPromise(DownloadClientGrabResult result)
        => result is { Succeeded: false, Failure: { RetryState: IntegrationRetryState.Retrying or IntegrationRetryState.RetryScheduled } failure }
            ? result with
            {
                Failure = failure with
                {
                    RetryState = IntegrationRetryState.NotRetryable,
                    RetryAfterUtc = failure.Kind == IntegrationFailureKind.RateLimit ? failure.RetryAfterUtc : null
                }
            }
            : result;

    private async Task<DownloadClientGrabResult> GrabCoreAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!downloadClientRegistry.TryGet(client.Protocol, out var implementation))
            {
                return Failed(
                    client.Id,
                    request,
                    "failed",
                    $"'{client.Protocol}' is not a supported download client protocol. Supported protocols: {string.Join(", ", downloadClientRegistry.KnownProtocols)}.",
                    category: "configuration");
            }

            var sent = await implementation.GrabAsync(client, request, cancellationToken);

            // A magnet names its torrent's hash, the id every torrent client
            // files it under, so the grab can be found by it whatever name the
            // client gives the download (#617).
            return sent.Succeeded && string.IsNullOrWhiteSpace(sent.ExternalId)
                   && Search.Trackers.TrackerMagnet.InfoHashOf(request.DownloadUrl) is { Length: 40 } hash
                ? sent with { ExternalId = hash.ToLowerInvariant() }
                : sent;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return Failed(
                client.Id,
                request,
                "failed",
                exception.Message,
                IntegrationFailureFactory.FromException(
                    "download-client",
                    client.Id,
                    client.Name,
                    "grab",
                    exception,
                    retryScheduled: true));
        }
    }

    /// <summary>
    /// A native tracker's link made into something a client can take (#606): a
    /// details page followed to its magnet, or a .torrent fetched with the
    /// tracker's sign-in. Every other indexer's link goes to the client as it is.
    /// </summary>
    private async Task<(DownloadClientGrabRequest Request, string? Problem)> ResolveTrackerReleaseAsync(
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        if (trackerIndexers is null
            || request.TorrentFile is not null
            || string.IsNullOrWhiteSpace(request.IndexerId) && string.IsNullOrWhiteSpace(request.IndexerName)
            || request.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return (request, null);
        }

        // By id; by name when a grab arrives without one (#609), since a
        // tracker's page sent to a client as it is can never download.
        var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
        var indexer = string.IsNullOrWhiteSpace(request.IndexerId)
            ? indexers.FirstOrDefault(item => IndexerProtocols.IsTracker(item.Protocol) && string.Equals(item.Name, request.IndexerName, StringComparison.OrdinalIgnoreCase))
            : indexers.FirstOrDefault(item => string.Equals(item.Id, request.IndexerId, StringComparison.OrdinalIgnoreCase));
        if (indexer is null || !IndexerProtocols.IsTracker(indexer.Protocol))
        {
            return (request, null);
        }

        try
        {
            var grab = await trackerIndexers.ResolveAsync(indexer, request.DownloadUrl, cancellationToken);
            var chosen = GrabLinkPreference.Choose(grab, indexer.PreferMagnetUrl);
            return chosen.MagnetUrl is { } magnet
                ? (request with { DownloadUrl = magnet }, null)
                : (request with { TorrentFile = chosen.TorrentFile }, null);
        }
        catch (Exception exception) when (exception is Deluno.Integrations.Search.Trackers.TrackerException or HttpRequestException or Deluno.Integrations.Search.Trackers.TrackerFieldException or FormatException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var message = $"{indexer.Name} would not hand over this release: {exception.Message}";

            // #754: a grab that could not fetch a tracker's release because its
            // sign-in has gone bad writes the same combined Indexer History row
            // an RSS sync's own auth failure does, through the same recorder.
            if (indexerAuthFailureRecorder is not null && IndexerAuthFailureText.LooksLikeAuthFailure(exception.Message))
            {
                await indexerAuthFailureRecorder.RecordAsync(indexer.Id, indexer.Name, "grab", message, null, cancellationToken);
            }

            return (request, message);
        }
    }

    private static DownloadClientGrabResult Failed(
        string clientId,
        DownloadClientGrabRequest request,
        string status,
        string message,
        IntegrationFailure? failure = null,
        string? category = null)
        => WithoutARetryPromise(new(clientId, request.ReleaseName, false, status, message, FailureCode: failure?.Code)
        {
            Failure = failure ?? IntegrationFailureFactory.FromLegacy(
                "download-client",
                clientId,
                clientId,
                "grab",
                category ?? status,
                message)
        });
}
