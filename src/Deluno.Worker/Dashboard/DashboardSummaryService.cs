using System.Collections.Concurrent;
using System.Diagnostics;
using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Updates;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Dashboard;

public interface IDashboardSummaryService
{
    /// <summary>The summary, built at most once every few seconds however many callers ask.</summary>
    Task<DashboardSummary> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Assembles <see cref="DashboardSummary"/> (#1082) from stored rows and
/// in-memory readings only (#1081): no download client, indexer, media server
/// or folder is called or walked while a person waits.
///
/// <para>Every section is built at the same time, each with its own scope,
/// its own time budget and its own failure: a source that throws or runs long
/// leaves its section null and the rest of the response intact. The whole
/// answer is remembered for <see cref="MemoLifetime"/> and built once for any
/// number of simultaneous callers.</para>
/// </summary>
public sealed class DashboardSummaryService : IDashboardSummaryService
{
    /// <summary>How long one built summary answers every caller.</summary>
    public static readonly TimeSpan MemoLifetime = TimeSpan.FromSeconds(4);

    /// <summary>How long a single section may take before it is left out of the response.</summary>
    public static readonly TimeSpan SectionBudget = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DispatchTotalsLifetime = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DashboardSummaryService> _logger;
    private readonly AsyncValueCache<DashboardSummary> _memo;
    private readonly AsyncValueCache<DispatchMetrics> _dispatchTotals;

    public DashboardSummaryService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DashboardSummaryService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _memo = new AsyncValueCache<DashboardSummary>(MemoLifetime, timeProvider);
        _dispatchTotals = new AsyncValueCache<DispatchMetrics>(DispatchTotalsLifetime, timeProvider, serveStale: true);
    }

    /// <summary>How many times the summary has actually been built (not served from memory).</summary>
    public int Builds => _memo.Builds;

    public Task<DashboardSummary> GetAsync(CancellationToken cancellationToken)
        => _memo.GetAsync(BuildAsync, cancellationToken);

    /// <summary>Drop the remembered summary; the next caller builds a fresh one.</summary>
    public void Invalidate() => _memo.Invalidate();

    private async Task<DashboardSummary> BuildAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var now = _timeProvider.GetUtcNow();
        var unavailable = new ConcurrentBag<string>();

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var library = Run("library", unavailable, () => LibraryAsync(services, now));
        var downloads = Run("downloads", unavailable, () => DownloadsAsync(services, now));
        var pipeline = Run("pipeline", unavailable, () => PipelineAsync(services, now));
        var imports = Run("imports", unavailable, () => ImportsAsync(services, now));
        var automation = Run("automation", unavailable, () => AutomationAsync(services, now));
        var indexers = Run("indexers", unavailable, () => IndexersAsync(services, now));
        var processors = Run("processors", unavailable, () => ProcessorsAsync(services, now));
        var retention = Run("retention", unavailable, () => RetentionAsync(services, now));
        var subtitles = Run("subtitles", unavailable, () => SubtitlesAsync(services, now));
        var requests = Run("requests", unavailable, () => RequestsAsync(services, now));
        var blocklist = Run("blocklist", unavailable, () => BlocklistAsync(services, now));
        var health = Run("health", unavailable, () => HealthAsync(services, now));
        var storage = Run("storage", unavailable, () => StorageAsync(services, now));
        var system = Run("system", unavailable, () => SystemAsync(services, now));
        var activity = Run("activity", unavailable, () => ActivityAsync(services, now));

        await Task.WhenAll(library, downloads, pipeline, imports, automation, indexers, processors, retention,
            subtitles, requests, blocklist, health, storage, system, activity);

        clock.Stop();
        return new DashboardSummary(
            AsOfUtc: now,
            BuildMilliseconds: (int)clock.ElapsedMilliseconds,
            UnavailableSections: unavailable.Order(StringComparer.Ordinal).ToArray(),
            Library: library.Result,
            Downloads: downloads.Result,
            Pipeline: pipeline.Result,
            Imports: imports.Result,
            Automation: automation.Result,
            Indexers: indexers.Result,
            Processors: processors.Result,
            Retention: retention.Result,
            Subtitles: subtitles.Result,
            Requests: requests.Result,
            Blocklist: blocklist.Result,
            Health: health.Result,
            Storage: storage.Result,
            System: system.Result,
            Activity: activity.Result);
    }

    /// <summary>Runs one section on its own, so it can be slow or fail without touching the others.</summary>
    private async Task<T?> Run<T>(string name, ConcurrentBag<string> unavailable, Func<Task<T>> build)
        where T : class
    {
        try
        {
            return await Task.Run(build).WaitAsync(SectionBudget);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The Dashboard's {Section} section took longer than {Budget} and was left out.", name, SectionBudget);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The Dashboard's {Section} section could not be read and was left out.", name);
        }

        unavailable.Add(name);
        return null;
    }

    private DateTimeOffset StartOfToday(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeProvider.LocalTimeZone);
        var midnight = new DateTimeOffset(local.Date, local.Offset);
        return midnight.ToUniversalTime();
    }

    // --------------------------------------------------------------- library

    private static async Task<DashboardLibrarySection> LibraryAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var moviesTask = sp.GetRequiredService<IMovieCatalogRepository>().GetDashboardCountsAsync(CancellationToken.None);
        var showsTask = sp.GetRequiredService<ISeriesCatalogRepository>().GetDashboardCountsAsync(CancellationToken.None);
        var librariesTask = sp.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var store = sp.GetRequiredService<DashboardCountsStore>();
        var mixTask = store.GetLibraryMixAsync(CancellationToken.None);
        var collectionsTask = store.GetCollectionCountsAsync(CancellationToken.None);
        await Task.WhenAll(moviesTask, showsTask, librariesTask, mixTask, collectionsTask);

        var movies = moviesTask.Result;
        var shows = showsTask.Result;
        var bytes = librariesTask.Result
            .Select(library =>
            {
                var source = string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    ? shows.FileBytesByLibrary
                    : movies.FileBytesByLibrary;
                return new DashboardLibraryBytes(library.Id, library.Name, library.MediaType, source.GetValueOrDefault(library.Id));
            })
            .ToArray();

        return new DashboardLibrarySection(
            now,
            Counts(movies),
            Counts(shows),
            movies.FileBytes + shows.FileBytes,
            bytes,
            new DashboardLibraryMix(
                Mix(mixTask.Result.MovieQuality),
                Mix(mixTask.Result.MovieCodec),
                Mix(mixTask.Result.EpisodeQuality),
                Mix(mixTask.Result.ShowCodec)),
            new DashboardCollections(
                collectionsTask.Result.Followed,
                collectionsTask.Result.Monitored,
                collectionsTask.Result.Members,
                collectionsTask.Result.InLibrary,
                collectionsTask.Result.SyncFailing,
                collectionsTask.Result.LastSyncedUtc));

        static DashboardMediaCounts Counts(MediaDashboardCounts counts) => new(
            counts.Facets.All,
            counts.Facets.Monitored,
            counts.Facets.Unmonitored,
            counts.Facets.Missing,
            counts.Facets.Upgrades,
            counts.Facets.Covered,
            counts.Facets.Upcoming,
            counts.Facets.Downloading,
            counts.Facets.Airing,
            counts.FileBytes);

        static IReadOnlyList<DashboardMixEntry> Mix(IReadOnlyDictionary<string, int> counts)
        {
            var ordered = counts
                .Select(pair => new DashboardMixEntry(string.IsNullOrWhiteSpace(pair.Key) ? "Unknown" : pair.Key, pair.Value))
                .GroupBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DashboardMixEntry(group.First().Label, group.Sum(entry => entry.Count)))
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count <= 8)
            {
                return ordered;
            }

            var rest = ordered.Skip(7).Sum(entry => entry.Count);
            return [.. ordered.Take(7), new DashboardMixEntry("Other", rest)];
        }
    }

    // ------------------------------------------------------------- downloads

    private static async Task<DashboardDownloadsSection> DownloadsAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var reading = sp.GetRequiredService<IDashboardDownloadsSnapshot>().Latest;
        var clientsTask = sp.GetRequiredService<Deluno.Connections.Data.IConnectionsRepository>().ListDownloadClientsAsync(CancellationToken.None);
        var sharingTask = sp.GetRequiredService<IDownloadSharingRepository>().GetSnapshotAsync(CancellationToken.None);
        var failedTask = sp.GetRequiredService<DashboardCountsStore>().CountFailedDownloadsAsync(CancellationToken.None);
        await Task.WhenAll(clientsTask, sharingTask, failedTask);

        var leftover = sp.GetRequiredService<LeftoverScanMemory>().Last;
        var snapshots = reading?.Clients.ToDictionary(client => client.ClientId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DownloadClientTelemetrySnapshot>(StringComparer.OrdinalIgnoreCase);

        var rows = clientsTask.Result
            .Where(client => client.IsEnabled)
            .Select(client =>
            {
                snapshots.TryGetValue(client.Id, out var snapshot);
                var reachable = snapshot is not null
                    ? snapshot.LastFailure is null
                    : !IsBadHealth(client.HealthStatus);
                return new DashboardClientRow(
                    client.Id,
                    client.Name,
                    client.Protocol,
                    reachable,
                    snapshot?.ClientPaused,
                    snapshot?.ClientVersion,
                    snapshot?.ClientWarnings?.Count ?? 0,
                    snapshot?.FreeSpaceBytes,
                    client.HealthStatus,
                    snapshot?.Summary.ActiveCount ?? 0,
                    snapshot?.Summary.QueuedCount ?? 0);
            })
            .ToArray();

        var summary = reading?.Summary;
        return new DashboardDownloadsSection(
            now,
            HasReading: reading is not null,
            ReadingUtc: reading?.CapturedUtc,
            Active: summary?.ActiveCount ?? 0,
            Queued: summary?.QueuedCount ?? 0,
            Stalled: summary?.StalledCount ?? 0,
            Processing: summary?.ProcessingCount ?? 0,
            ImportReady: summary?.ImportReadyCount ?? 0,
            WaitingForProcessor: summary?.WaitingForProcessorCount ?? 0,
            DownloadMbps: Math.Round(summary?.TotalSpeedMbps ?? 0, 2),
            UploadMbps: Math.Round(summary?.TotalUploadSpeedMbps ?? 0, 2),
            Clients: rows,
            SharingHolds: sharingTask.Result.Holds.Count,
            SharingExtraBytes: sharingTask.Result.ExtraBytes,
            Leftovers: leftover is null
                ? new DashboardLeftovers(false, 0, 0, null)
                : new DashboardLeftovers(true, leftover.Count, leftover.Bytes, leftover.ScannedUtc),
            FailedDownloads: failedTask.Result);
    }

    private static bool IsBadHealth(string? status)
        => status is not null && status.ToLowerInvariant() is "unreachable" or "unhealthy" or "failed" or "error";

    // -------------------------------------------------------------- pipeline

    private async Task<DashboardPipelineSection> PipelineAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var metricsRepository = sp.GetRequiredService<IDispatchMetricsRepository>();
        var totalsTask = _dispatchTotals.GetAsync(token => metricsRepository.GetMetricsAsync(token), CancellationToken.None);
        var dayTask = sp.GetRequiredService<DashboardCountsStore>().GetDispatchDayAsync(StartOfToday(now), CancellationToken.None);
        await Task.WhenAll(totalsTask, dayTask);

        var totals = totalsTask.Result;
        var day = dayTask.Result;
        return new DashboardPipelineSection(
            now,
            new DashboardPipelineDay(day.Grabbed, day.Detected, day.Imported, day.Failed),
            new DashboardPipelineTotals(
                totals.SuccessfulGrabs,
                totals.DetectedDownloads,
                totals.SuccessfulImports,
                totals.FailedGrabs,
                totals.FailedImports,
                totals.ActiveDispatchesCount),
            totals.AverageGrabToDetection.TotalSeconds <= 0 ? null : Math.Round(totals.AverageGrabToDetection.TotalSeconds, 1),
            totals.AverageDetectionToImport.TotalSeconds <= 0 ? null : Math.Round(totals.AverageDetectionToImport.TotalSeconds, 1));
    }

    // --------------------------------------------------------------- imports

    private static async Task<DashboardImportsSection> ImportsAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var store = sp.GetRequiredService<DashboardCountsStore>();
        var kindsTask = store.GetOpenRecoveryByKindAsync(CancellationToken.None);
        var jobsTask = store.CountFailedImportJobsAsync(CancellationToken.None);
        var processorsTask = store.GetProcessorCountsAsync(now, CancellationToken.None);
        await Task.WhenAll(kindsTask, jobsTask, processorsTask);

        return new DashboardImportsSection(
            now,
            kindsTask.Result.Values.Sum(),
            kindsTask.Result,
            jobsTask.Result,
            processorsTask.Result.FailedHandoffs);
    }

    // ------------------------------------------------------------ automation

    private static async Task<DashboardAutomationSection> AutomationAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var jobs = sp.GetRequiredService<IJobQueueRepository>();
        var settingsTask = sp.GetRequiredService<IPlatformSettingsRepository>().GetAsync(CancellationToken.None);
        var statesTask = jobs.ListLibraryAutomationStatesAsync(CancellationToken.None);
        var cyclesTask = jobs.ListSearchCycleRunsAsync(1, null, CancellationToken.None);
        var taskStatesTask = jobs.ListSystemTaskStatesAsync(CancellationToken.None);
        await Task.WhenAll(settingsTask, statesTask, cyclesTask, taskStatesTask);

        var settings = settingsTask.Result;
        var gate = BuildGate(sp, settings.PauseSearchWhenQueueLongEnabled, settings.PauseSearchWhenQueueLongThreshold);

        var libraries = statesTask.Result.Values
            .OrderBy(state => state.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Select(state => new DashboardLibraryAutomation(
                state.LibraryId,
                state.LibraryName,
                state.MediaType,
                state.Status,
                state.NextSearchUtc,
                state.NextMissingSearchUtc,
                state.NextUpgradeSearchUtc,
                state.LastCompletedUtc,
                state.LastError))
            .ToArray();

        var cycle = cyclesTask.Result.FirstOrDefault();
        var states = taskStatesTask.Result.ToDictionary(state => state.ScheduleKey, StringComparer.OrdinalIgnoreCase);
        var tasks = SystemTasks.All.Select(task =>
        {
            states.TryGetValue(task.Key, out var state);
            return new DashboardScheduledTask(
                task.Key,
                task.Name,
                state?.LastResult ?? "not-run",
                state?.LastCompletedUtc,
                state?.NextRunUtc);
        }).ToArray();

        return new DashboardAutomationSection(
            now,
            settings.AutoStartJobs,
            gate,
            libraries,
            cycle is null
                ? null
                : new DashboardSearchCycle(
                    cycle.LibraryId,
                    cycle.LibraryName,
                    cycle.MediaType,
                    cycle.Status,
                    cycle.PlannedCount,
                    cycle.QueuedCount,
                    cycle.SkippedCount,
                    cycle.StartedUtc,
                    cycle.CompletedUtc),
            tasks);
    }

    /// <summary>The pause gate's answer for the last download reading; never asks a client.</summary>
    private static DashboardQueuePauseGate BuildGate(IServiceProvider sp, bool enabled, int threshold)
    {
        var limit = Math.Max(1, threshold);
        if (!enabled)
        {
            return new DashboardQueuePauseGate(false, limit, false, false, 0, 0, false, null);
        }

        var reading = sp.GetRequiredService<IDashboardDownloadsSnapshot>().Latest;
        if (reading is null)
        {
            // Nothing read yet: the gate cannot say either way, and does not pretend to.
            return new DashboardQueuePauseGate(true, limit, false, false, 0, 0, true, "The download queue has not been read yet.");
        }

        var movies = SearchQueuePauseGate.Decide(limit, "movies", reading);
        var tv = SearchQueuePauseGate.Decide(limit, "tv", reading);
        return new DashboardQueuePauseGate(
            true,
            limit,
            movies.Paused,
            tv.Paused,
            movies.Waiting,
            tv.Waiting,
            movies.Unreadable || tv.Unreadable,
            movies.UnreadableReason ?? tv.UnreadableReason);
    }

    // -------------------------------------------------------------- indexers

    private async Task<DashboardIndexersSection> IndexersAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var indexersTask = sp.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(CancellationToken.None);
        var statsTask = sp.GetRequiredService<IIndexerQueryStatsRepository>().GetScoreboardAsync(StartOfToday(now), now, CancellationToken.None);
        await Task.WhenAll(indexersTask, statsTask);

        var indexers = indexersTask.Result;
        var enabled = indexers.Where(indexer => indexer.IsEnabled).ToArray();
        bool RateLimited(Deluno.Connections.Contracts.IndexerItem item) => item.RateLimitedUntilUtc is { } until && until > now;

        var rateLimited = enabled.Count(RateLimited);
        var healthy = enabled.Count(indexer => string.Equals(indexer.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase) && !RateLimited(indexer));
        var failingRows = enabled
            .Where(indexer => !RateLimited(indexer) && IsFailing(indexer.HealthStatus))
            .OrderByDescending(indexer => indexer.ConsecutiveFailures)
            .ThenBy(indexer => indexer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var stats = statsTask.Result;
        return new DashboardIndexersSection(
            now,
            indexers.Count,
            healthy,
            failingRows.Length,
            rateLimited,
            indexers.Count - enabled.Length,
            (int)stats.TotalQueries,
            (int)stats.QueryStats.Sum(item => item.FailedQueries),
            (int)stats.TotalGrabs,
            failingRows
                .Take(5)
                .Select(indexer => new DashboardFailingIndexer(
                    indexer.Id,
                    indexer.Name,
                    indexer.LastHealthMessage ?? indexer.DisabledReason ?? indexer.HealthStatus))
                .ToArray());

        static bool IsFailing(string? status)
            => status is not null && status.ToLowerInvariant() is not ("healthy" or "untested" or "unknown");
    }

    // ------------------------------------------------------------ processors

    private static async Task<DashboardProcessorsSection> ProcessorsAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var counts = await sp.GetRequiredService<DashboardCountsStore>().GetProcessorCountsAsync(StartOfLocalDayFallback(sp, now), CancellationToken.None);
        return new DashboardProcessorsSection(now, counts.Total, counts.Healthy, counts.Failing, counts.HandoffsToday, counts.Waiting);
    }

    private static DateTimeOffset StartOfLocalDayFallback(IServiceProvider sp, DateTimeOffset now)
    {
        var provider = sp.GetRequiredService<TimeProvider>();
        var local = TimeZoneInfo.ConvertTime(now, provider.LocalTimeZone);
        return new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
    }

    // ------------------------------------------------------------- retention

    private static async Task<DashboardRetentionSection> RetentionAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var counts = await sp.GetRequiredService<DashboardCountsStore>().GetRetentionCountsAsync(now.AddDays(-7), CancellationToken.None);
        return new DashboardRetentionSection(
            now,
            counts.Leaving,
            counts.Next
                .Select(title => new DashboardLeavingTitle(title.EntityId, title.Title, Deluno.Retention.RetentionLevels.MediaType(title.Level), title.RemoveAfterUtc))
                .ToArray(),
            counts.Ready,
            counts.Removed,
            counts.RemovedBytes);
    }

    // ------------------------------------------------------------- subtitles

    private static async Task<DashboardSubtitlesSection> SubtitlesAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var libraries = await sp.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(CancellationToken.None);
        var subtitles = sp.GetRequiredService<Deluno.Media.IMediaSubtitleRepository>();

        var counts = await Task.WhenAll(libraries
            .Where(library => library.SubtitleLanguages is { Count: > 0 })
            .Select(library => subtitles.CountWantedAsync(
                string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? MediaKind.Series : MediaKind.Movie,
                library.Id,
                library.SubtitleLanguages!,
                library.SubtitleEmbeddedCounts,
                library.SubtitlePictureBasedCounts,
                CancellationToken.None)));

        var wanted = counts.Sum();
        var filesByLibrary = (await sp.GetRequiredService<DashboardCountsStore>().GetLibraryMixAsync(CancellationToken.None)).FilesByLibrary;
        var files = libraries
            .Where(library => library.SubtitleLanguages is { Count: > 0 })
            .Sum(library => filesByLibrary.GetValueOrDefault(library.Id));
        return new DashboardSubtitlesSection(now, wanted, Math.Max(0, files - wanted));
    }

    // -------------------------------------------------------------- requests

    private static async Task<DashboardRequestsSection> RequestsAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var store = sp.GetRequiredService<DashboardCountsStore>();
        var counts = await store.GetRequestCountsAsync(now.AddDays(-7), CancellationToken.None);
        var listTitlesAdded = await store.CountListTitlesAddedAsync(now.AddDays(-7), CancellationToken.None);
        return new DashboardRequestsSection(
            now,
            counts.Servers,
            counts.Healthy,
            counts.Failing,
            counts.AcquisitionsByStatus,
            counts.ImportListsByLastSync.Values.Sum(),
            counts.ImportListsByLastSync,
            listTitlesAdded);
    }

    // ------------------------------------------------------------- blocklist

    private static async Task<DashboardBlocklistSection> BlocklistAsync(IServiceProvider sp, DateTimeOffset now)
        => new(now, await sp.GetRequiredService<DashboardCountsStore>().CountBlockedReleasesAsync(CancellationToken.None));

    // ---------------------------------------------------------------- health

    private static async Task<DashboardHealthSection> HealthAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var repository = sp.GetRequiredService<IHealthCheckRepository>();
        var summaryTask = repository.GetSummaryAsync(CancellationToken.None);
        var checksTask = repository.ListAsync(CancellationToken.None);
        await Task.WhenAll(summaryTask, checksTask);

        var top = checksTask.Result
            .Where(check => string.Equals(check.Status, HealthCheckStatuses.Problem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(check => check.IsSnoozed(now))
            .ThenBy(check => check.ProblemSinceUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(check => new DashboardHealthProblem(
                check.Id,
                check.Name,
                check.Category,
                check.Message,
                check.ProblemSinceUtc,
                LinkFor(check.Category)))
            .ToArray();

        var summary = summaryTask.Result;
        return new DashboardHealthSection(now, summary.ProblemCount, summary.UnknownCount, summary.SuggestionCount, summary.LastRunUtc, top);
    }

    /// <summary>The screen that fixes a problem of this kind; Health itself when there is no better one.</summary>
    internal static string LinkFor(string category) => category switch
    {
        HealthCheckCategories.DownloadClient => "/indexers/download-clients",
        HealthCheckCategories.Indexer => "/indexers/indexers",
        HealthCheckCategories.Library => "/settings/libraries",
        HealthCheckCategories.Processor => "/settings/processing",
        HealthCheckCategories.MediaServer => "/indexers/media-servers",
        HealthCheckCategories.Seerr => "/indexers/media-servers",
        HealthCheckCategories.Metadata => "/settings/metadata",
        HealthCheckCategories.SubtitleProvider => "/indexers/subtitle-providers",
        HealthCheckCategories.Proxy => "/indexers/proxies",
        HealthCheckCategories.RemotePathMapping => "/indexers/path-mappings",
        HealthCheckCategories.Tags => "/settings/tags",
        _ => "/system"
    };

    // --------------------------------------------------------------- storage

    private static Task<DashboardStorageSection> StorageAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var cache = sp.GetRequiredService<DashboardVolumeCache>();
        var reading = cache.Latest;
        if (reading is null)
        {
            // Never a disk call here: start the first reading in the background
            // and say there is nothing to show yet.
            cache.EnsureReading();
            return Task.FromResult(new DashboardStorageSection(now, null, []));
        }

        return Task.FromResult(new DashboardStorageSection(now, reading.ReadingUtc, reading.Volumes));
    }

    // ---------------------------------------------------------------- system

    private static async Task<DashboardSystemSection> SystemAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var updatesTask = sp.GetRequiredService<IUpdateOrchestrator>().GetStatusAsync(CancellationToken.None);
        var backupTask = sp.GetRequiredService<IDelunoBackupService>().GetSettingsAsync(CancellationToken.None);
        var machineTask = sp.GetRequiredService<IMachineTelemetryRepository>().GetLatestAsync(CancellationToken.None);
        var jobCountsTask = sp.GetRequiredService<IJobQueueRepository>().CountJobsByStatusAsync(CancellationToken.None);
        var unreadTask = sp.GetRequiredService<INotificationService>().GetUnreadCountAsync(CancellationToken.None);
        await Task.WhenAll(updatesTask, backupTask, machineTask, jobCountsTask, unreadTask);

        var counts = jobCountsTask.Result;
        int JobsWith(params string[] statuses) => statuses.Sum(status => counts.GetValueOrDefault(status, 0));

        var lanes = sp.GetService<JobLaneStateBoard>()?.Snapshot()
            .Select(lane => new DashboardLane(lane.Name, lane.State, lane.ActiveJobs, lane.LastError))
            .ToArray() ?? [];

        // The last verdict the connectivity monitor reached; never a probe.
        var connectivity = sp.GetRequiredService<IConnectivityMonitor>().Current;
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var machine = machineTask.Result;

        return new DashboardSystemSection(
            now,
            DelunoVersion.Running,
            updatesTask.Result.UpdateAvailable,
            updatesTask.Result.LatestVersion,
            Math.Max(0, (long)(now - started).TotalSeconds),
            machine is null ? null : new DashboardMachine(machine.CapturedUtc, machine.CpuPercent, machine.MemoryBytes, machine.MemoryPercent),
            new DashboardJobCounts(JobsWith("running"), JobsWith("queued"), JobsWith("failed", "dead-letter")),
            lanes,
            backupTask.Result.LastRunUtc,
            backupTask.Result.NextRunUtc,
            backupTask.Result.Enabled,
            connectivity.IsOutage,
            connectivity.IsOutage ? connectivity.SinceUtc : null,
            unreadTask.Result);
    }

    // -------------------------------------------------------------- activity

    private static async Task<DashboardActivitySection> ActivityAsync(IServiceProvider sp, DateTimeOffset now)
    {
        var feed = sp.GetRequiredService<IActivityFeedRepository>();
        var recent = await feed.ListActivityAsync(8, null, null, CancellationToken.None);
        var stats = await feed
            .GetActivityStatsAsync(now.AddHours(-24), 24, CancellationToken.None);
        return new DashboardActivitySection(
            now,
            stats.WindowHours,
            stats.SearchesRun,
            stats.DownloadsStarted,
            stats.ImportsCompleted,
            stats.CleanupRemovals,
            stats.RetentionRemovals,
            recent
                .Select(item => new DashboardActivityItem(item.Id, item.Category, item.Message, item.RelatedEntityType, item.RelatedEntityId, item.CreatedUtc))
                .ToArray());
    }
}
