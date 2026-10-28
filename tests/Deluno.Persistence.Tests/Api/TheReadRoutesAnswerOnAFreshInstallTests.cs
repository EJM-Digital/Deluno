using System.Net;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1297, #1329 and the automation screen's reads: every read route that needs nothing but a signed-in caller answers a fresh install with
/// 200, JSON, the kind of body the screen reads (an empty list, or an object carrying the properties the screen asks for). The routes are
/// the real ones on the real host; a route that stopped answering, answered another kind of body or lost a property fails here.
/// </summary>
public sealed class TheReadRoutesAnswerOnAFreshInstallTests
{
    private static readonly (string Path, string Shape)[] Routes =
    [
        ("/api", "object:name,kind,architecture"),
        ("/api/backups/settings", "object:enabled,frequency,timeOfDay,retentionCount,backupFolder,lastRunUtc,nextRunUtc,problem,passwordSet"),
        ("/api/dashboard/metrics", "object:days,from,to,librarySize,titlesAdded,searches,jobs,importFailures,grabs"),
        ("/api/failure-rules", "array:reasonCode,category,decision,defaultDecision,isOverridden"),
        ("/api/monitoring/dashboard", "object:generatedUtc,readiness,storage,services,performance,alerts,machine"),
        ("/api/monitoring/diagnostics", "object:items,nextPageToken,hasMore"),
        ("/api/system/connectivity", "object:isOutage,sinceUtc,checkedUtc,checkedHosts,message"),
        ("/api/updates/status", "object:currentVersion,channel,installKind,behaviorMode,isInstalled,canCheck,canDownload,canApply,updateAvailable,latestVersion"),
        ("/api/connections/updates", "empty-array"),
        ("/api/connections/checks", "empty-array"),
        ("/api/download-clients/path-mappings", "empty-array"),
        ("/api/indexer-proxies", "empty-array"),
        ("/api/intake-sources", "empty-array"),
        ("/api/download-clients/orphans", "empty-array"),
        ("/api/download-clients/throughput", "object:hours,samples"),
        ("/api/download-health", "object:items,nextPageToken,hasMore"),
        ("/api/download-health/content-blocklist", "object:enabled,mode,useBuiltInPatterns,customPatterns,remoteUrl,remotePatterns,lastRefreshedUtc,updatedUtc,syncToQbittorrentEnabled"),
        ("/api/download-health/content-blocklist/qbittorrent-sync-status", "object:"),
        ("/api/download-health/ignore-list", "empty-array"),
        ("/api/download-health/rules", "array:id,kind,enabled,strikeLimit,timerMinutes,appliesTo,minCompletionPercent,maxCompletionPercent,resetStrikesOnProgress,actionPreset"),
        ("/api/integrations/processors/download-destinations", "object:libraries"),
        ("/api/intelligent-routing/anomalies", "object:count,anomalies"),
        ("/api/intelligent-routing/snapshot", "object:computedUtc,preferences,indexerSuccessRates,downloadClientSuccessRates,downloadClientRecords"),
        ("/api/metadata/broker/search", "object:provider,mode,resultCount,results,failure"),
        ("/api/metadata/broker/status", "object:provider,isConfigured,mode,message,failure"),
        ("/api/metadata/status", "object:provider,isConfigured,mode,message,sources,lastFailure"),
        ("/api/ranking-model/status", "object:enabled,autoDispatchImpactEnabled,maxAbsoluteBoost,mode,notes,modelLoaded,activeModelVersion,lastTrainedUtc,trainingSampleCount,lastAuc"),
        ("/api/seerr-servers", "empty-array"),
        ("/api/trakt/status", "object:clientConfigured,connected,expiresUtc,needsReconnect"),
        ("/api/activity/display-settings", "object:pageSize"),
        ("/api/decisions", "object:items,nextPageToken,hasMore"),
        ("/api/download-dispatches", "object:items,nextPageToken,hasMore"),
        ("/api/library-automation", "object:items,nextPageToken,hasMore"),
        ("/api/search-cycles", "object:items,nextPageToken,hasMore"),
        ("/api/search-retry-windows", "object:items,nextPageToken,hasMore"),
        ("/api/stats", "object:sinceUtc,generatedUtc,windowHours,searchesRun,downloadsStarted,importsCompleted,cleanupRemovals,retentionRemovals"),
        ("/api/system/tasks", "array:key,name,description,intervalSeconds,isConfigurable,lastStartedUtc,lastCompletedUtc,lastResult,lastError,lastDurationMs"),
        ("/api/library-views", "empty-array"),
        ("/api/subtitle-languages", "array:code,name"),
        ("/api/movie-collections", "empty-array"),
        ("/api/movies/calendar", "empty-array"),
        ("/api/movies/controls", "object:kind,filterFields,sortFields,posterOptions"),
        ("/api/movies/genres", "empty-array"),
        ("/api/movies/page", "object:items,nextPageToken,hasMore,totalCount,facets"),
        ("/api/movies/posters", "object:"),
        ("/api/movies/titles", "object:"),
        ("/api/movies/wanted", "object:totalWanted,missingCount,upgradeCount,coveredCount,upcomingCount,recentItems"),
        ("/api/notification-preferences", "object:importCompletedEnabled,importFailedEnabled,providerHealthChangedEnabled,upgradeEnabled,renameEnabled,fileDeletedEnabled,manualInteractionRequiredEnabled,grabEnabled,addedEnabled,updateEnabled"),
        ("/api/notification-webhooks/deliveries", "empty-array"),
        ("/api/auto-tag-rules", "empty-array"),
        ("/api/integrations/external/activity", "empty-array"),
        ("/api/integrations/external/health", "object:instanceName,status,libraryCount,enabledIndexerCount,enabledDownloadClientCount,activeJobCount,problemCount,checkedUtc"),
        ("/api/integrations/external/queue", "object:jobs,dispatches"),
        ("/api/integrations/outbound-throttle", "object:hosts"),
        ("/api/migration/reports", "empty-array"),
        ("/api/recycle-bin", "empty-array"),
        ("/api/recycle-bin/cleanup/preview", "object:items,expiredCount,overCapacityCount,bytesFreed"),
        ("/api/setup/draft", "object:mode,mediaIntent,movieRootPath,seriesRootPath,downloadsPath,movieDownloadsPath,seriesDownloadsPath,qualityPreset,formatGoal,indexerName"),
        ("/api/setup/progress", "object:lastCompletedStep,isSkipped,isCompleted,updatedUtc"),
        ("/api/sharing-rules", "empty-array"),
        ("/api/v1/guides/trash/rule-migration", "object:packageVersion,items,previewHash"),
        ("/api/media-plan-scenarios", "array:id,name,description,mediaTypes,requirements,version,variants"),
        ("/api/v1/guides/trash/inventory", "object:packageId,packageVersion,sourceRevision,packageIntegritySha256,totalItemCount,typedItemCount,advancedItemCount,items,unaccounted,inventoryHash"),
        ("/api/v1/guides/trash/update-check", "object:isEnabled,lastCheckedUtc,lastSeenRevision,status,error,report,updatedUtc"),
        ("/api/v1/guides/trash/versions", "array:package,isActive,storedUtc,integritySha256"),
        ("/api/v1/release-preferences/plans", "empty-array"),
        ("/api/retention/collections", "empty-array"),
        ("/api/retention/fields", "array:id,label,hint,group,valueKind,operators,options,source,unsupportedOn,perPerson"),
        ("/api/retention/leaving-soon", "empty-array"),
        ("/api/retention/seerr-cleanups", "empty-array"),
        ("/api/series/calendar", "empty-array"),
        ("/api/series/controls", "object:kind,filterFields,sortFields,posterOptions"),
        ("/api/series/genres", "empty-array"),
        ("/api/series/page", "object:items,nextPageToken,hasMore,totalCount,facets"),
        ("/api/series/posters", "object:"),
        ("/api/series/search-history", "empty-array"),
        ("/api/series/titles", "object:series,episodes"),
        ("/api/series/upcoming", "empty-array"),
        ("/api/series/wanted", "object:totalWanted,missingCount,upgradeCount,coveredCount,upcomingCount,recentItems"),
        ("/api/attention/held-releases", "empty-array"),
        ("/api/attention/no-release", "empty-array"),
        ("/api/automation/summary", "object:generatedUtc,readiness,queue,imports,attention"),
        ("/api/command", "empty-array"),
    ];

    [Fact]
    public async Task Every_read_route_answers_a_fresh_install_with_the_body_its_screen_reads()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        foreach (var (path, shape) in Routes) await AssertRouteAsync(app, path, shape);
    }

    private static async Task AssertRouteAsync(ApplicationTestHost app, string path, string shape)
    {

        var response = await app.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var separator = shape.IndexOf(':');
        var kind = separator < 0 ? shape : shape[..separator];
        var names = separator < 0 ? [] : shape[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        switch (kind)
        {
            case "empty-array":
                Assert.Equal(JsonValueKind.Array, json.ValueKind);
                Assert.Equal(0, json.GetArrayLength());
                break;
            case "array":
                Assert.Equal(JsonValueKind.Array, json.ValueKind);
                Assert.True(json.GetArrayLength() > 0, $"{path} answered an empty list; a fresh install holds some");
                foreach (var name in names) Assert.True(json[0].TryGetProperty(name, out _), $"{path}: the first item has no '{name}'");
                break;
            default:
                Assert.Equal(JsonValueKind.Object, json.ValueKind);
                foreach (var name in names) Assert.True(json.TryGetProperty(name, out _), $"{path}: the answer has no '{name}'");
                break;
        }
    }

    [Fact]
    public async Task A_fresh_install_reads_as_nothing_done_nothing_connected_and_the_defaults_a_person_has_not_changed()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        async Task<JsonElement> Read(string path) => JsonDocument.Parse(await app.Client.GetStringAsync(path)).RootElement;

        Assert.Equal("Deluno", (await Read("/api")).GetProperty("name").GetString());
        Assert.Equal(500, (await Read("/api/activity/display-settings")).GetProperty("pageSize").GetInt32());
        var progress = await Read("/api/setup/progress");
        Assert.False(progress.GetProperty("isCompleted").GetBoolean());
        Assert.False(progress.GetProperty("isSkipped").GetBoolean());
        Assert.Equal(0, progress.GetProperty("lastCompletedStep").GetInt32());
        var trakt = await Read("/api/trakt/status");
        Assert.False(trakt.GetProperty("clientConfigured").GetBoolean());
        Assert.False(trakt.GetProperty("connected").GetBoolean());
        Assert.False((await Read("/api/ranking-model/status")).GetProperty("enabled").GetBoolean());
        Assert.False((await Read("/api/download-health/content-blocklist")).GetProperty("enabled").GetBoolean());
        Assert.False((await Read("/api/backups/settings")).GetProperty("enabled").GetBoolean());
        Assert.True((await Read("/api/notification-preferences")).GetProperty("importCompletedEnabled").GetBoolean());
        Assert.Equal(0, (await Read("/api/integrations/outbound-throttle")).GetProperty("hosts").GetArrayLength());
        Assert.Equal(0, (await Read("/api/movies/wanted")).GetProperty("totalWanted").GetInt32());
        Assert.Equal(0, (await Read("/api/movies/page")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Read("/api/series/page")).GetProperty("totalCount").GetInt32());
        var languages = await Read("/api/subtitle-languages");
        Assert.Equal("en", languages[0].GetProperty("code").GetString());
        Assert.Equal("English", languages[0].GetProperty("name").GetString());
        Assert.Equal(0, (await Read("/api/integrations/external/queue")).GetProperty("jobs").GetArrayLength());
    }
}
