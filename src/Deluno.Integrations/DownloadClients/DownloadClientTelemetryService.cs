using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Resilience;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientTelemetryService(
    IPlatformSettingsRepository platformRepository,
    IDownloadHealthRepository healthRepository,
    ILibrariesRepository librariesRepository,
    IConnectionsRepository connectionsRepository,
    IJobQueueRepository jobQueueRepository,
    IDownloadClientRegistry downloadClientRegistry,
    TimeProvider timeProvider,
    IIntegrationResiliencePolicy resiliencePolicy,
    IJobScheduler jobScheduler,
    IDownloadDispatchesRepository dispatchesRepository,
    IActivityFeedRepository activityFeedRepository,
    IRecoveryHealthEvaluator healthEvaluator,
    IProcessorRepository processorRepository,
    IBlockedReleaseRepository? blockedReleases = null)
    : IDownloadClientTelemetryService
{
    public async Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var capturedUtc = timeProvider.GetUtcNow();
        var platformSettings = await platformRepository.GetAsync(cancellationToken);
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var pathMappings = await connectionsRepository.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var routeCategoriesByLibrary = await LoadRouteCategoriesAsync(libraries, cancellationToken);
        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(
            DownloadClientTelemetryLimits.HistoryWindow,
            null,
            cancellationToken);
        // Housekeeping archives a finished record after a week, while the
        // torrent it describes can go on seeding for far longer.
        var settledDispatches = await jobQueueRepository.ListDownloadDispatchesAsync(
            DownloadClientTelemetryLimits.HistoryWindow,
            null,
            cancellationToken,
            includeArchived: true);
        var importJobs = await jobQueueRepository.ListAsync(200, cancellationToken);
        // The hand-off is the only thing that knows a download in
        // C:\...\Downloads-Complete and a refined file in C:\...\Refined are
        // the same item; without it the queue never learns its import finished
        // (#280).
        var handoffs = await processorRepository.ListProcessorHandoffsAsync(null, 250, cancellationToken);
        var snapshots = new List<DownloadClientTelemetrySnapshot>();

        foreach (var client in clients.OrderBy(item => item.Priority).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!client.IsEnabled)
            {
                snapshots.Add(CreateSnapshot(client, [], capturedUtc, "paused", "Client is disabled."));
                continue;
            }

            var liveSnapshot = await TryGetLiveSnapshotAsync(client, capturedUtc, cancellationToken);
            if (liveSnapshot is not null)
            {
                var clientDispatches = dispatches
                    .Where(dispatch => string.Equals(dispatch.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await RecordWhatTheClientShowsAsync(liveSnapshot, clientDispatches, cancellationToken);
                snapshots.Add(DownloadClientHelpers.NormalizeSnapshotFailures(
                    EnrichQueueImportState(
                        EnrichWithDispatchHistory(
                            liveSnapshot,
                            clientDispatches,
                            capturedUtc),
                        libraries,
                        clientDispatches,
                        settledDispatches,
                        importJobs,
                        handoffs,
                        routeCategoriesByLibrary)));
                continue;
            }

            var dispatchHistory = dispatches
                .Where(dispatch => string.Equals(dispatch.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                .Select(dispatch => CreateDispatchHistoryItem(client, dispatch, capturedUtc))
                .ToArray();

            snapshots.Add(CreateSnapshot(
                client,
                [],
                capturedUtc,
                NormalizeHealth(client.HealthStatus),
                client.LastHealthMessage ?? "Live telemetry unavailable; showing Deluno dispatch history only.",
                dispatchHistory,
                client.LastHealthFailure));
        }

        var mappedSnapshots = snapshots
            .Select(snapshot => ApplyPathMappings(snapshot, pathMappings))
            .ToArray();
        var healthAnnotatedSnapshots = await Task.WhenAll(mappedSnapshots.Select(snapshot => AttachHealthFindingsAsync(snapshot, platformSettings.DownloadHealthStrikeThreshold, platformSettings.CleanupBlockReleaseAfterThreshold, cancellationToken)));

        return new DownloadTelemetryOverview(
            Summary: Summarize(healthAnnotatedSnapshots.SelectMany(snapshot => snapshot.Queue)),
            Clients: healthAnnotatedSnapshots,
            CapturedUtc: capturedUtc);
    }

    public async Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, false, "Download client was not found.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    clientId,
                    clientId,
                    $"action:{request.Action}",
                    "notFound",
                    "Download client was not found.")
            };
        }

        var action = NormalizeAction(request.Action);
        if (action is null)
        {
            return new DownloadClientActionResult(client.Id, request.QueueItemId, request.Action, false, "Unsupported action.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{request.Action}",
                    "rejected",
                    "Unsupported action.")
            };
        }

        // An item owned by another downloader can be shared or cross-seeded. Queue
        // removal is therefore an opt-in, confirmed operation. Deluno does not use
        // this setting for automatic cleanup, and adapters that support deletion are
        // still asked to retain payload files wherever their client API permits it.
        // Deleting is gated; forgetting is not, and the difference is who asked.
        //
        // This setting exists so Deluno does not tidy away queue entries the
        // owner wanted kept — an item can be shared or cross-seeded by another
        // downloader. Forgetting is never Deluno tidying up: it comes from a
        // confirmed "force a re-download", or from a refusal the owner can turn
        // off in the failure console (DESIGN-007 decisions 9 and 16). Gating it
        // here would make the force silently refuse on the strength of an
        // unrelated setting, which is the same defect as a button that does not
        // do what it says.
        if (action is DownloadClientActions.Delete or DownloadClientActions.DeleteWithData &&
            !(await platformRepository.GetAsync(cancellationToken)).RemoveCompletedDownloads)
        {
            return new DownloadClientActionResult(
                client.Id,
                request.QueueItemId,
                action,
                false,
                "Removing downloads from their client is turned off. Turn on \"Allow removing client queue entries\" under Find & Download > Download Clients to remove it from Deluno.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    "action:delete",
                    "configuration",
                    "External-client queue removal is disabled.")
            };
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "action"),
                "action",
                MaxAttempts: 1,
                FailureThreshold: 3,
                ServiceType: "download-client",
                ServiceId: client.Id,
                ServiceName: client.Name),
            token => ExecuteActionCoreAsync(client, action, request.QueueItemId, token),
            value => value.Succeeded
                ? IntegrationResilienceOutcome.Success
                : IntegrationResilienceOutcome.RetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            return new DownloadClientActionResult(
                client.Id,
                request.QueueItemId,
                action,
                false,
                "Deluno paused queue actions for this client after repeated failures. Test the client connection before trying again.")
            {
                Failure = result.Failure ?? IntegrationFailureFactory.CircuitOpen(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    result.RetryAfterUtc)
            };
        }

        var actionResult = result.Value ?? new DownloadClientActionResult(
            client.Id,
            request.QueueItemId,
            action,
            false,
            result.FailureMessage ?? "Download client action failed.")
        {
            Failure = result.Failure
        };
        return !actionResult.Succeeded && actionResult.Failure is null
            ? actionResult with
            {
                Failure = result.Failure ?? IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    "failed",
                    actionResult.Message,
                    attempts: result.Attempts)
            }
            : actionResult;
    }

    private async Task<DownloadClientTelemetrySnapshot?> TryGetLiveSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "telemetry"),
                "telemetry",
                FailureThreshold: 2,
                ServiceType: "download-client",
                ServiceId: client.Id,
                ServiceName: client.Name),
            token => GetLiveSnapshotCoreAsync(client, capturedUtc, token),
            value => value is null
                ? IntegrationResilienceOutcome.NonRetryableFailure
                : value.HealthStatus == "healthy"
                    ? IntegrationResilienceOutcome.Success
                    : IntegrationResilienceOutcome.NonRetryableFailure,
            cancellationToken);

        if (result.CircuitOpen)
        {
            return CreateSnapshot(
                client,
                [],
                capturedUtc,
                "degraded",
                "Live telemetry is temporarily paused after repeated connection failures.",
                failure: result.Failure ?? IntegrationFailureFactory.CircuitOpen(
                    "download-client",
                    client.Id,
                    client.Name,
                    "telemetry",
                    result.RetryAfterUtc));
        }

        return result.Value ??
            (result.FailureMessage is null
                ? null
                : CreateSnapshot(
                    client,
                    [],
                    capturedUtc,
                    "degraded",
                    result.FailureMessage,
                    failure: result.Failure));
    }

    private async Task<DownloadClientActionResult> ExecuteActionCoreAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        try
        {
            return !downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? new DownloadClientActionResult(client.Id, queueItemId, action, false, $"'{client.Protocol}' is not a supported download client protocol. Supported protocols: {string.Join(", ", downloadClientRegistry.KnownProtocols)}.")
                {
                    Failure = IntegrationFailureFactory.FromLegacy(
                        "download-client",
                        client.Id,
                        client.Name,
                        $"action:{action}",
                        "configuration",
                        $"'{client.Protocol}' is not a supported download client protocol.")
                }
                : await implementation.ExecuteActionAsync(client, action, queueItemId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or JsonException)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, exception.Message)
            {
                Failure = IntegrationFailureFactory.FromException(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    exception,
                    retryScheduled: true)
            };
        }
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetLiveSnapshotCoreAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? await implementation.GetSnapshotAsync(client, capturedUtc, cancellationToken)
                : null;
        }
        catch (Exception exception) when (exception is not HttpRequestException and not TaskCanceledException and not IOException)
        {
            return CreateSnapshot(
                client,
                [],
                capturedUtc,
                "degraded",
                exception.Message,
                failure: IntegrationFailureFactory.FromException(
                    "download-client",
                    client.Id,
                    client.Name,
                    "telemetry",
                    exception,
                    retryScheduled: true));
        }
    }

    private DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null,
        IntegrationFailure? failure = null)
    {
        var normalizedQueue = (queue ?? [])
            .Select(DownloadClientHelpers.NormalizeQueueFailure)
            .ToArray();
        var historyItems = (history ?? CreateHistoryFromQueue(client, normalizedQueue, capturedUtc))
            .Select(DownloadClientHelpers.NormalizeHistoryFailure)
            .ToArray();
        return new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Capabilities: downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? implementation.Capabilities
                : new DownloadClientTelemetryCapabilities(false, false, false, false, false, false, "unknown"),
            Summary: Summarize(normalizedQueue),
            Queue: normalizedQueue,
            History: historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            CapturedUtc: capturedUtc,
            HistoryTruncated: historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow)
        {
            LastFailure = failure
        };
    }

    private async Task<DownloadClientTelemetrySnapshot> AttachHealthFindingsAsync(
        DownloadClientTelemetrySnapshot snapshot,
        int strikeThreshold,
        bool blockReleaseAfterThreshold,
        CancellationToken cancellationToken)
    {
        var annotated = snapshot.Queue
            .Select(item => (Item: item, Findings: healthEvaluator.Evaluate(
                new RecoveryQueueSnapshot(
                    item.Status,
                    item.ErrorMessage,
                    item.SourcePath,
                    item.SpeedMbps,
                    item.AddedUtc,
                    item.EtaSeconds,
                    item.ReleaseName),
                snapshot.CapturedUtc)))
            .ToArray();
        var observations = annotated
            .SelectMany(entry => entry.Findings.Select(finding => new DownloadHealthObservation(
                entry.Item.ClientId, entry.Item.Id, entry.Item.ReleaseName, finding.Kind, finding.Severity, finding.Evidence)))
            .ToArray();
        var records = await healthRepository.RecordDownloadHealthObservationsAsync(
            snapshot.ClientId,
            snapshot.Queue.Select(item => item.Id).ToArray(),
            observations,
            cancellationToken);
        var recordsByFinding = records.ToDictionary(
            record => $"{record.ClientId}\u001f{record.QueueItemId}\u001f{record.Kind}",
            StringComparer.OrdinalIgnoreCase);

        return snapshot with
        {
            Queue = annotated.Select(entry => entry.Item with
            {
                HealthFindings = entry.Findings.Select(finding =>
                {
                    recordsByFinding.TryGetValue($"{entry.Item.ClientId}\u001f{entry.Item.Id}\u001f{finding.Kind}", out var record);
                    return new DownloadHealthFinding(
                        finding.Severity,
                        finding.Kind,
                        finding.Summary,
                        finding.Evidence,
                        finding.RecommendedAction,
                        finding.CanSafelyRetry,
                        finding.CanSafelyRemove,
                        StrikeCount: record?.StrikeCount ?? 0,
                        CandidateBlocked: blockReleaseAfterThreshold && (record?.BlocksCandidate(snapshot.CapturedUtc, strikeThreshold) ?? false),
                        IgnoredUntilUtc: record?.IgnoredUntilUtc);
                }).ToArray()
            }).ToArray()
        };
    }

    public async Task<DownloadCleanupPreview?> PreviewCleanupAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var overview = await GetOverviewAsync(cancellationToken);
        var item = overview.Clients
            .Where(client => string.Equals(client.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(client => client.Queue)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, queueItemId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return null;
        var settings = await platformRepository.GetAsync(cancellationToken);
        return DownloadCleanupPreviewBuilder.Create(item, settings);
    }

    public async Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(
        DownloadTelemetryOverview overview,
        CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        var refuseRelease = settings.CleanupBlockReleaseAfterThreshold && blockedReleases is not null;
        if (!refuseRelease &&
            !settings.CleanupQueueReplacementAfterThreshold &&
            !settings.CleanupRemoveClientEntryAfterThreshold &&
            !settings.CleanupPurgePayloadAfterThreshold)
        {
            return new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []);
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var routeCategoriesByLibrary = await LoadRouteCategoriesAsync(libraries, cancellationToken);
        var notes = new List<string>();
        var evaluated = 0;
        var refused = 0;
        var replacements = 0;
        var removed = 0;
        var purged = 0;
        var skipped = 0;

        foreach (var snapshot in overview.Clients)
        {
            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, snapshot.ClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null) continue;

            foreach (var item in snapshot.Queue)
            {
                // Only a download that has stopped is struck (#560): a failed
                // import or processing run has its own rules about the release.
                var struck = (item.HealthFindings ?? []).FirstOrDefault(finding =>
                    DownloadHealthStrikes.Counts(finding.Kind) &&
                    finding.StrikeCount >= settings.DownloadHealthStrikeThreshold &&
                    (finding.IgnoredUntilUtc is null || finding.IgnoredUntilUtc <= overview.CapturedUtc));
                if (struck is null)
                {
                    continue;
                }

                evaluated++;
                var dispatch = await dispatchesRepository.FindDispatchByHashAsync(client.Id, item.Id, cancellationToken);
                if (dispatch is null || string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var library = libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
                var routeCategory = routeCategoriesByLibrary.TryGetValue(dispatch.LibraryId, out var categories) &&
                    categories.TryGetValue(client.Id, out var configuredRouteCategory)
                    ? configuredRouteCategory
                    : null;
                var categoryOwned = IsDelunoCategory(client, item.Category, dispatch.MediaType, routeCategory);
                var payloadOwned = library is not null && IsPathWithinApprovedDownloadRoot(item.SourcePath, library.DownloadsPath);
                var applied = new List<string>();

                // "Do not select the same failed release again": on the one
                // blocklist, which searches skip, the title page counts, and the
                // blocklist screen lists and lifts. It used to be a separate list
                // only the grab checked, so the search kept choosing the release
                // and nothing on screen said why (#560). Refused before the
                // replacement is queued, so that search already skips it.
                if (refuseRelease)
                {
                    await blockedReleases!.BlockAsync(
                        new BlockedRelease(
                            Guid.NewGuid().ToString("n"),
                            BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                            dispatch.ReleaseName,
                            dispatch.IndexerName,
                            dispatch.MediaType,
                            dispatch.EntityId,
                            item.Title,
                            ImportFailurePolicy.DownloadStalled,
                            StalledReason(struck, client.Name),
                            dispatch.TorrentHashOrItemId ?? item.Id,
                            client.Id,
                            client.Name,
                            DateTimeOffset.MinValue),
                        cancellationToken);
                    refused++;
                    applied.Add("release refused");
                }

                if (settings.CleanupQueueReplacementAfterThreshold &&
                    SupportsReplacementSearch(dispatch.EntityType))
                {
                    await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
                    replacements++;
                    applied.Add("replacement search queued");
                }
                else if (settings.CleanupQueueReplacementAfterThreshold)
                {
                    notes.Add($"{item.ReleaseName}: replacement was not queued because this dispatch is not a movie, series or episode library search.");
                }

                if (settings.CleanupRemoveClientEntryAfterThreshold)
                {
                    if (categoryOwned)
                    {
                        // This bypasses the interactive external-client toggle only after
                        // the durable dispatch, Deluno category, and selected health policy
                        // have all matched. The public queue-action endpoint retains its
                        // stricter manual guard.
                        var result = await ExecuteOwnedRemediationRemovalAsync(client, item.Id, cancellationToken);
                        if (result.Succeeded)
                        {
                            removed++;
                            applied.Add("client entry removed (payload retained by client)");
                        }
                        else
                        {
                            notes.Add($"{item.ReleaseName}: client entry was not removed: {result.Message}");
                        }
                    }
                    else
                    {
                        notes.Add($"{item.ReleaseName}: client entry was retained because its Deluno category could not be proven.");
                    }
                }

                if (settings.CleanupPurgePayloadAfterThreshold)
                {
                    if (payloadOwned && TryPurgePayload(item.SourcePath!))
                    {
                        purged++;
                        applied.Add("approved residual payload purged");
                    }
                    else
                    {
                        notes.Add($"{item.ReleaseName}: payload was retained because it is absent, inaccessible, or outside the configured download root.");
                    }
                }

                if (applied.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var details = JsonSerializer.Serialize(new
                {
                    clientId = client.Id,
                    queueItemId = item.Id,
                    releaseName = item.ReleaseName,
                    actions = applied,
                    categoryOwned,
                    payloadOwned
                });
                await dispatchesRepository.RecordTimelineEventAsync(dispatch.Id, "health-remediation-applied", details, cancellationToken);
                await dispatchesRepository.RecordImportOutcomeAsync(
                    dispatch.Id,
                    "failed",
                    null,
                    "health-remediation-applied",
                    string.Join("; ", applied),
                    cancellationToken,
                    failure: IntegrationFailureFactory.FromLegacy(
                        "deluno",
                        dispatch.LibraryId,
                        "Deluno health remediation",
                        "remediate",
                        "rejected",
                        string.Join("; ", applied),
                        code: "health-remediation-applied",
                        externalId: dispatch.Id));
                await activityFeedRepository.RecordActivityAsync(
                    "download.health.remediated",
                    $"Applied failed-download handling to {item.ReleaseName}: {string.Join(", ", applied)}.",
                    details,
                    null,
                    "download_dispatch",
                    dispatch.Id,
                    cancellationToken);
            }
        }

        return new DownloadHealthRemediationReport(evaluated, replacements, removed, purged, skipped, notes, refused);
    }

    /// <summary>
    /// Why the release was refused, for the blocklist row. The strikes say how
    /// long it went on, because "stalled" on its own reads like a moment.
    /// </summary>
    internal static string StalledReason(DownloadHealthFinding finding, string clientName)
    {
        // "Nothing arriving" is only a finding once it has gone on for half an
        // hour, so that half hour came before the first strike.
        var nothingArriving = string.Equals(finding.Kind, DownloadHealthStrikes.NoThroughput, StringComparison.OrdinalIgnoreCase);
        var lasted = DownloadHealthStrikes.Window * (Math.Max(0, finding.StrikeCount - 1) + (nothingArriving ? 1 : 0));
        var forHowLong = lasted.TotalMinutes switch
        {
            <= 0 => string.Empty,
            < 60 => $" for {lasted.TotalMinutes:0} minutes",
            60 => " for an hour",
            _ => $" for {lasted.TotalHours:0.#} hours"
        };
        var what = nothingArriving
            ? $"Nothing arrived in {clientName}{forHowLong}"
            : $"{clientName} showed it stalled or in error{forHowLong}";
        return $"{what}, so Deluno stopped waiting for it and will not pick it again.";
    }

    private async Task QueueReplacementSearchAsync(
        DownloadDispatchItem dispatch,
        LibraryItem? library,
        CancellationToken cancellationToken)
    {
        var libraryName = library?.Name ?? dispatch.LibraryId;
        await jobScheduler.EnqueueAsync(new EnqueueJobRequest(
            LibrarySearchJobTypes.For(dispatch.MediaType),
            "download-health",
            JsonSerializer.Serialize(new
            {
                libraryId = dispatch.LibraryId,
                libraryName,
                mediaType = dispatch.MediaType,
                checkMissing = true,
                checkUpgrades = true,
                maxItems = 1,
                retryDelayHours = 24,
                triggeredBy = "health-replacement",
                targetEntityId = dispatch.EntityId
            }),
            "download_dispatch",
            dispatch.Id,
            IdempotencyKey: $"download-health-replacement:{dispatch.Id}",
            DedupeKey: $"download-health-replacement:{dispatch.Id}"), cancellationToken);
    }

    public async Task<DownloadClientActionResult> ReclaimCompletedAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var client = clients.FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, queueItemId, "delete-with-data", false, "Download client was not found.");
        }

        return await ExecuteActionCoreAsync(client, "delete-with-data", queueItemId, cancellationToken);
    }

    private Task<DownloadClientActionResult> ExecuteOwnedRemediationRemovalAsync(
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken)
        => ExecuteActionCoreAsync(client, "delete", queueItemId, cancellationToken);

    private static bool IsDelunoCategory(DownloadClientItem client, string category, string mediaType, string? routeCategory = null)
    {
        if (!string.IsNullOrWhiteSpace(routeCategory) && string.Equals(routeCategory, category, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expected = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase)
            ? client.TvCategory
            : client.MoviesCategory;
        return !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, category, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithinApprovedDownloadRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryPurgePayload(string sourcePath)
    {
        try
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            else if (Directory.Exists(sourcePath)) Directory.Delete(sourcePath, recursive: true);
            else return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Tell each download record what its client is showing (#554).
    ///
    /// <para>Every pass read each client's queue and history, and nothing wrote
    /// down that a download was there: only a client's completion webhook did.
    /// Without one, a download was "never shown by the client" two hours after
    /// its grab whatever the client said. On the rig, eight downloads SABnzbd had
    /// completed, and Deluno had itself handed to Weir, were failed that way and
    /// grabbed again.</para>
    /// </summary>
    internal async Task RecordWhatTheClientShowsAsync(
        DownloadClientTelemetrySnapshot live,
        IReadOnlyList<DownloadDispatchItem> clientDispatches,
        CancellationToken cancellationToken)
    {
        foreach (var sighting in ShownByClient(live, clientDispatches))
        {
            try
            {
                await dispatchesRepository.RecordDetectionAsync(
                    sighting.Dispatch.Id,
                    sighting.Dispatch.TorrentHashOrItemId,
                    sighting.DownloadedBytes,
                    sighting.Finished,
                    cancellationToken);

                if (sighting.Failed)
                {
                    await RecordClientReportedFailureAsync(sighting.Dispatch, sighting.FailureReason, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The screen this pass feeds matters more than one record: the
                // next pass tries again, and the grab timeout is still there.
            }
        }
    }

    /// <summary>
    /// The client's own history said this release failed — NZBGet's FAILURE,
    /// DELETED or WARNING statuses, SABnzbd's Failed — read from a poll rather
    /// than a push (#647).
    ///
    /// <para>Before this, a native history failure was read exactly like a
    /// native history success: it marked the dispatch detected and completed,
    /// and nothing else. The dispatch then wore "sent" until a generic
    /// detection timeout fired hours later, with no failure reason, no
    /// blocklist entry and no replacement search — even though the client had
    /// already said, in its own words, that the download was not coming.</para>
    ///
    /// <para>This is the poll-driven twin of
    /// <see cref="DownloadClientWebhookService.HandleFailureAsync"/>: same
    /// dispatch outcome, same failure code
    /// (<see cref="DownloadClientFailureCodes.ClientReported"/>), so a
    /// dispatch either path already handled is recognised by the other rather
    /// than recorded, blocked and searched for twice.</para>
    /// </summary>
    private async Task RecordClientReportedFailureAsync(
        DownloadDispatchItem dispatch,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (dispatch.ImportStatus is not null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{dispatch.DownloadClientName} reported this download as failed."
            : reason;

        await dispatchesRepository.RecordImportOutcomeAsync(
            dispatch.Id,
            importStatus: "failed",
            importedFilePath: null,
            importFailureCode: DownloadClientFailureCodes.ClientReported,
            importFailureMessage: message,
            cancellationToken,
            failure: IntegrationFailureFactory.FromLegacy(
                "download-client",
                dispatch.DownloadClientId,
                dispatch.DownloadClientName,
                "download",
                "failed",
                message,
                code: DownloadClientFailureCodes.ClientReported,
                externalId: dispatch.Id));

        await dispatchesRepository.RecordTimelineEventAsync(
            dispatch.Id,
            "client-reported-failed",
            JsonSerializer.Serialize(new { reason = message, source = "history" }),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.failed",
            $"{dispatch.DownloadClientName} could not download {dispatch.ReleaseName}: {message}",
            null,
            null,
            "download_dispatch",
            dispatch.Id,
            cancellationToken);

        if (blockedReleases is not null)
        {
            // Blocked the same way a stalled torrent is: on the spot, not
            // behind a settings toggle. A queue item stalling might still
            // recover; a client that has already filed the download under
            // FAILURE, DELETED or WARNING never will.
            await blockedReleases.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                    dispatch.ReleaseName,
                    dispatch.IndexerName,
                    dispatch.MediaType,
                    dispatch.EntityId,
                    dispatch.ReleaseName,
                    ImportFailurePolicy.ClientReportedFailure,
                    message,
                    dispatch.TorrentHashOrItemId,
                    dispatch.DownloadClientId,
                    dispatch.DownloadClientName,
                    DateTimeOffset.MinValue),
                cancellationToken);
        }

        if (SupportsReplacementSearch(dispatch.EntityType))
        {
            var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
        }
    }

    /// <summary>
    /// Whether <see cref="QueueReplacementSearchAsync"/> can act on this
    /// dispatch's entity type.
    ///
    /// <para>A manual grab dispatches with EntityType <c>"movie"</c> or
    /// <c>"series"</c>; an automatic per-episode search
    /// (<c>EpisodeSearchJobHandler</c>) dispatches with <c>"episode"</c>.
    /// Checking only the first two silently dropped the replacement search for
    /// every automatically-searched TV episode — the shape #647 was filed
    /// against.</para>
    /// </summary>
    private static bool SupportsReplacementSearch(string entityType)
        => entityType is "movie" or "series" or "episode";

    internal sealed record ClientSighting(
        DownloadDispatchItem Dispatch,
        long? DownloadedBytes,
        bool Finished,
        bool Failed = false,
        string? FailureReason = null);

    /// <summary>
    /// The downloads this client shows that their records do not know about yet:
    /// never seen, or seen and now finished.
    ///
    /// <para>Matched on the id the client gave when it took the download
    /// (SABnzbd's nzo_id, the torrent's infohash), never on the release name, which
    /// an earlier attempt at the same release shares.</para>
    /// </summary>
    internal static IReadOnlyList<ClientSighting> ShownByClient(
        DownloadClientTelemetrySnapshot live,
        IEnumerable<DownloadDispatchItem> clientDispatches)
    {
        var shown = new Dictionary<string, (long? Bytes, bool Finished, bool Failed, string? Reason)>(StringComparer.OrdinalIgnoreCase);

        void Add(string? id, long? bytes, bool finished, bool failed, string? reason)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var key = id.Trim();
            shown[key] = shown.TryGetValue(key, out var seen)
                ? (bytes ?? seen.Bytes, finished || seen.Finished, failed || seen.Failed, reason ?? seen.Reason)
                : (bytes, finished, failed, reason);
        }

        foreach (var item in live.Queue)
        {
            Add(item.Id, item.DownloadedBytes, item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed, false, null);
        }

        foreach (var item in live.History)
        {
            // A failure counts as finished — and as a failure Deluno should act
            // on — only in the client's own history. One derived from the
            // queue is a torrent with an error message, which can still be
            // downloading (#647: NZBGet's FAILURE/DELETED/WARNING statuses,
            // SABnzbd's Failed, land here).
            var failedByClient = item.Outcome == "failed" && item.HistorySource == "native";
            var finished = item.Outcome is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady || failedByClient;
            var bytes = item.SizeBytes > 0 ? item.SizeBytes : (long?)null;
            var reason = failedByClient ? item.ErrorMessage : null;
            Add(item.Id, bytes, finished, failedByClient, reason);
            Add(item.ExternalId, bytes, finished, failedByClient, reason);
        }

        var sightings = new List<ClientSighting>();
        foreach (var dispatch in clientDispatches)
        {
            if (string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId)
                || !shown.TryGetValue(dispatch.TorrentHashOrItemId.Trim(), out var seen))
            {
                continue;
            }

            if (dispatch.DetectedUtc is null || (seen.Finished && dispatch.ClientCompletedUtc is null))
            {
                sightings.Add(new ClientSighting(dispatch, seen.Bytes, seen.Finished, seen.Failed, seen.Reason));
            }
        }

        return sightings;
    }

    internal static DownloadClientTelemetrySnapshot EnrichWithDispatchHistory(
        DownloadClientTelemetrySnapshot snapshot,
        IEnumerable<DownloadDispatchItem> dispatches,
        DateTimeOffset capturedUtc)
    {
        // Native usenet history and Deluno dispatch history use different row
        // identifiers. Match on both identities so a completed grab is shown
        // once while still retaining dispatch-only rows when the client has
        // forgotten them. This is especially important after restart: duplicate
        // rows make the grab -> client -> import trace look like two downloads.
        var liveIdentifiers = snapshot.History
            .SelectMany(item => new[] { item.Id, item.ExternalId })
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dispatchHistory = dispatches
            .Where(dispatch => !liveIdentifiers.Contains(dispatch.Id) &&
                               !liveIdentifiers.Contains(dispatch.TorrentHashOrItemId ?? string.Empty))
            .Select(dispatch => CreateDispatchHistoryItem(snapshot, dispatch, capturedUtc))
            .ToArray();

        if (dispatchHistory.Length == 0)
        {
            return snapshot;
        }

        var historyItems = snapshot.History
            .Concat(dispatchHistory)
            .OrderByDescending(item => item.CompletedUtc)
            .ToArray();

        return snapshot with
        {
            History = historyItems.Take(DownloadClientTelemetryLimits.HistoryWindow).ToArray(),
            HistoryTruncated = snapshot.HistoryTruncated || historyItems.Length > DownloadClientTelemetryLimits.HistoryWindow
        };
    }

    internal static DownloadClientTelemetrySnapshot EnrichQueueImportState(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyList<DownloadDispatchItem> settledDispatches,
        IReadOnlyList<JobQueueItem> importJobs,
        IReadOnlyList<ProcessorHandoffItem> handoffs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> routeCategoriesByLibrary)
    {
        if (snapshot.Queue.Count == 0)
        {
            return snapshot;
        }

        var jobsBySource = importJobs
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => new { Job = job, SourcePath = TryReadImportSourcePath(job.PayloadJson) })
            .Where(item => !string.IsNullOrWhiteSpace(item.SourcePath))
            .GroupBy(item => NormalizeSourceKey(item.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Job.CreatedUtc).First().Job, StringComparer.OrdinalIgnoreCase);
        var jobsById = importJobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);

        var clientHandoffs = handoffs
            .Where(handoff => string.Equals(handoff.ClientId, snapshot.ClientId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(handoff => handoff.UpdatedUtc)
            .ToArray();
        var handoffsByQueueItem = clientHandoffs
            .GroupBy(handoff => handoff.QueueItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var handoffsBySource = clientHandoffs
            .Where(handoff => !string.IsNullOrWhiteSpace(handoff.SourcePath))
            .GroupBy(handoff => NormalizeSourceKey(handoff.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var queue = snapshot.Queue.Select(item =>
        {
            if (item.Status is not (DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed))
            {
                return item;
            }

            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                jobsBySource.TryGetValue(NormalizeSourceKey(item.SourcePath), out var job))
            {
                var status = ShouldApplyImportJobState(item, job, dispatches)
                    ? job.Status switch
                    {
                        "queued" or "running" => DownloadQueueStatuses.ImportQueued,
                        "completed" => DownloadQueueStatuses.Imported,
                        "failed" => DownloadQueueStatuses.ImportFailed,
                        _ => item.Status
                    }
                    : item.Status;
                return item with { Status = status };
            }

            var library = ResolveLibraryForQueueItem(item, libraries, dispatches, routeCategoriesByLibrary);
            var resolvedItem = library is null
                ? item
                : item with { LibraryId = library.Id, MediaType = library.MediaType };

            // A refined item is imported from the processor output path while it
            // still sits in the client queue under its download path, so matching
            // the two by path can never work. The hand-off record is what joins
            // them, and reading its lifecycle here is what finally lets an item
            // leave the Processing stage: without it a completed import still
            // reported processingCount 1 for ever (#280).
            var handoff = handoffsByQueueItem.GetValueOrDefault(item.Id)
                ?? (string.IsNullOrWhiteSpace(item.SourcePath) ? null : handoffsBySource.GetValueOrDefault(NormalizeSourceKey(item.SourcePath)));

            // A hand-off that predates the attempt in front of us describes a
            // previous one.
            //
            // Both keys it is found by — the infohash and the download path —
            // are the same every time the same release is fetched, so a
            // re-download inherited the last outcome. On the lab rig a brand new
            // download of Big Buck Bunny matched yesterday's completed hand-off
            // and was reported `imported` while the library folder was empty and
            // the film read Missing. That status is not in the set the import
            // planner accepts, so no hand-off was created, no import was
            // attempted, and nothing failed: the download simply stopped
            // existing as far as Deluno was concerned.
            if (handoff is not null && IsFromAnEarlierAttempt(handoff, item, dispatches))
            {
                handoff = null;
            }

            if (handoff is not null)
            {
                var queueStatus = ProcessorHandoffQueueStatus.Resolve(handoff, jobsById, resolvedItem.Status);
                return resolvedItem with
                {
                    Status = queueStatus,
                    // The hand-off is the only record holding the processor's own
                    // reason (#616). The client itself never saw a failure - it
                    // downloaded the file fine - so resolvedItem.ErrorMessage is
                    // empty here, and without this the row and its health finding
                    // fall back to a generic "the processor did not produce an
                    // import-ready output", leaving nothing to actually read.
                    ErrorMessage = queueStatus == DownloadQueueStatuses.ProcessingFailed
                        ? resolvedItem.ErrorMessage ?? handoff.FailureMessage
                        : resolvedItem.ErrorMessage
                };
            }

            // The library's workflow says what happens to a new download, not
            // what happened to this one. On the rig Sintel was imported, its
            // library was switched to refine-before-import days later, and the
            // torrent was still seeding: with its import job aged out of the
            // window above, no hand-off (none was ever needed) and its record
            // archived, it read "Waiting for processor" for ever and held the
            // Processing count at one.
            if (WasAlreadyImported(item, settledDispatches))
            {
                return resolvedItem with { Status = DownloadQueueStatuses.Imported };
            }

            if (library is not null && ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
            {
                return resolvedItem with { Status = DownloadQueueStatuses.WaitingForProcessor };
            }

            return resolvedItem;
        }).ToArray();

        return snapshot with
        {
            Queue = queue,
            Summary = Summarize(queue)
        };
    }
    private static LibraryItem? ResolveLibraryForQueueItem(
        DownloadQueueItem item,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> routeCategoriesByLibrary)
    {
        var dispatch = dispatches
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault(dispatch =>
                string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dispatch.ReleaseName, item.Title, StringComparison.OrdinalIgnoreCase));
        if (dispatch is not null)
        {
            var dispatchedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (dispatchedLibrary is not null)
            {
                return dispatchedLibrary;
            }
        }

        var category = item.Category?.Trim();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryLibraries = libraries
                .Where(library => routeCategoriesByLibrary.TryGetValue(library.Id, out var categories) &&
                    categories.TryGetValue(item.ClientId, out var routeCategory) &&
                    string.Equals(routeCategory, category, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (categoryLibraries.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    var categorySource = NormalizeSourceKey(item.SourcePath);
                    var categoryPathMatch = categoryLibraries.FirstOrDefault(library =>
                        !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                        categorySource.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
                    if (categoryPathMatch is not null)
                    {
                        return categoryPathMatch;
                    }
                }

                return categoryLibraries[0];
            }
        }

        var normalizedMediaType = item.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
        var mediaLibraries = libraries
            .Where(library => string.Equals(library.MediaType, normalizedMediaType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(item.SourcePath))
        {
            var source = NormalizeSourceKey(item.SourcePath);
            var pathMatch = mediaLibraries.FirstOrDefault(library =>
                !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                source.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return mediaLibraries.FirstOrDefault();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadRouteCategoriesAsync(
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var routeCategoriesByLibrary = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            routeCategoriesByLibrary[library.Id] = (routing?.DownloadClients ?? [])
                .Where(link => !string.IsNullOrWhiteSpace(link.Category))
                .ToDictionary(link => link.DownloadClientId, link => link.Category!.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        return routeCategoriesByLibrary;
    }

    internal static string? TryReadImportSourcePath(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!TryGetProperty(root, "preview", out var preview) ||
                !TryGetProperty(preview, "sourcePath", out var sourcePath) ||
                sourcePath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sourcePath.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    /// <summary>
    /// A download client reports paths from its own filesystem namespace. Before
    /// Deluno queues a processor handoff or an import, translate that namespace
    /// to the one visible to this host. The longest matching remote root wins so
    /// a specific mapping may safely sit beside a broader one.
    /// </summary>
    internal static string? TranslateRemotePath(
        string? reportedPath,
        IEnumerable<DownloadClientPathMappingItem> mappings)
    {
        if (string.IsNullOrWhiteSpace(reportedPath)) return reportedPath;

        var normalizedReported = NormalizeSourceKey(reportedPath);
        var match = mappings
            .Where(item => item.IsEnabled)
            .Select(item => new { Mapping = item, Remote = NormalizeSourceKey(item.RemotePath) })
            .Where(item => normalizedReported.Equals(item.Remote, StringComparison.OrdinalIgnoreCase) ||
                           normalizedReported.StartsWith(item.Remote + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Remote.Length)
            .ThenBy(item => item.Mapping.Priority)
            .FirstOrDefault();

        if (match is null) return reportedPath;

        var relative = normalizedReported[match.Remote.Length..].TrimStart('/');
        if (relative.Length == 0) return match.Mapping.LocalPath;

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Aggregate(match.Mapping.LocalPath, Path.Combine);
    }

    private static DownloadClientTelemetrySnapshot ApplyPathMappings(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<DownloadClientPathMappingItem> mappings)
    {
        var clientMappings = mappings
            .Where(item => string.Equals(item.DownloadClientId, snapshot.ClientId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (clientMappings.Length == 0) return snapshot;

        return snapshot with
        {
            Queue = snapshot.Queue
                .Select(item => item with { SourcePath = TranslateRemotePath(item.SourcePath, clientMappings) })
                .ToArray(),
            History = snapshot.History
                .Select(item => item with { SourcePath = TranslateRemotePath(item.SourcePath, clientMappings) })
                .ToArray()
        };
    }

    private static IReadOnlyList<DownloadClientHistoryItem> CreateHistoryFromQueue(
        DownloadClientItem client,
        IEnumerable<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc)
    {
        return queue
            .Where(item => item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady || !string.IsNullOrWhiteSpace(item.ErrorMessage))
            .OrderByDescending(item => item.AddedUtc)
            .Select(item => new DownloadClientHistoryItem(
                Id: item.Id,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: item.MediaType,
                Title: item.Title,
                ReleaseName: item.ReleaseName,
                Category: item.Category,
                Outcome: !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? "failed"
                    : item.Status == DownloadQueueStatuses.ImportReady
                        ? DownloadQueueStatuses.ImportReady
                        : DownloadQueueStatuses.Completed,
                IndexerName: item.IndexerName,
                SizeBytes: item.SizeBytes,
                CompletedUtc: item.Status is DownloadQueueStatuses.Completed or DownloadQueueStatuses.ImportReady ? capturedUtc : item.AddedUtc,
                ErrorMessage: item.ErrorMessage,
                SourcePath: item.SourcePath,
                HistorySource: "queue-derived",
                ExternalId: item.Id,
                Failure: item.Failure))
            .ToArray();
    }

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientTelemetrySnapshot snapshot,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            snapshot.ClientId,
            snapshot.ClientName,
            snapshot.Protocol,
            dispatch,
            capturedUtc);

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientItem client,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            client.Id,
            client.Name,
            client.Protocol,
            dispatch,
            capturedUtc);

    internal static DownloadClientHistoryItem CreateDispatchHistoryItem(
        string clientId,
        string clientName,
        string protocol,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
    {
        var dispatchFailure = dispatch.Failure;
        var failureMessage = dispatchFailure?.Message ?? DescribeDispatchFailure(dispatch);
        var failureOperation = IsFailureStatus(dispatch.ImportStatus)
            ? "import"
            : IsFailureStatus(dispatch.GrabStatus)
                ? "grab"
                : "dispatch";
        var failureCode = IsFailureStatus(dispatch.ImportStatus)
            ? dispatch.ImportFailureCode
            : dispatch.GrabFailureCode ?? dispatch.ImportFailureCode;
        return new DownloadClientHistoryItem(
            Id: dispatch.Id,
            ClientId: clientId,
            ClientName: clientName,
            Protocol: protocol,
            MediaType: dispatch.MediaType,
            Title: DownloadClientHelpers.CleanReleaseTitle(dispatch.ReleaseName),
            ReleaseName: dispatch.ReleaseName,
            Category: dispatch.MediaType,
            Outcome: NormalizeHistoryOutcome(dispatch),
            IndexerName: dispatch.IndexerName,
            SizeBytes: dispatch.DownloadedBytes ?? 0,
            CompletedUtc: dispatch.ImportCompletedUtc
                ?? dispatch.ImportDetectedUtc
                ?? dispatch.DetectedUtc
                ?? dispatch.GrabAttemptedUtc
                ?? (dispatch.CreatedUtc == default ? capturedUtc : dispatch.CreatedUtc),
            // Only a real failure message belongs here. NotesJson is the
            // dispatch's diagnostic payload (the search plan), and passing it
            // as an error message painted successful "sent" rows red and
            // leaked raw JSON into the activity list (#257).
            ErrorMessage: failureMessage,
            HistorySource: "dispatch-derived",
            ExternalId: dispatch.TorrentHashOrItemId ?? dispatch.Id,
            Failure: dispatchFailure ?? (failureMessage is { } message
                ? IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    clientId,
                    clientName,
                    failureOperation,
                    "rejected",
                    message,
                    code: failureCode ?? dispatch.Status,
                    externalId: dispatch.TorrentHashOrItemId ?? dispatch.Id)
                : null));
    }

    /// <summary>
    /// Whether this hand-off belongs to an earlier fetch of the same release.
    ///
    /// <para>The dispatch is what says "this is a new attempt", so a hand-off
    /// created before the current dispatch cannot be about it. Where there is no
    /// dispatch to compare against, the hand-off is kept — losing the Processing
    /// stage for an item Deluno cannot place is worse than showing a stale
    /// one.</para>
    /// </summary>
    internal static bool IsFromAnEarlierAttempt(
        ProcessorHandoffItem handoff,
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latestDispatch is not null && handoff.CreatedUtc < latestDispatch.CreatedUtc;
    }

    /// <summary>
    /// Whether the latest fetch of this release from this client was imported.
    ///
    /// <para>The latest, because fetching a release again is a new attempt: a
    /// newer record that has not been imported outranks an older one that was.
    /// Archived records count, because archiving is housekeeping and says
    /// nothing about the outcome.</para>
    /// </summary>
    internal static bool WasAlreadyImported(
        DownloadQueueItem item,
        IReadOnlyList<DownloadDispatchItem> dispatchesIncludingArchived)
    {
        var latestDispatch = dispatchesIncludingArchived
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();

        return latestDispatch is not null
            && string.Equals(latestDispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldApplyImportJobState(
        DownloadQueueItem item,
        JobQueueItem job,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var latestDispatch = dispatches
            .Where(dispatch =>
                string.Equals(dispatch.DownloadClientId, item.ClientId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault();
        if (latestDispatch is null
            || string.Equals(latestDispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            TryReadImportDispatchId(job.PayloadJson),
            latestDispatch.Id,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadImportDispatchId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return TryGetProperty(document.RootElement, "dispatchId", out var dispatchId)
                && dispatchId.ValueKind == JsonValueKind.String
                ? dispatchId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The human-readable reason a dispatch failed, or null when it did not.
    /// </summary>
    private static string? DescribeDispatchFailure(DownloadDispatchItem dispatch)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ImportFailureMessage))
        {
            return dispatch.ImportFailureMessage;
        }

        if (IsFailureStatus(dispatch.ImportStatus) || IsFailureStatus(dispatch.GrabStatus) || IsFailureStatus(dispatch.Status))
        {
            if (IsFailureStatus(dispatch.ImportStatus))
            {
                return dispatch.ImportFailureMessage
                    ?? dispatch.ImportFailureCode
                    ?? "The download client reported an import failure.";
            }

            return dispatch.GrabMessage
                ?? dispatch.GrabFailureCode
                ?? dispatch.ImportFailureCode
                ?? "The download client reported a failure.";
        }

        return null;
    }

    private static bool IsFailureStatus(string? status)
        => status is not null &&
           (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("error", StringComparison.OrdinalIgnoreCase));

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
        => DownloadQueueSummary.Of(queue);

    private static string InferMediaType(DownloadClientItem client, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "movies";
        }

        if (!string.IsNullOrWhiteSpace(client.TvCategory) &&
            string.Equals(category, client.TvCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        if (!string.IsNullOrWhiteSpace(client.MoviesCategory) &&
            string.Equals(category, client.MoviesCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "movies";
        }

        var normalized = category.Trim().ToLowerInvariant();
        if (normalized.Contains("sonarr") ||
            normalized.Contains("series") ||
            normalized.Contains("show") ||
            normalized.Contains("tv"))
        {
            return "tv";
        }

        return "movies";
    }

    /// <summary>
    /// The verbs this gateway will pass to an adapter.
    ///
    /// <para>Every adapter implements <c>forget</c> and <c>delete-with-data</c>
    /// — Deluge, NZBGet, qBittorrent and SABnzbd all map them — but this
    /// function did not list them, so both were refused here and no adapter was
    /// ever reached. Measured on the lab rig on 2026-09-05: forcing a
    /// re-download reported <i>"qBittorrent would not forget the release:
    /// Unsupported action."</i></para>
    ///
    /// <para>That took out every path that asks a client to forget a release —
    /// the acquisition override, the refused-download clean-up pass, and
    /// "Clean up now" on the blocklist. None of their tests could see it,
    /// because they all stand in for this service.</para>
    /// </summary>
    private static string? NormalizeAction(string action)
        => action.Trim().ToLowerInvariant() switch
        {
            "pause" => DownloadClientActions.Pause,
            "resume" => DownloadClientActions.Resume,
            "remove" or "delete" => DownloadClientActions.Delete,
            "delete-with-data" => DownloadClientActions.DeleteWithData,
            "forget" => DownloadClientActions.Forget,
            "recheck" or "force-recheck" => DownloadClientActions.Recheck,
            _ => null
        };

    private static string NormalizeHealth(string value)
        => value.Equals("healthy", StringComparison.OrdinalIgnoreCase) || value.Equals("ready", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : value.Equals("paused", StringComparison.OrdinalIgnoreCase) || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? "paused"
                : value.Equals("attention", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("degraded", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("untested", StringComparison.OrdinalIgnoreCase)
                    ? "degraded"
                    : value.Equals("unreachable", StringComparison.OrdinalIgnoreCase)
                        ? "down"
                        : "unknown";

    private static string NormalizeHistoryOutcome(DownloadDispatchItem dispatch)
    {
        if (dispatch.Failure is not null ||
            IsFailureStatus(dispatch.ImportStatus) ||
            IsFailureStatus(dispatch.GrabStatus) ||
            IsFailureStatus(dispatch.Status))
        {
            return "failed";
        }

        // Import is the latest durable lifecycle state. A dispatch can still
        // have Status=sent or GrabStatus=succeeded after the import callback
        // has completed, so history must not regress to the earlier stage.
        var importOutcome = NormalizeHistoryOutcomeValue(dispatch.ImportStatus);
        if (importOutcome is not "unknown")
        {
            return importOutcome == DownloadQueueStatuses.Completed
                ? DownloadQueueStatuses.Imported
                : importOutcome;
        }

        var grabOutcome = NormalizeHistoryOutcomeValue(dispatch.GrabStatus);
        if (grabOutcome is not "unknown" and not "sent")
        {
            return grabOutcome;
        }

        return NormalizeHistoryOutcomeValue(dispatch.Status);
    }

    private static string NormalizeHistoryOutcomeValue(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is "completed" or "succeeded" or "success") return DownloadQueueStatuses.Completed;
        if (normalized is "imported") return DownloadQueueStatuses.Imported;
        if (normalized.Contains("fail") || normalized.Contains("error")) return "failed";
        if (normalized.Contains("import")) return DownloadQueueStatuses.ImportReady;
        return normalized.Length == 0 ? "unknown" : normalized;
    }


}
