using System.Globalization;
using System.Text.Json;
using Deluno.Companions;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Worker.Dashboard;

namespace Deluno.Worker.Attention;

/// <summary>
/// One row of the bell's "needs a look" list (#1185): what it is, the words, where its button goes, and how many
/// problems it stands for. <see cref="Data"/> is the record behind it (the stuck download, the import case, the job)
/// so the screen can draw the real buttons for it; the screen never works out a count or a membership of its own.
/// </summary>
/// <param name="Id">Unique among the rows; the same word the screen used as the row's key.</param>
/// <param name="Section"><c>media</c> (your downloads and imports) or <c>deluno</c> (Deluno itself).</param>
/// <param name="Kind">What kind of problem; rows of one kind are what a pile of old ones groups.</param>
/// <param name="Tone"><c>bad</c> or <c>warn</c>.</param>
/// <param name="Action">The words on the row's main button.</param>
/// <param name="Count">How many problems the row stands for (more than one only for "3 background workers keep failing").</param>
public sealed record AttentionRow(
    string Id,
    string Section,
    string Kind,
    string Tone,
    string Title,
    string Text,
    string To,
    string Action,
    int Count,
    DateTimeOffset? SinceUtc,
    object? Data);

/// <summary>
/// The bell's whole answer, built in one place (#1185). <see cref="Count"/> is the number the bell badge, the bell's
/// own heading and the tray's "N things need a look" all show: the sum of <see cref="Rows"/>. <see cref="Notices"/>
/// (a newer Deluno, a newer connected app) are listed in their own group and are never part of the count: an update
/// ready is the blue dot, not "needs a look" (docs/tray-standard.md).
/// </summary>
public sealed record AttentionSummary(
    int Count,
    IReadOnlyList<AttentionRow> Rows,
    IReadOnlyList<AttentionRow> Notices,
    int Leftovers,
    int Suggestions)
{
    public static AttentionSummary None { get; } = new(0, [], [], 0, 0);
}

/// <summary>An import case in the shape the bell's buttons read it (it also says which kind of title it is for).</summary>
public sealed record AttentionRecoveryData(
    string Id,
    string Title,
    string FailureKind,
    string Status,
    string Summary,
    string RecommendedAction,
    string? DetailsJson,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? ResolvedUtc,
    string MediaType);

/// <summary>Everything the count is read from. Gathered by <see cref="AttentionSummaryService"/>; the builder only reads it.</summary>
public sealed record AttentionSources(
    DashboardSummary? Summary,
    DownloadTelemetryOverview? Telemetry,
    IReadOnlyList<MovieImportRecoveryCase> MovieCases,
    IReadOnlyList<SeriesImportRecoveryCase> SeriesCases,
    IReadOnlyList<JobQueueItem> FailedJobs,
    IReadOnlyList<ProcessorHandoffItem> Handoffs,
    IReadOnlyList<DownloadDispatchItem> Dispatches,
    IReadOnlyList<IndexerItem> Indexers,
    IReadOnlyList<ProcessorConnectionItem> Processors,
    IReadOnlyList<HealthCheckResultItem> Health,
    IReadOnlyList<AttentionEndpoints.NoReleaseRow> NoRelease,
    IReadOnlyList<ConnectionUpdateItem> ConnectionUpdates,
    DateTimeOffset Now);

/// <summary>
/// The one definition of what needs a person (#1185). The bell asks for the rows through <c>GET /api/attention/summary</c>;
/// the tray calls <see cref="AttentionSummaryService"/> in the same process. Nothing else adds the number up, so the two
/// can only differ by being read at different moments.
/// </summary>
public static class AttentionSummaryBuilder
{
    public const string Media = "media";
    public const string Deluno = "deluno";
    private const string Bad = "bad";
    private const string Warn = "warn";
    private const string ImportJobType = "filesystem.import.execute";

    public static AttentionSummary Build(AttentionSources source)
    {
        var rows = new List<AttentionRow>();
        var notices = new List<AttentionRow>();
        var summary = source.Summary;
        var system = summary?.System;
        var now = source.Now;

        // ------------------------------------------------------------ your media
        var imported = ImportedDownloads(source.Dispatches);
        var failedHandoffs = source.Handoffs
            .Where(handoff => ProcessorHandoffStatuses.NeedsPerson(handoff.Status, handoff.FailureMessage))
            .Where(handoff => !imported.ByClientItem.Contains(ClientItem(handoff.ClientId, handoff.QueueItemId)))
            .ToList();

        // One failure is one row (#1167): a failed hand-off names its download by client and item, and the dispatch,
        // the dead-lettered import job and the case behind that same download are the same problem. The hand-off
        // carries the most specific cause and the fixes, so it is the row that stays.
        var handoffClientItems = failedHandoffs.Select(handoff => ClientItem(handoff.ClientId, handoff.QueueItemId)).ToHashSet(StringComparer.Ordinal);
        var handoffDispatchIds = source.Dispatches
            .Where(dispatch => !string.IsNullOrEmpty(dispatch.TorrentHashOrItemId) && handoffClientItems.Contains(ClientItem(dispatch.DownloadClientId, dispatch.TorrentHashOrItemId!)))
            .Select(dispatch => dispatch.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handoffJobIds = failedHandoffs.Select(handoff => handoff.ImportJobId).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToHashSet(StringComparer.Ordinal);
        var handoffIdsWithTheirOwnRow = source.Handoffs.Select(handoff => handoff.Id).ToHashSet(StringComparer.Ordinal);

        var cases = source.MovieCases.Select(item => new AttentionRecoveryData(item.Id, item.Title, item.FailureKind, item.Status, item.Summary, item.RecommendedAction, item.DetailsJson, item.DetectedUtc, item.ResolvedUtc, "movie"))
            .Concat(source.SeriesCases.Select(item => new AttentionRecoveryData(item.Id, item.Title, item.FailureKind, item.Status, item.Summary, item.RecommendedAction, item.DetailsJson, item.DetectedUtc, item.ResolvedUtc, "series")))
            .Where(item =>
            {
                var dispatchId = DispatchIdIn(item.DetailsJson);
                if (dispatchId is not null && (imported.ByDispatchId.Contains(dispatchId) || handoffDispatchIds.Contains(dispatchId)))
                {
                    return false;
                }

                var clientItem = ClientItemIn(item.DetailsJson);
                if (clientItem is not null && handoffClientItems.Contains(clientItem))
                {
                    return false;
                }

                var handoffId = HandoffIdIn(item.DetailsJson);
                return handoffId is null || !handoffIdsWithTheirOwnRow.Contains(handoffId);
            })
            .ToList();

        // A download a case is about is that case, and the client's copy of it is the same problem (#603).
        var coveredByCase = cases.Select(item => DispatchIdIn(item.DetailsJson)).Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal);
        var caseClientItems = cases.Select(item => ClientItemIn(item.DetailsJson)).Where(key => key is not null).Select(key => key!).ToHashSet(StringComparer.Ordinal);
        var coveredClientItems = new HashSet<string>(caseClientItems, StringComparer.Ordinal);
        foreach (var dispatch in source.Dispatches.Where(dispatch => coveredByCase.Contains(dispatch.Id) && !string.IsNullOrEmpty(dispatch.TorrentHashOrItemId)))
        {
            coveredClientItems.Add(ClientItem(dispatch.DownloadClientId, dispatch.TorrentHashOrItemId!));
        }

        coveredClientItems.UnionWith(handoffClientItems);

        var queueAttention = (source.Telemetry?.Clients ?? [])
            .SelectMany(client => client.Queue)
            .Where(QueueItemNeedsAttention)
            .Where(item => !imported.ByClientItem.Contains(ClientItem(item.ClientId, item.Id)))
            .Where(item => !coveredClientItems.Contains(ClientItem(item.ClientId, item.Id)))
            .ToList();

        var failedDispatchIds = source.Dispatches.Where(IsDispatchFailure).Select(dispatch => dispatch.Id).ToHashSet(StringComparer.Ordinal);
        var failedImportJobs = source.FailedJobs
            .Where(job => job.JobType == ImportJobType && !IsRetrying(job))
            .Where(job =>
            {
                if (handoffJobIds.Contains(job.Id))
                {
                    return false;
                }

                var dispatchId = DispatchIdIn(job.PayloadJson);
                return dispatchId is null || !(coveredByCase.Contains(dispatchId) || failedDispatchIds.Contains(dispatchId) || handoffDispatchIds.Contains(dispatchId));
            })
            .ToList();

        foreach (var item in queueAttention)
        {
            rows.Add(new AttentionRow(
                $"stuck:{item.ClientId}:{item.Id}", Media, "stuck",
                item.Status == DownloadQueueStatuses.Stalled || !string.IsNullOrWhiteSpace(item.ErrorMessage) ? Bad : Warn,
                string.IsNullOrWhiteSpace(item.Title) ? item.ReleaseName : item.Title,
                StuckReason(item), "/queue", "Open In progress", 1, item.LastActivityUtc ?? item.AddedUtc, item));
        }

        foreach (var item in cases)
        {
            rows.Add(new AttentionRow(
                $"recovery:{item.MediaType}:{item.Id}", Media, "recovery", Warn, item.Title,
                $"{(item.MediaType == "movie" ? "Movies" : "TV")}: {item.Summary}",
                "/queue", "Open In progress", 1, item.DetectedUtc, item));
        }

        foreach (var job in failedImportJobs)
        {
            rows.Add(new AttentionRow(
                $"import:{job.Id}", Media, "import", Bad, "An import didn’t finish",
                string.IsNullOrWhiteSpace(job.LastError) ? "It stopped without saying why." : PlainFailure(job.LastError, "The import"),
                "/queue", "Open In progress", 1, job.CompletedUtc ?? job.CreatedUtc, new { id = job.Id }));
        }

        foreach (var handoff in failedHandoffs)
        {
            var processor = string.IsNullOrWhiteSpace(handoff.ProcessorName) ? null : handoff.ProcessorName;
            rows.Add(new AttentionRow(
                $"handoff:{handoff.Id}", Media, "handoff", Bad, handoff.ReleaseName,
                string.IsNullOrWhiteSpace(handoff.FailureMessage)
                    ? $"{processor ?? "The processor"} could not finish it."
                    : PlainFailure(handoff.FailureMessage, $"Sending it to {processor ?? "the processor"}"),
                "/queue", "Open In progress", 1, handoff.UpdatedUtc, new
                {
                    id = handoff.Id,
                    releaseName = handoff.ReleaseName,
                    status = handoff.Status,
                    outputPath = handoff.OutputPath,
                    importJobId = handoff.ImportJobId,
                    outcome = handoff.Outcome is null ? null : new { outcome = handoff.Outcome.Outcome }
                }));
        }

        foreach (var title in source.NoRelease)
        {
            rows.Add(new AttentionRow(
                $"no-release:{title.MediaType}:{title.Id}", Media, "no-release", Warn,
                title.Year is { } year ? $"{title.Title} ({year})" : title.Title,
                "No acceptable release yet: every search found nothing Deluno would take.",
                title.MediaType == "movie" ? $"/movies/{Uri.EscapeDataString(title.Id)}" : $"/tv/{Uri.EscapeDataString(title.Id)}",
                "Open Wanted", 1, title.MissingSinceUtc ?? title.LastSearchUtc, title));
        }

        // ---------------------------------------------------------------- Deluno
        if (system?.InternetOutage == true)
        {
            rows.Add(Row("outage", "outage", Bad, "Deluno can't reach the internet",
                "Searching and cleanup are paused until it is back. Nothing was marked as a bad release.", "/system", "Open System"));
        }

        if (summary?.Automation?.QueuePause is { Enabled: true, Unreadable: true })
        {
            rows.Add(Row("queue-gate", "queue-gate", Warn, "Deluno can't read the download queue",
                "Pausing searches when the queue is long is not working until it can.", "/search-cycles", "Open Automation"));
        }

        var indexers = source.Indexers.Where(item => item.IsEnabled && !string.Equals(item.HealthStatus, "healthy", StringComparison.Ordinal)).ToList();
        var clients = (summary?.Downloads?.Clients ?? []).Where(ClientNeedsButton).ToList();
        var processors = source.Processors
            .Where(item => item.IsEnabled && item.HealthStatus is "unreachable" or "degraded")
            .ToList();

        foreach (var indexer in indexers)
        {
            rows.Add(new AttentionRow($"indexer:{indexer.Id}", Deluno, "connection", Warn,
                $"{indexer.Name} isn’t answering",
                string.IsNullOrWhiteSpace(indexer.LastHealthMessage) ? "Deluno can’t search this site until it answers." : indexer.LastHealthMessage,
                "/system", "Open System", 1, null, new { id = indexer.Id, name = indexer.Name }));
        }

        foreach (var client in clients)
        {
            // Warnings are cleared in the client's own drawer, through the client's API (#1193).
            var warningsOnly = client.Reachable && client.Paused != true;
            rows.Add(new AttentionRow($"client:{client.Id}", Deluno, "connection", Warn,
                !client.Reachable ? $"{client.Name} isn’t answering" : client.Paused == true ? $"{client.Name} is paused" : $"{client.Name} has warnings",
                !client.Reachable ? "Downloads can’t start there until it answers." : client.Paused == true ? "Nothing downloads there while it is paused." : "The download client reported something to clear. Open it to read the warnings and clear them.",
                warningsOnly ? ConnectionLink("downloadClient", client.Id) : "/system", warningsOnly ? "Clear warnings" : "Open System", 1, null, new { id = client.Id, name = client.Name }));
        }

        foreach (var processor in processors)
        {
            rows.Add(new AttentionRow($"processor:{processor.Id}", Deluno, "connection", Warn,
                $"{processor.Name} isn’t answering",
                string.IsNullOrWhiteSpace(processor.LastHealthMessage) ? "Finished downloads can’t be cleaned until it answers." : processor.LastHealthMessage,
                "/system", "Open System", 1, null, new { id = processor.Id, name = processor.Name }));
        }

        if ((summary?.Library?.Collections.SyncFailing ?? 0) > 0)
        {
            var failing = summary!.Library!.Collections.SyncFailing;
            rows.Add(Row("collections", "collections", Warn, $"{Plural(failing, "collection")} could not update",
                "The list a collection follows could not be read.", "/collections", "Collections"));
        }

        if ((summary?.Requests?.SeerrFailing ?? 0) > 0)
        {
            var failing = summary!.Requests!.SeerrFailing;
            rows.Add(Row("requests", "requests", Warn, $"{Plural(failing, "request server")} not answering",
                "New requests can't arrive until it answers.", "/indexers/media-servers", "Find & Download"));
        }

        // A health problem for a connection that already has its own row is that row, and a snoozed one is not counted.
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in indexers) covered.Add($"indexer:{item.Id}");
        foreach (var item in clients) covered.Add($"download-client:{item.Id}");
        foreach (var item in processors) covered.Add($"processor:{item.Id}");
        foreach (var problem in source.Health
                     .Where(check => string.Equals(check.Status, HealthCheckStatuses.Problem, StringComparison.OrdinalIgnoreCase) && !check.IsSnoozed(now))
                     .Where(check => !covered.Contains(check.Id))
                     .OrderBy(check => check.ProblemSinceUtc ?? DateTimeOffset.MaxValue)
                     .ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase))
        {
            var link = DashboardSummaryService.LinkFor(problem.Category);
            rows.Add(new AttentionRow($"health:{problem.Id}", Deluno, $"health:{problem.Category}", Warn,
                problem.Name, problem.Message, "/system", "Open System", 1, problem.ProblemSinceUtc,
                new { id = problem.Id, category = problem.Category, name = problem.Name, link }));
        }

        // A job that gave up is one Retry row. A failed import is under In progress, and a job Deluno will try again by
        // itself is not a problem. Split by where each is cleared (#1114).
        var stopped = source.FailedJobs
            .Where(job => job.JobType != ImportJobType && IsFailed(job.Status) && !IsRetrying(job))
            .ToList();
        var backgroundGaveUp = stopped.Count(IsBackgroundJob);
        var titleGaveUp = stopped.Count - backgroundGaveUp;
        if (backgroundGaveUp > 0)
        {
            rows.Add(Row("failed-jobs", "jobs", Bad, $"{Plural(backgroundGaveUp, "background job")} gave up",
                "Nothing else waits on them, but the work was not done.", "/system", "Open System"));
        }

        if (titleGaveUp > 0)
        {
            rows.Add(Row("failed-title-jobs", "jobs", Bad, $"{Plural(titleGaveUp, "job")} about a title gave up",
                "They failed on every attempt. Open them in Activity to retry or dismiss.", "/activity?status=failed", "Open Activity"));
        }

        var failingLanes = system?.Lanes.Count(lane => lane.State == "failing") ?? 0;
        if (failingLanes > 0)
        {
            rows.Add(new AttentionRow("lanes", Deluno, "lanes", Bad,
                $"{Plural(failingLanes, "background worker")} {(failingLanes == 1 ? "keeps" : "keep")} failing",
                "Deluno keeps trying, but this part is not getting its work done.", "/system", "Open System", failingLanes, null, null));
        }

        // ---------------------------------------------------- ready, but not a problem
        if (system?.UpdateAvailable == true)
        {
            notices.Add(new AttentionRow("update", Deluno, "update", Warn, "A new version of Deluno is ready",
                "Nothing is installed until you say so.", "/system/updates", "Open Updates", 1, null, null));
        }

        foreach (var entry in source.ConnectionUpdates.Where(entry => entry.Notice))
        {
            notices.Add(new AttentionRow($"connection-update:{entry.Kind}:{entry.Id}", Deluno, "connection-update", Warn,
                $"{entry.AppName} {entry.LatestVersion} is out · you have {entry.Version}",
                entry.Mode == "updateNow" ? "Open it to read the release notes or update now." : entry.Message,
                ConnectionLink(entry.Kind, entry.Id), "Open", 1, null, new { kind = entry.Kind, id = entry.Id }));
        }

        var leftovers = summary?.Downloads?.Leftovers is { Scanned: true } scanned ? scanned.Count : 0;
        var ordered = rows.Select((row, index) => (row, index)).OrderBy(entry => entry.row.Tone == Bad ? 0 : 1).ThenBy(entry => entry.index).Select(entry => entry.row).ToArray();
        return new AttentionSummary(ordered.Sum(row => row.Count), ordered, notices, leftovers, summary?.Health?.Suggestions ?? 0);
    }

    private static AttentionRow Row(string id, string kind, string tone, string title, string text, string to, string action)
        => new(id, Deluno, kind, tone, title, text, to, action, 1, null, null);

    /// <summary>A client row has a button when it is not answering, is paused, or has warnings to clear.</summary>
    internal static bool ClientNeedsButton(DashboardClientRow client) => !client.Reachable || client.Paused == true || client.Warnings > 0;

    /// <summary>Where a connection is, so an update notice opens that app's own connection.</summary>
    internal static string ConnectionLink(string kind, string id)
    {
        var query = $"?connection={Uri.EscapeDataString(id)}";
        return kind switch
        {
            "downloadClient" => $"/indexers/download-clients{query}",
            "processor" => $"/settings/processing{query}",
            "mediaServer" or "requests" => $"/indexers/media-servers{query}",
            _ => "/system"
        };
    }

    private static bool QueueItemNeedsAttention(DownloadQueueItem item)
    {
        // A download that is in the library has nothing left to ask of anyone, whatever its client said on the way.
        if (item.Status == DownloadQueueStatuses.Imported)
        {
            return false;
        }

        return item.Status == DownloadQueueStatuses.Stalled || !string.IsNullOrEmpty(item.ErrorMessage) || item.HealthFindings is { Count: > 0 };
    }

    private static string StuckReason(DownloadQueueItem item)
    {
        var finding = item.HealthFindings?.FirstOrDefault();
        if (!string.IsNullOrEmpty(item.ErrorMessage))
        {
            return PlainFailure(item.ErrorMessage, $"Talking to {item.ClientName} about it");
        }

        if (!string.IsNullOrEmpty(finding?.Summary))
        {
            return finding!.Summary;
        }

        return item.Status == DownloadQueueStatuses.Stalled ? $"Not moving in {item.ClientName}." : $"{item.ClientName} reported a problem with it.";
    }

    /// <summary>
    /// A failure says which step failed and what to do, never a bare "Error: timed out" (#1167). A message that is only
    /// a generic word is dressed with the step; a message that already says something is left as it is.
    /// </summary>
    internal static string PlainFailure(string? raw, string step)
    {
        var message = (raw ?? string.Empty).Trim();
        var bare = message;
        if (bare.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[5..].TrimStart(':').TrimStart();
        }

        bare = bare.TrimEnd('.', '!', ' ', '\t', '\r', '\n').ToLowerInvariant();
        if (message.Length == 0 || bare is "timed out" or "timeout" or "time out" or "failed" or "unknown" or "unknown error" or "error")
        {
            var what = message.Length == 0 || bare is "failed" or "error" || bare.StartsWith("unknown", StringComparison.Ordinal) ? "failed" : "timed out";
            return $"{step} {what}. If the title is already where it belongs, dismiss this; otherwise try again.";
        }

        return message;
    }

    private static string Plural(int count, string one, string? many = null)
        => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? one : many ?? one + "s")}";

    private static string ClientItem(string clientId, string itemId) => $"{clientId}:{itemId}".ToLowerInvariant();

    private static bool IsFailed(string status) => status is "failed" or "dead-letter";

    /// <summary>A job that failed an attempt and has another coming is retrying, not waiting on a person (#562).</summary>
    private static bool IsRetrying(JobQueueItem job)
        => job.Status == "failed" && job.NextAttemptUtc is not null && job.Attempts < job.MaxAttempts;

    /// <summary>A background job is one that is not about a title (#1114). Importing a download is always about a title.</summary>
    private static bool IsBackgroundJob(JobQueueItem job)
        => job.JobType != ImportJobType && string.IsNullOrWhiteSpace(job.RelatedEntityId);

    private static readonly string[] DispatchFailureWords = ["failed", "blocked", "rejected", "unresolved", "error", GrabStatuses.StoredNotFound, GrabStatuses.StoredCircuitOpen, GrabStatuses.StoredPaused];

    private static bool IsDispatchFailure(DownloadDispatchItem dispatch)
    {
        // Deluno's own stall rules blocked, removed and replaced this download (#1167): Activity's story, not a failure.
        if (string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return new[] { dispatch.Status, dispatch.GrabStatus, dispatch.ImportStatus, dispatch.GrabFailureCode, dispatch.ImportFailureCode }
            .Any(value => !string.IsNullOrEmpty(value) && DispatchFailureWords.Contains(value.ToLowerInvariant()))
            || dispatch.Failure is not null;
    }

    /// <summary>An alarm resolves itself when what it was about did: a download since imported has nothing left to ask.</summary>
    private static (HashSet<string> ByClientItem, HashSet<string> ByDispatchId) ImportedDownloads(IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var byClientItem = new HashSet<string>(StringComparer.Ordinal);
        var byDispatchId = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dispatch in dispatches.Where(dispatch => string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase)))
        {
            byDispatchId.Add(dispatch.Id);
            if (!string.IsNullOrEmpty(dispatch.TorrentHashOrItemId))
            {
                byClientItem.Add(ClientItem(dispatch.DownloadClientId, dispatch.TorrentHashOrItemId));
            }
        }

        return (byClientItem, byDispatchId);
    }

    /// <summary>The download a case or job is about, from the details written with it (at the top, or inside the retry it keeps, #602).</summary>
    internal static string? DispatchIdIn(string? json)
        => Details(json, root => Text(root, "DispatchId") ?? (Obj(root, "RetryRequest") is { } retry ? Text(retry, "DispatchId") : null));

    private static string? HandoffIdIn(string? json) => Details(json, root => Text(root, "HandoffId"));

    /// <summary>The client's entry for the download a case is about (<c>clientId:queueItemId</c>, lower case, #1167).</summary>
    private static string? ClientItemIn(string? json)
        => Details(json, root => Text(root, "ClientId") is { } clientId && Text(root, "QueueItemId") is { } itemId ? ClientItem(clientId, itemId) : null);

    private static string? Details(string? json, Func<JsonElement, string?> read)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Obj(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    private static string? Text(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
