using Deluno.Connections.Usenet;
using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Connections.Data;
using Deluno.Notifications;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Integrations.DownloadClients;

public static class DownloadClientEndpointRouteBuilderExtensions
{
    /// <summary>The strike records for downloads Deluno sent; anything written for someone else's download is left out (#1062).</summary>
    public static async Task<IReadOnlyList<DownloadHealthRecord>> OwnHealthRecordsAsync(
        IReadOnlyList<DownloadHealthRecord> records,
        IDownloadDispatchesRepository dispatches,
        CancellationToken cancellationToken)
    {
        var own = new List<DownloadHealthRecord>(records.Count);
        foreach (var byClient in records.GroupBy(record => record.ClientId, StringComparer.OrdinalIgnoreCase))
        {
            var known = await dispatches.FindDispatchesByClientItemIdsAsync(
                byClient.Key, byClient.Select(record => record.QueueItemId).ToArray(), cancellationToken);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dispatch in known)
            {
                if (!string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
                {
                    ids.Add(dispatch.TorrentHashOrItemId.Trim());
                }

                ids.Add(dispatch.Id);
            }

            own.AddRange(byClient.Where(record => ids.Contains(record.QueueItemId?.Trim() ?? string.Empty)));
        }

        return own;
    }

    /// <summary>How many strike records a person can see: only downloads Deluno sent (#1062).</summary>
    internal static async Task<int> CountOwnHealthRecordsAsync(
        IDownloadHealthRepository health,
        IDownloadDispatchesRepository dispatches,
        CancellationToken cancellationToken)
    {
        var count = 0;
        string? cursor = null;
        do
        {
            var page = await OwnHealthRecordsPageAsync(health, dispatches, 100, cursor, cancellationToken);
            count += page.Items.Count;
            cursor = page.NextPageToken;
        }
        while (cursor is not null);

        return count;
    }

    /// <summary>
    /// One page of strike records for downloads Deluno sent. The ownership
    /// filter runs before the page is cut, not after: it reads more pages until
    /// it has a full page of Deluno's own (or none are left), so a page is
    /// never short or empty just because outside records sat in front of it.
    /// </summary>
    internal static async Task<Page<DownloadHealthRecord>> OwnHealthRecordsPageAsync(
        IDownloadHealthRepository health,
        IDownloadDispatchesRepository dispatches,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken)
    {
        var own = new List<DownloadHealthRecord>();
        var token = pageToken;
        string? next = null;
        for (var pass = 0; pass < 50; pass++)
        {
            var page = await health.ListDownloadHealthRecordsPageAsync(new PageRequest(pageSize, token), cancellationToken);
            own.AddRange(await OwnHealthRecordsAsync(page.Items, dispatches, cancellationToken));
            next = page.NextPageToken;
            if (next is null || own.Count >= pageSize)
            {
                break;
            }

            token = next;
        }

        return Page<DownloadHealthRecord>.Of(own, next);
    }

    public static IEndpointRouteBuilder MapDelunoDownloadClientIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/download-health", async (
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            IDownloadDispatchesRepository dispatches,
            int? pageSize,
            string? pageToken,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // #1062: Deluno only ever shows downloads it sent. A strike written
            // for someone else's download before that was the rule is not listed,
            // and is filtered out before the page is cut.
            return Results.Ok(await OwnHealthRecordsPageAsync(
                platformRepository, dispatches, pageSize ?? 30, pageToken, cancellationToken));
        });

        // Its own Settings screen (#905): the rule set Deluno holds for what
        // counts as a stuck download and what it does once it does. Reads
        // synthesize the legacy flat toggles into equivalent rules the first
        // time an installation asks, so nothing changes until a person edits
        // one here (#936).
        endpoints.MapGet("/api/download-health/rules", async (
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            IPlatformSettingsRepository settingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var legacy = await settingsRepository.GetAsync(cancellationToken);
            return Results.Ok(await platformRepository.GetDownloadHealthRulesAsync(legacy, cancellationToken));
        });

        endpoints.MapPut("/api/download-health/rules/{id}", async (
            string id,
            [FromBody] SaveDownloadHealthRuleRequest request,
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            IPlatformSettingsRepository settingsRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            if (!DownloadHealthRuleKinds.IsKnown(request.Kind ?? string.Empty))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["kind"] = ["That isn't a rule kind Deluno knows about."]
                });
            }

            var now = timeProvider.GetUtcNow();
            var candidate = new DownloadHealthRule(
                Id: string.IsNullOrWhiteSpace(id) || id == "new" ? Guid.NewGuid().ToString("n") : id,
                Kind: request.Kind!,
                Enabled: request.Enabled,
                StrikeLimit: request.StrikeLimit,
                TimerMinutes: request.TimerMinutes,
                AppliesTo: request.AppliesTo ?? DownloadHealthAppliesTo.Both,
                MinCompletionPercent: request.MinCompletionPercent,
                MaxCompletionPercent: request.MaxCompletionPercent,
                ResetStrikesOnProgress: request.ResetStrikesOnProgress,
                ActionPreset: request.ActionPreset ?? DownloadHealthActionPresets.JustTellMe,
                CreatedUtc: now,
                UpdatedUtc: now,
                Threshold: request.Threshold,
                PrivacyScope: request.PrivacyScope ?? DownloadHealthPrivacyScopes.Both,
                TooSlowMaxTotalMinutes: request.TooSlowMaxTotalMinutes,
                IgnoreAboveSizeBytes: request.IgnoreAboveSizeBytes,
                SkipWhileAlternateSpeedLimitOn: request.SkipWhileAlternateSpeedLimitOn,
                ResetStrikesMinimumProgressBytes: request.ResetStrikesMinimumProgressBytes);

            var legacy = await settingsRepository.GetAsync(cancellationToken);
            var (rules, error) = await platformRepository.SaveDownloadHealthRuleAsync(candidate, legacy, cancellationToken);
            return error is null ? Results.Ok(rules) : Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = [error] });
        });

        endpoints.MapDelete("/api/download-health/rules/{id}", async (
            string id,
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await platformRepository.DeleteDownloadHealthRuleAsync(id, cancellationToken));
        });

        // Ignored downloads (#817): exempt by hash, client category, or
        // tracker/indexer domain - never struck by any rule while listed.
        endpoints.MapGet("/api/download-health/ignore-list", async (
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await platformRepository.ListDownloadHealthIgnoreEntriesAsync(cancellationToken));
        });

        endpoints.MapPost("/api/download-health/ignore-list", async (
            [FromBody] AddDownloadHealthIgnoreEntryRequest request,
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(request.Hash) && string.IsNullOrWhiteSpace(request.Category) && string.IsNullOrWhiteSpace(request.TrackerDomain) && string.IsNullOrWhiteSpace(request.Tag))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["entry"] = ["Give at least a hash, a client category, a tag, or a tracker domain to ignore."]
                });
            }

            var entry = new DownloadHealthIgnoreEntry(
                Id: Guid.NewGuid().ToString("n"),
                Hash: request.Hash?.Trim(),
                Category: request.Category?.Trim(),
                TrackerDomain: request.TrackerDomain?.Trim(),
                Note: request.Note?.Trim(),
                CreatedUtc: timeProvider.GetUtcNow(),
                Tag: request.Tag?.Trim());
            return Results.Ok(await platformRepository.AddDownloadHealthIgnoreEntryAsync(entry, cancellationToken));
        });

        endpoints.MapDelete("/api/download-health/ignore-list/{id}", async (
            string id,
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await platformRepository.RemoveDownloadHealthIgnoreEntryAsync(id, cancellationToken));
        });

        // #826: clears every recorded strike for every download, in one call.
        // Irreversible, so it stays a deliberate action rather than a toggle.
        endpoints.MapPost("/api/download-health/purge-strikes", async (
            HttpContext httpContext,
            IDownloadHealthRepository platformRepository,
            IDownloadDispatchesRepository dispatches,
            IActivityFeedRepository activityFeedRepository,
            IOutboundNotificationService outboundNotificationService,
            INotificationService notificationPreferences,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            // Only the records a person can see are counted (#1062): strikes kept for
            // someone else's downloads are cleared too, but were never on any screen.
            var visible = await CountOwnHealthRecordsAsync(platformRepository, dispatches, cancellationToken);
            await platformRepository.PurgeAllDownloadHealthStrikesAsync(cancellationToken);
            var purged = visible;
            // #826: irreversible, so it is said in plain words the same way
            // every other destructive action Deluno takes on its own already
            // is - here, one this installation's one person asked for directly.
            var purgeMessage = purged > 0
                ? $"All recorded download-health strikes were cleared: {purged} download{(purged == 1 ? "" : "s")} reset to zero."
                : "All recorded download-health strikes were cleared. There was nothing to clear.";
            await activityFeedRepository.RecordActivityAsync(
                "download.health.strikes-purged",
                purgeMessage,
                null,
                null,
                null,
                null,
                cancellationToken);
            // #966: gated by its own preference, off by default (#936).
            var strikesPurgedPreferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (strikesPurgedPreferences.StrikesPurgedEnabled)
            {
                await outboundNotificationService.DispatchAsync(
                    "download.health.strikes-purged",
                    "Download-health strikes purged",
                    purgeMessage,
                    null,
                    cancellationToken);
            }
            return Results.Ok(new PurgeDownloadHealthStrikesResult(purged));
        });

        // #816: the per-file content blocklist. Off by default (#936); read
        // and saved from Settings -> Download health, alongside the
        // dangerous-file rule toggle on the main platform-settings endpoint.
        endpoints.MapGet("/api/download-health/content-blocklist", async (
            HttpContext httpContext,
            IDownloadHealthRepository healthRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await healthRepository.GetContentBlocklistSettingsAsync(cancellationToken));
        });

        endpoints.MapPut("/api/download-health/content-blocklist", async (
            HttpContext httpContext,
            [FromBody] ContentBlocklistSettings request,
            IDownloadHealthRepository healthRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            if (!ContentBlocklistModes.IsKnown(request.Mode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Mode)] = ["Choose blacklist or whitelist."]
                });
            }

            return Results.Ok(await healthRepository.SaveContentBlocklistSettingsAsync(request, cancellationToken));
        });

        // #822: the qBittorrent "Excluded file names" sync's own last-attempt
        // state per client, for the Connections screen's client row/drawer.
        endpoints.MapGet("/api/download-health/content-blocklist/qbittorrent-sync-status", async (
            HttpContext httpContext,
            IDownloadHealthRepository healthRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await healthRepository.ListQbittorrentBlocklistSyncStatesAsync(cancellationToken));
        });

        // Orphaned download folders (#588): what sits in a library's completed
        // folder that nothing claims. Counted in Needs attention; each is kept
        // or, when Deluno can prove it downloaded it, removed - by a person.
        endpoints.MapGet("/api/download-clients/orphans", async (
            HttpContext httpContext,
            IOrphanedDownloadService orphans,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await orphans.ListCachedAsync(cancellationToken));
        });

        endpoints.MapPost("/api/download-clients/orphans/keep", async (
            HttpContext httpContext,
            [FromBody] OrphanedDownloadRequest request,
            IOrphanedDownloadService orphans,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await orphans.KeepAsync(request.Path, cancellationToken);
            return result.Done ? Results.Ok(result) : Results.Conflict(result);
        });

        endpoints.MapPost("/api/download-clients/orphans/remove", async (
            HttpContext httpContext,
            [FromBody] OrphanedDownloadRequest request,
            IOrphanedDownloadService orphans,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await orphans.RemoveAsync(request.Path, cancellationToken);
            return result.Done ? Results.Ok(result) : Results.Conflict(result);
        });

        // #932: the "Run now" button for the "Clean up leftover downloads"
        // system task - the same service the schedule runs, so the answer
        // cannot depend on which one started it.
        endpoints.MapPost("/api/download-clients/leftovers/cleanup", async (
            HttpContext httpContext,
            ILeftoverDownloadCleanupService leftoverCleanup,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            return denied ?? Results.Ok(await leftoverCleanup.RunAsync(cancellationToken));
        });

        // Served from the last capture the background refresher took (#1081): a
        // screen never waits on a download client, whatever any client is doing.
        // The reading's age is in CapturedUtc and, for convenience, a header.
        endpoints.MapGet("/api/download-clients/telemetry", async (
            HttpContext httpContext,
            IDownloadClientTelemetryService telemetryService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var overview = await telemetryService.GetLatestOverviewAsync(DownloadTelemetryFreshness.Screen, cancellationToken);
            var age = timeProvider.GetUtcNow() - overview.CapturedUtc;
            httpContext.Response.Headers["X-Deluno-Snapshot-Age-Ms"] =
                Math.Max(0, (long)age.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(overview);
        });

        // A failed row in a client's history is read from the client, so Deluno cannot delete it there. Dismissing it
        // (#1218) is remembered here, and Activity stops counting it as failed. Nothing is changed at the client.
        endpoints.MapPost("/api/download-clients/{clientId}/history/{itemId}/dismiss", async (
            string clientId,
            string itemId,
            HttpContext httpContext,
            Deluno.Jobs.Data.AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            await dismissals.DismissAsync(Deluno.Jobs.Data.AttentionDismissalStore.ClientHistoryKey(clientId, itemId), cancellationToken);
            return Results.Ok(new { clientId, itemId, dismissed = true });
        });

        endpoints.MapGet("/api/download-clients/history/dismissed", async (
            HttpContext httpContext,
            Deluno.Jobs.Data.AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var keys = (await dismissals.ListAsync(cancellationToken))
                .Where(key => key.StartsWith(Deluno.Jobs.Data.AttentionDismissalStore.ClientHistoryPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Results.Ok(keys);
        });

        // The stored counterpart to the live telemetry above: what the speed
        // has been, rather than what it is this second. Kept separate because
        // the two answer different questions and have very different costs.
        endpoints.MapGet("/api/download-clients/throughput", async (
            int? hours,
            IDownloadThroughputRepository throughputRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Bounded by the sampler's own retention: asking for a week cannot
            // conjure history that was never kept.
            var window = Math.Clamp(hours ?? 6, 1, 48);
            var since = timeProvider.GetUtcNow().AddHours(-window);
            var samples = await throughputRepository.ListSamplesAsync(since, cancellationToken);
            return Results.Ok(new DownloadThroughputWindow(window, samples));
        });

        // #1142: the Usenet provider is entered once, here, and written to SABnzbd and NZBGet.
        // The password is passed on and never kept or echoed.
        endpoints.MapGet("/api/download-clients/usenet-provider", async (
            UsenetProviderService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new { clients = await service.StatusAsync(cancellationToken) }))
            .RequireAuthorization(DelunoAuthorizationPolicies.Queue);

        endpoints.MapPost("/api/download-clients/usenet-provider", async (
            [FromBody] UsenetProviderRequest request,
            UsenetProviderService service,
            CancellationToken cancellationToken) =>
        {
            if (UsenetProviderText.Validate(request) is { } problem)
            {
                return Results.BadRequest(new { message = problem });
            }

            var results = await service.SaveAsync(request, cancellationToken);
            return results.Count == 0
                ? Results.BadRequest(new { message = "There is no SABnzbd or NZBGet connection to give the provider to." })
                : Results.Ok(new { saved = results.All(result => result.Saved), clients = results });
        }).RequireAuthorization(DelunoAuthorizationPolicies.Write);

        endpoints.MapPost("/api/download-clients/{clientId}/queue/actions", async (
            string clientId,
            HttpContext httpContext,
            [FromBody] DownloadClientActionRequest request,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetryService.ExecuteActionAsync(clientId, request, cancellationToken);
            return result.Refused ? Results.Conflict(result)
                : result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        // #813: dismissing a SABnzbd queue-level warning in Deluno also
        // clears it at SABnzbd, so it does not reappear next poll.
        endpoints.MapPost("/api/download-clients/{clientId}/warnings/clear", async (
            string clientId,
            HttpContext httpContext,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var cleared = await telemetryService.ClearClientWarningsAsync(clientId, cancellationToken);
            return cleared ? Results.Ok() : Results.BadRequest(new { message = "Could not clear this client's warnings." });
        });

        // Resumes a client that is paused as a whole (SABnzbd, NZBGet). Not the
        // per-item resume in queue/actions.
        endpoints.MapPost("/api/download-clients/{clientId}/resume", async (
            string clientId,
            HttpContext httpContext,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetryService.ResumeClientAsync(clientId, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        // The Queue's "why" behind removing a download (cleanup sheet J1/J2,
        // #924): one of QueueRemovalReasons, in plain words on the dialog
        // that asks it. Replaces the old single confirm and the "Allow
        // removing client queue entries" setting that gated it — this
        // endpoint is the confirmation, and every reason acts.
        endpoints.MapPost("/api/download-clients/{clientId}/queue/{queueItemId}/remove", async (
            string clientId,
            string queueItemId,
            HttpContext httpContext,
            [FromBody] QueueRemovalRequest request,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetryService.RemoveWithReasonAsync(clientId, queueItemId, request.Reason, cancellationToken);
            return result.Refused ? Results.Conflict(result)
                : result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        endpoints.MapGet("/api/download-clients/{clientId}/queue/{queueItemId}/cleanup-preview", async (
            string clientId,
            string queueItemId,
            HttpContext httpContext,
            IDownloadClientTelemetryService telemetryService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var preview = await telemetryService.PreviewCleanupAsync(clientId, queueItemId, cancellationToken);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });

        endpoints.MapPost("/api/download-clients/{clientId}/queue/{queueItemId}/health/{kind}/ignore", async (
            string clientId,
            string queueItemId,
            string kind,
            HttpContext httpContext,
            [FromBody] IgnoreDownloadHealthFindingRequest request,
            IDownloadHealthRepository platformRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var record = await platformRepository.IgnoreDownloadHealthFindingAsync(clientId, queueItemId, kind, request.DurationDays, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });

        // Each routed client's real category folder beside the library's
        // folders, link by link (#616).
        endpoints.MapGet("/api/libraries/{libraryId}/folder-chain", async (
            string libraryId,
            HttpContext httpContext,
            IFolderChainService folderChain,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var report = await folderChain.CheckAsync(libraryId, cancellationToken);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        // Where each library's downloads really land, for a processor that has
        // to watch the right folder (#1073). Generic: any processor may read it.
        endpoints.MapGet("/api/integrations/processors/download-destinations", async (
            HttpContext httpContext,
            string? libraryId,
            IFolderChainService folderChain,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            var scopeDenied = UserAuthorization.RequireApiScope(httpContext, "imports");
            if (scopeDenied is not null) return scopeDenied;

            var libraries = await folderChain.ListDestinationsAsync(libraryId, cancellationToken);
            return libraries is null ? Results.NotFound() : Results.Ok(new { libraries });
        });

        endpoints.MapPost("/api/download-clients/{clientId}/categories/check", async (
            string clientId,
            HttpContext httpContext,
            DownloadClientCategoryCheckRequest request,
            IConnectionsRepository connectionsRepository,
            IDownloadClientRegistry downloadClientRegistry,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var category = request.Category?.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["category"] = ["Enter the category name used by the download client."]
                });
            }

            var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                return Results.NotFound();
            }

            if (!downloadClientRegistry.TryGet(client.Protocol, out var downloadClient))
            {
                return Results.Ok(new DownloadClientCategoryCheckResult(
                    client.Id,
                    client.Name,
                    category,
                    DownloadClientCategoryStatuses.Unsupported,
                    $"Deluno does not have a category checker for {client.Protocol} yet.",
                    Supported: false,
                    Found: false));
            }

            return Results.Ok(await downloadClient.CheckCategoryAsync(client, category, cancellationToken));
        });

        endpoints.MapPost("/api/download-clients/{clientId}/webhook", async (
            string clientId,
            DownloadClientWebhookRequest request,
            IDownloadClientWebhookService webhookService,
            CancellationToken cancellationToken) =>
        {
            var result = await webhookService.HandleAsync(clientId, request, cancellationToken);
            return result.Accepted ? Results.Ok(result) : Results.NotFound(result);
        });

        return endpoints;
    }
}

public sealed record IgnoreDownloadHealthFindingRequest(int DurationDays = 7);

public sealed record SaveDownloadHealthRuleRequest(
    string? Kind,
    bool Enabled,
    int StrikeLimit,
    int TimerMinutes,
    string? AppliesTo,
    int MinCompletionPercent,
    int MaxCompletionPercent,
    bool ResetStrikesOnProgress,
    string? ActionPreset,
    double Threshold,
    string? PrivacyScope = null,
    int? TooSlowMaxTotalMinutes = null,
    long? IgnoreAboveSizeBytes = null,
    bool SkipWhileAlternateSpeedLimitOn = false,
    long? ResetStrikesMinimumProgressBytes = null);

public sealed record AddDownloadHealthIgnoreEntryRequest(
    string? Hash,
    string? Category,
    string? TrackerDomain,
    string? Note,
    string? Tag = null);

public sealed record PurgeDownloadHealthStrikesResult(int PurgedCount);
