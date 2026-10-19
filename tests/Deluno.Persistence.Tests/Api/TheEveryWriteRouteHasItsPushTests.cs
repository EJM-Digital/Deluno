using Deluno.Api;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1358: every write route of the real host is walked against its ConfigChanged area, so a route that
/// changes something a screen shows cannot go without a push. A route outside the areas must be listed
/// here with the reason it needs none (it publishes its own event, or nothing on a screen shows it).
/// </summary>
public sealed class TheEveryWriteRouteHasItsPushTests
{
    [Fact]
    public async Task Every_write_route_either_names_a_ConfigChanged_area_or_is_listed_with_its_own_push()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var writes = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
            .Select(endpoint => (Pattern: endpoint.RoutePattern.RawText!, Methods: endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(route => route.Methods.Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE"))
            .Select(route => route.Pattern)
            .Distinct()
            .Where(pattern => ConfigChangedPush.AreaFor(new Microsoft.AspNetCore.Http.PathString(pattern)) is null
                && !ConfigChangedPush.HasOwnEvent(new Microsoft.AspNetCore.Http.PathString(pattern)))
            .Order()
            .ToArray();

        var unlisted = writes.Where(route => !ReviewedRoutes.Contains(route)).ToArray();
        Assert.True(unlisted.Length == 0,
            "These write routes name no ConfigChanged area and publish no event of their own that is on this list: " + string.Join(", ", unlisted)
            + ". Map them in ConfigChangedPush (or its own-event table), or add them to ReviewedRoutes with the push that serves them.");

        var stale = ReviewedRoutes.Where(route => !writes.Contains(route)).ToArray();
        Assert.True(stale.Length == 0, "These reviewed routes are gone or now have an area; remove them from the list: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("/api/blocked-releases")]
    [InlineData("/api/blocked-releases/abc/refuse")]
    [InlineData("/api/blocked-releases/cleanup-all")]
    public void Every_blocklist_write_names_the_download_health_area_the_Blocklist_tab_follows(string path)
        => Assert.Equal(ConfigAreas.DownloadHealth, ConfigChangedPush.AreaFor(new Microsoft.AspNetCore.Http.PathString(path)));

    [Theory]
    [InlineData("/api/backups/settings")]
    [InlineData("/api/updates/preferences")]
    [InlineData("/api/recycle-bin/settings")]
    [InlineData("/api/movie-collections/bulk")]
    public void A_settings_write_on_a_screen_that_follows_its_own_event_publishes_that_event(string path)
        => Assert.True(ConfigChangedPush.HasOwnEvent(new Microsoft.AspNetCore.Http.PathString(path)));

    private static readonly HashSet<string> ReviewedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        // the activity feed publishes ActivityEventAdded; clear and display settings are read on the next open
        "/api/activity/clear",
        "/api/activity/display-settings",
        // AttentionChangedPush publishes the Attention event after the write
        "/api/attention/held-releases/{historyId}/approve",
        "/api/attention/held-releases/{historyId}/dismiss",
        "/api/attention/no-release/{mediaType}/{id}/dismiss",
        // sign in and out; the session is the screen
        "/api/auth/bootstrap",
        "/api/auth/login",
        "/api/auth/logout",
        "/api/auth/password",
        // publishes the AutomationState event
        "/api/automation/catalogue/bulk",
        "/api/automation/series/{seriesId}/episodes/bulk",
        // the backup service publishes BackupChanged
        "/api/backups/",
        "/api/backups/{id}",
        "/api/backups/restore",
        "/api/backups/restore/preview",
        // runs a command the job store publishes
        "/api/command/",
        // publishes CompanionJobChanged
        "/api/companion-tools/install",
        "/api/companion-tools/tray-install",
        // only tests or updates a connection and publishes ConnectionChecked
        "/api/connections/{kind}/{id}/test",
        "/api/connections/{kind}/{id}/update",
        "/api/connections/{kind}/{id}/version/dismiss",
        "/api/connections/test-all",
        // adding a title publishes the Movie or Series event
        "/api/discover/add",
        // publishes the DownloadClient or queue events itself, or only tests a connection
        "/api/download-clients/",
        "/api/download-clients/{clientId}/categories/check",
        "/api/download-clients/{clientId}/history/{itemId}/dismiss",
        "/api/download-clients/{clientId}/queue/{queueItemId}/health/{kind}/ignore",
        "/api/download-clients/{clientId}/queue/{queueItemId}/remove",
        "/api/download-clients/{clientId}/queue/actions",
        "/api/download-clients/{clientId}/resume",
        "/api/download-clients/{clientId}/warnings/clear",
        "/api/download-clients/{clientId}/webhook",
        "/api/download-clients/{id}",
        "/api/download-clients/{id}/path-mappings",
        "/api/download-clients/{id}/path-mappings/{mappingId}",
        "/api/download-clients/{id}/path-mappings/{mappingId}/test",
        "/api/download-clients/{id}/test",
        "/api/download-clients/leftovers/cleanup",
        "/api/download-clients/orphans/keep",
        "/api/download-clients/orphans/remove",
        "/api/download-clients/test",
        "/api/download-clients/usenet-provider",
        // publishes the DownloadDispatch events
        "/api/download-dispatches/{id}/dismiss",
        // files and imports publish the Movie, Series and ImportState events, or the call only reads
        "/api/filesystem/file-check",
        "/api/filesystem/import/execute-batch",
        "/api/filesystem/import/jobs",
        "/api/filesystem/import/preview",
        "/api/filesystem/import/scan-folder",
        "/api/filesystem/import/season-pack/leftovers/delete",
        "/api/filesystem/libraries/{libraryId}/files-gone",
        "/api/filesystem/native-folder-picker",
        "/api/filesystem/path-diagnostics",
        "/api/filesystem/reconciliation/repair",
        "/api/filesystem/rescan-title",
        // clears history the Indexers screen reads again on IndexerQueryRecorded
        "/api/indexer-history/clear",
        // publishes the IndexerProxy event itself
        "/api/indexer-proxies/",
        "/api/indexer-proxies/{id}",
        // publishes the Indexer event itself, or only tests a connection
        "/api/indexers/",
        "/api/indexers/{id}",
        "/api/indexers/{id}/reset-circuit",
        "/api/indexers/{id}/test",
        "/api/indexers/test",
        // publishes the IntakeSource event itself
        "/api/intake-sources/",
        "/api/intake-sources/{id}",
        "/api/intake-sources/{id}/approve-preview",
        "/api/intake-sources/{id}/exclude-preview",
        "/api/intake-sources/{id}/exclusions/{exclusionId}",
        "/api/intake-sources/{id}/preview",
        "/api/intake-sources/{id}/sync",
        // processor hand-offs publish ProcessorHandoffChanged, or the call only reads
        "/api/integrations/external/file-changed",
        "/api/integrations/external/import-preview",
        "/api/integrations/external/redownload",
        "/api/integrations/external/trigger-refresh",
        // only recommends, changes nothing
        "/api/intelligent-routing/recommend-release",
        // the job store publishes JobChanged
        "/api/jobs/{id}/dismiss",
        "/api/jobs/{id}/retry",
        "/api/jobs/retry-failed",
        // publishes the Library and AutomationState events itself
        "/api/libraries/",
        "/api/libraries/{id}",
        "/api/libraries/{id}/automation",
        "/api/libraries/{id}/import-existing",
        "/api/libraries/{id}/import-existing/pause",
        "/api/libraries/{id}/import-existing/resume",
        "/api/libraries/{id}/import-existing/selected",
        "/api/libraries/{id}/media-plan",
        "/api/libraries/{id}/quality-profile",
        "/api/libraries/{id}/routing",
        "/api/libraries/{id}/search-now",
        "/api/libraries/{id}/skip-cycle",
        "/api/libraries/{id}/subtitles",
        "/api/libraries/{id}/workflow",
        // publishes the Library event itself
        "/api/library-views/",
        "/api/library-views/{id}",
        // only tests the metadata provider
        "/api/metadata/test",
        // a one-off import that publishes the Library and Movie events as it runs
        "/api/migration/apply",
        "/api/migration/preview",
        // publishes the Movie event itself (entity push) or only previews/searches
        "/api/movies/",
        "/api/movies/{id}",
        "/api/movies/{id}/automation/defer",
        "/api/movies/{id}/automation/skip-once",
        "/api/movies/{id}/files",
        "/api/movies/{id}/files/language",
        "/api/movies/{id}/files/quality",
        "/api/movies/{id}/force-redownload",
        "/api/movies/{id}/grab",
        "/api/movies/{id}/libraries/",
        "/api/movies/{id}/libraries/{libraryId}",
        "/api/movies/{id}/libraries/{libraryId}/want-again",
        "/api/movies/{id}/metadata/issue/acknowledge",
        "/api/movies/{id}/metadata/link",
        "/api/movies/{id}/metadata/link/preview",
        "/api/movies/{id}/metadata/override",
        "/api/movies/{id}/metadata/refresh",
        "/api/movies/{id}/replacement-protection",
        "/api/movies/{id}/search",
        "/api/movies/{id}/subtitles/{language}/blacklist",
        "/api/movies/{id}/subtitles/{language}/candidates/pick",
        "/api/movies/{id}/subtitles/{language}/reject",
        "/api/movies/{id}/subtitles/{language}/resync",
        "/api/movies/{id}/subtitles/{language}/upload",
        "/api/movies/{id}/subtitles/override",
        "/api/movies/bulk",
        "/api/movies/bulk/minimum-availability",
        "/api/movies/bulk/quality-profile",
        "/api/movies/bulk/reassign-library",
        "/api/movies/bulk/rename-apply",
        "/api/movies/bulk/rename-preview",
        "/api/movies/bulk/search",
        "/api/movies/bulk/tags",
        "/api/movies/import-recovery",
        "/api/movies/import-recovery/{id}",
        "/api/movies/import-recovery/{id}/dismiss",
        "/api/movies/import-recovery/{id}/import-original",
        "/api/movies/import-recovery/{id}/retry",
        "/api/movies/import-recovery/{id}/search-again",
        "/api/movies/import-recovery/{id}/stop-wanting",
        "/api/movies/metadata/jobs",
        "/api/movies/monitoring",
        // publishes the PolicySet event itself, or only previews
        "/api/policy-sets/",
        "/api/policy-sets/{id}",
        "/api/policy-sets/{id}/preview",
        "/api/policy-sets/{id}/rollback",
        // publishes the QualityProfile event itself
        "/api/quality-profiles/",
        "/api/quality-profiles/{id}",
        // the queue publishes QueueItem events after the write
        "/api/queue/{clientId}/{queueItemId}",
        "/api/queue/bulk",
        // training and rollback run in the background and publish when done
        "/api/ranking-model/rollback",
        "/api/ranking-model/train",
        // the recycle bin publishes RecycleBinChanged itself
        "/api/recycle-bin/{id}",
        "/api/recycle-bin/{id}/restore",
        "/api/recycle-bin/cleanup",
        // a manual search or grab; the grab publishes the dispatch events
        "/api/search",
        "/api/search/grab",
        // publishes the Series event itself (entity push) or only previews/searches
        "/api/series/",
        "/api/series/{id}",
        "/api/series/{id}/automation/defer",
        "/api/series/{id}/automation/skip-once",
        "/api/series/{id}/episodes/search",
        "/api/series/{id}/force-redownload",
        "/api/series/{id}/grab",
        "/api/series/{id}/libraries/",
        "/api/series/{id}/libraries/{libraryId}",
        "/api/series/{id}/metadata/issue/acknowledge",
        "/api/series/{id}/metadata/link",
        "/api/series/{id}/metadata/link/preview",
        "/api/series/{id}/metadata/override",
        "/api/series/{id}/metadata/refresh",
        "/api/series/{id}/monitor-new-episodes",
        "/api/series/{id}/numbering",
        "/api/series/{id}/search",
        "/api/series/{id}/seasons/{seasonNumber:int}/search",
        "/api/series/{id}/subtitles/override",
        "/api/series/bulk",
        "/api/series/bulk/quality-profile",
        "/api/series/bulk/reassign-library",
        "/api/series/bulk/rename-apply",
        "/api/series/bulk/rename-preview",
        "/api/series/bulk/search",
        "/api/series/bulk/tags",
        "/api/series/episodes/{episodeId}/file",
        "/api/series/episodes/{episodeId}/subtitles/{language}/blacklist",
        "/api/series/episodes/{episodeId}/subtitles/{language}/candidates/pick",
        "/api/series/episodes/{episodeId}/subtitles/{language}/reject",
        "/api/series/episodes/{episodeId}/subtitles/{language}/resync",
        "/api/series/episodes/{episodeId}/subtitles/{language}/upload",
        "/api/series/episodes/monitoring",
        "/api/series/import-recovery",
        "/api/series/import-recovery/{id}",
        "/api/series/import-recovery/{id}/dismiss",
        "/api/series/import-recovery/{id}/import-original",
        "/api/series/import-recovery/{id}/retry",
        "/api/series/import-recovery/{id}/search-again",
        "/api/series/import-recovery/{id}/stop-wanting",
        "/api/series/metadata/jobs",
        "/api/series/monitoring",
        "/api/series/season-pass/monitoring",
        // publishes the Settings event itself
        "/api/settings/",
        "/api/settings/automation",
        // the setup wizard owns its own state and reloads on finish
        "/api/setup/completed",
        "/api/setup/draft",
        "/api/setup/progress",
        // edited only in the client drawer, which holds the list it shows
        "/api/sharing-rules/",
        "/api/sharing-rules/{id}",
        // publishes its own Health, Monitoring, LanAccess or BundledComponent event, or sets a log level no screen shows
        "/api/system/bundled-components/{kind}/connect",
        "/api/system/bundled-components/connect-all",
        "/api/system/component-recovery/{kind}/dismiss",
        "/api/system/component-recovery/{kind}/readd",
        "/api/system/health/{id}/snooze",
        "/api/system/health/{id}/unsnooze",
        "/api/system/health/background-problems/{key}",
        "/api/system/health/background-problems/{key}/retry",
        "/api/system/health/notices/{id}",
        "/api/system/health/run",
        "/api/system/lan-access/allow",
        "/api/system/log-files/{name}",
        "/api/system/log-level",
        // only checks for updates or tests a helper, changes nothing a screen lists
        "/api/trackers/cloudflare-helper/test",
        "/api/trackers/definitions/update-check",
        "/api/trackers/definitions/update-check/run",
        // the update orchestrator publishes UpdateStatusChanged
        "/api/updates/check",
        "/api/updates/download",
        "/api/updates/restart-now",
        // dispatch dismissal publishes the DownloadDispatch event; guide previews change nothing
        "/api/v1/download-dispatches/{dispatchId}",
    };
}
