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

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientTelemetryService(
    IPlatformSettingsRepository platformRepository,
    ILibrariesRepository librariesRepository,
    IConnectionsRepository connectionsRepository,
    IJobQueueRepository jobQueueRepository,
    IDownloadClientRegistry downloadClientRegistry,
    TimeProvider timeProvider,
    IIntegrationResiliencePolicy resiliencePolicy,
    IJobScheduler jobScheduler,
    IDownloadDispatchesRepository dispatchesRepository,
    IActivityFeedRepository activityFeedRepository)
    : IDownloadClientTelemetryService
{
    public async Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var capturedUtc = timeProvider.GetUtcNow();
        var platformSettings = await platformRepository.GetAsync(cancellationToken);
        var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
        var pathMappings = await connectionsRepository.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(100, null, cancellationToken);
        var importJobs = await jobQueueRepository.ListAsync(200, cancellationToken);
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
                snapshots.Add(EnrichQueueImportState(
                    EnrichWithDispatchHistory(
                        liveSnapshot,
                        clientDispatches,
                        capturedUtc),
                    libraries,
                    clientDispatches,
                    importJobs));
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
                dispatchHistory));
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
            return new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, false, "Download client was not found.");
        }

        var action = NormalizeAction(request.Action);
        if (action is null)
        {
            return new DownloadClientActionResult(client.Id, request.QueueItemId, request.Action, false, "Unsupported action.");
        }

        // An item owned by another downloader can be shared or cross-seeded. Queue
        // removal is therefore an opt-in, confirmed operation. Deluno does not use
        // this setting for automatic cleanup, and adapters that support deletion are
        // still asked to retain payload files wherever their client API permits it.
        if (action == "delete" &&
            !(await platformRepository.GetAsync(cancellationToken)).RemoveCompletedDownloads)
        {
            return new DownloadClientActionResult(
                client.Id,
                request.QueueItemId,
                action,
                false,
                "External-client queue removal is disabled. Enable it in Library setup > Connections > Download clients before removing an item from Deluno.");
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "action"),
                "download-client.action",
                MaxAttempts: 1,
                FailureThreshold: 3),
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
                "Deluno paused queue actions for this client after repeated failures. Test the client connection before trying again.");
        }

        return result.Value ?? new DownloadClientActionResult(client.Id, request.QueueItemId, action, false, result.FailureMessage ?? "Download client action failed.");
    }

    private async Task<DownloadClientTelemetrySnapshot?> TryGetLiveSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                DownloadClientHelpers.BuildResilienceKey(client, "telemetry"),
                "download-client.telemetry",
                FailureThreshold: 2),
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
                "Live telemetry is temporarily paused after repeated connection failures.");
        }

        return result.Value ??
            (result.FailureMessage is null
                ? null
                : CreateSnapshot(client, [], capturedUtc, "degraded", result.FailureMessage));
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
                : await implementation.ExecuteActionAsync(client, action, queueItemId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, exception.Message);
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
            return CreateSnapshot(client, [], capturedUtc, "degraded", exception.Message);
        }
    }

    private DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
        => new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Capabilities: downloadClientRegistry.TryGet(client.Protocol, out var implementation)
                ? implementation.Capabilities
                : new DownloadClientTelemetryCapabilities(false, false, false, false, false, false, "unknown"),
            Summary: Summarize(queue),
            Queue: queue,
            History: history ?? CreateHistoryFromQueue(client, queue, capturedUtc),
            CapturedUtc: capturedUtc);

    private async Task<DownloadClientTelemetrySnapshot> AttachHealthFindingsAsync(
        DownloadClientTelemetrySnapshot snapshot,
        int strikeThreshold,
        bool blockReleaseAfterThreshold,
        CancellationToken cancellationToken)
    {
        var annotated = snapshot.Queue
            .Select(item => (Item: item, Findings: DownloadHealthEvaluator.Evaluate(item, snapshot.CapturedUtc)))
            .ToArray();
        var observations = annotated
            .SelectMany(entry => entry.Findings.Select(finding => new DownloadHealthObservation(
                entry.Item.ClientId, entry.Item.Id, entry.Item.ReleaseName, finding.Kind, finding.Severity, finding.Evidence)))
            .ToArray();
        var records = await platformRepository.RecordDownloadHealthObservationsAsync(observations, cancellationToken);
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
                    return finding with
                    {
                        StrikeCount = record?.StrikeCount ?? 0,
                        CandidateBlocked = blockReleaseAfterThreshold && (record?.BlocksCandidate(snapshot.CapturedUtc, strikeThreshold) ?? false),
                        IgnoredUntilUtc = record?.IgnoredUntilUtc
                    };
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
        if (!settings.CleanupQueueReplacementAfterThreshold &&
            !settings.CleanupRemoveClientEntryAfterThreshold &&
            !settings.CleanupPurgePayloadAfterThreshold)
        {
            return new DownloadHealthRemediationReport(0, 0, 0, 0, 0, []);
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var notes = new List<string>();
        var evaluated = 0;
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
                if (!(item.HealthFindings ?? []).Any(finding =>
                        finding.StrikeCount >= settings.DownloadHealthStrikeThreshold &&
                        (finding.IgnoredUntilUtc is null || finding.IgnoredUntilUtc <= overview.CapturedUtc)))
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
                var categoryOwned = IsDelunoCategory(client, item.Category, dispatch.MediaType);
                var payloadOwned = library is not null && IsPathWithinApprovedDownloadRoot(item.SourcePath, library.DownloadsPath);
                var applied = new List<string>();

                if (settings.CleanupQueueReplacementAfterThreshold &&
                    dispatch.EntityType is "movie" or "series")
                {
                    await QueueReplacementSearchAsync(dispatch, library, cancellationToken);
                    replacements++;
                    applied.Add("replacement search queued");
                }
                else if (settings.CleanupQueueReplacementAfterThreshold)
                {
                    notes.Add($"{item.ReleaseName}: replacement was not queued because this dispatch is not a movie or series library search.");
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
                    cancellationToken);
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

        return new DownloadHealthRemediationReport(evaluated, replacements, removed, purged, skipped, notes);
    }

    private async Task QueueReplacementSearchAsync(
        DownloadDispatchItem dispatch,
        LibraryItem? library,
        CancellationToken cancellationToken)
    {
        var libraryName = library?.Name ?? dispatch.LibraryId;
        await jobScheduler.EnqueueAsync(new EnqueueJobRequest(
            "library.search",
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

    private Task<DownloadClientActionResult> ExecuteOwnedRemediationRemovalAsync(
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken)
        => ExecuteActionCoreAsync(client, "delete", queueItemId, cancellationToken);

    private static bool IsDelunoCategory(DownloadClientItem client, string category, string mediaType)
    {
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

    private static DownloadClientTelemetrySnapshot EnrichWithDispatchHistory(
        DownloadClientTelemetrySnapshot snapshot,
        IEnumerable<DownloadDispatchItem> dispatches,
        DateTimeOffset capturedUtc)
    {
        var liveIds = new HashSet<string>(snapshot.History.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var dispatchHistory = dispatches
            .Where(dispatch => !liveIds.Contains(dispatch.Id))
            .Select(dispatch => CreateDispatchHistoryItem(snapshot, dispatch, capturedUtc))
            .ToArray();

        if (dispatchHistory.Length == 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            History = snapshot.History
                .Concat(dispatchHistory)
                .OrderByDescending(item => item.CompletedUtc)
                .Take(50)
                .ToArray()
        };
    }

    private static DownloadClientTelemetrySnapshot EnrichQueueImportState(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyList<JobQueueItem> importJobs)
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

        var queue = snapshot.Queue.Select(item =>
        {
            if (item.Status is not (DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed))
            {
                return item;
            }

            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                jobsBySource.TryGetValue(NormalizeSourceKey(item.SourcePath), out var job))
            {
                var status = job.Status switch
                {
                    "queued" or "running" => DownloadQueueStatuses.ImportQueued,
                    "completed" => DownloadQueueStatuses.Imported,
                    "failed" => DownloadQueueStatuses.ImportFailed,
                    _ => item.Status
                };
                return item with { Status = status };
            }

            var library = ResolveLibraryForQueueItem(item, libraries, dispatches);
            if (library is not null &&
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                return item with { Status = DownloadQueueStatuses.WaitingForProcessor };
            }

            return item;
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
        IReadOnlyList<DownloadDispatchItem> dispatches)
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

    private static string? TryReadImportSourcePath(string? payloadJson)
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
            .Take(30)
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
                SourcePath: item.SourcePath))
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

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        string clientId,
        string clientName,
        string protocol,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
    {
        return new DownloadClientHistoryItem(
            Id: dispatch.Id,
            ClientId: clientId,
            ClientName: clientName,
            Protocol: protocol,
            MediaType: dispatch.MediaType,
            Title: DownloadClientHelpers.CleanReleaseTitle(dispatch.ReleaseName),
            ReleaseName: dispatch.ReleaseName,
            Category: dispatch.MediaType,
            Outcome: NormalizeHistoryOutcome(dispatch.Status),
            IndexerName: dispatch.IndexerName,
            SizeBytes: 0,
            CompletedUtc: dispatch.CreatedUtc == default ? capturedUtc : dispatch.CreatedUtc,
            ErrorMessage: dispatch.NotesJson);
    }

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
    {
        var items = queue.ToArray();
        return new DownloadTelemetrySummary(
            ActiveCount: items.Count(item => item.Status == DownloadQueueStatuses.Downloading),
            QueuedCount: items.Count(item => item.Status == DownloadQueueStatuses.Queued),
            CompletedCount: items.Count(item => item.Status == DownloadQueueStatuses.Completed),
            StalledCount: items.Count(item => item.Status == DownloadQueueStatuses.Stalled),
            ProcessingCount: items.Count(item => item.Status is DownloadQueueStatuses.Processing or DownloadQueueStatuses.Processed or DownloadQueueStatuses.ProcessingFailed or DownloadQueueStatuses.WaitingForProcessor or DownloadQueueStatuses.ImportQueued),
            ImportReadyCount: items.Count(item => item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed),
            TotalSpeedMbps: Math.Round(items.Sum(item => item.SpeedMbps), 1));
    }

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

    private static string? NormalizeAction(string action)
        => action.Trim().ToLowerInvariant() switch
        {
            "pause" => "pause",
            "resume" => "resume",
            "remove" or "delete" => "delete",
            "recheck" or "force-recheck" => "recheck",
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

    private static string NormalizeHistoryOutcome(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "completed" or "succeeded" or "success") return DownloadQueueStatuses.Completed;
        if (normalized.Contains("fail") || normalized.Contains("error")) return "failed";
        if (normalized.Contains("import")) return DownloadQueueStatuses.ImportReady;
        return normalized.Length == 0 ? "unknown" : normalized;
    }


}
