namespace Deluno.Contracts;

/// <summary>
/// What each activity event kind says in plain words -- the one place this is
/// written (#1086). The Activity API, the live activity feed, the Dashboard
/// summary and every screen that lists an event read the label from here, so
/// a category such as <c>download.vanished.recovered</c> never reaches a
/// person as a code. A test fails if any writer in <c>src/</c> can emit a
/// category with no label here.
/// </summary>
public static class ActivityEventLabels
{
    private static readonly Dictionary<string, string> Labels = Build();

    /// <summary>Every category that has a label of its own.</summary>
    public static IReadOnlyCollection<string> KnownCategories => Labels.Keys;

    public static bool IsKnown(string? category) =>
        !string.IsNullOrWhiteSpace(category) && Labels.ContainsKey(category.Trim());

    /// <summary>
    /// The plain-words name of an event kind. A kind nobody has labelled yet
    /// still says something generic in words (its area, never its code).
    /// </summary>
    public static string For(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Activity";
        }

        var key = category.Trim();
        if (Labels.TryGetValue(key, out var label))
        {
            return label;
        }

        var dot = key.IndexOfAny(['.', '_', '-']);
        var area = (dot > 0 ? key[..dot] : key).ToLowerInvariant();
        return Areas.TryGetValue(area, out var areaName) ? $"{areaName} activity" : "Activity";
    }

    private static readonly Dictionary<string, string> Areas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["download"] = "Download", ["downloads"] = "Download", ["filesystem"] = "Import", ["library"] = "Library",
        ["metadata"] = "Metadata", ["processing"] = "Processing", ["processor"] = "Processing", ["job"] = "Background task",
        ["retention"] = "Retention", ["intake"] = "List", ["tags"] = "Tag", ["trackers"] = "Indexer",
        ["subtitle"] = "Subtitle", ["movie"] = "Film", ["series"] = "Show", ["release"] = "Release",
        ["health"] = "Health", ["connectivity"] = "Connection", ["quality"] = "Quality", ["seerr"] = "Request"
    };

    private static Dictionary<string, string> Build()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Background tasks
            ["job.queued"] = "Task queued",
            ["job.started"] = "Task started",
            ["job.completed"] = "Task finished",
            ["job.failed"] = "Task failed",
            ["job.dead-letter"] = "Task gave up after repeated failures",
            ["job.lease-expired"] = "Task stopped responding",
            ["job.lease-released"] = "Task handed back to the queue",
            ["job.retry"] = "Task will try again",
            ["job.revived"] = "Task brought back to run again",
            ["job.cancelled"] = "Task cancelled",
            ["job.dismissed"] = "Task dismissed",
            ["job.duplicate"] = "Task skipped, already queued",
            ["activity.cleared"] = "Activity history cleared",
            ["decision.explained"] = "Why Deluno chose a release",

            // Searching
            ["library.search.cycle"] = "Search round started",
            ["library.search.executed"] = "Search finished",
            ["library.search.queuePause"] = "Search paused while downloads catch up",
            ["library.search.requested"] = "Search requested",
            ["library.search.skipped"] = "Search skipped",
            ["episode.search.executed"] = "Episode search finished",
            ["episode.search.result"] = "Episode search result",
            ["episode.search.asked-again"] = "Search asked again",
            ["library.search.asked-again"] = "Search asked again",
            ["series.episode-file-link.repaired"] = "Cleared a file linked to the wrong episode",
            ["series.episode-file.mislabelled"] = "Replaced a file that was another episode",
            ["series.episode-file.linked"] = "Linked an episode to its own file",
            ["series.search.episode"] = "Searched for an episode",
            ["series.search.season"] = "Searched for a season",
            ["movie.search.manual"] = "Searched for a film by hand",
            ["series.search.manual"] = "Searched for a show by hand",
            ["rss.sync"] = "Checked indexer feeds for new releases",
            ["acquisition.override"] = "Search rules overridden by hand",

            // Grabbing and sending to a client
            ["movie.release.grabbed"] = "Grabbed a release for a film",
            ["series.release.grabbed"] = "Grabbed a release for a show",
            ["movie.release.force-grabbed"] = "Grabbed a release for a film, overriding the rules",
            ["series.release.force-grabbed"] = "Grabbed a release for a show, overriding the rules",
            ["download.dispatch.recorded"] = "Sent to the download client",
            ["download.dispatch.planned"] = "Chosen, not sent yet",
            ["download.dispatch.paused"] = "Held back, client is paused",
            ["download.dispatch.failed"] = "Could not send to the download client",
            ["download.grab.duplicate"] = "Skipped, already downloading",
            ["download.grab.unreachable"] = "Download client could not be reached",
            ["download.grab.foundElsewhere"] = "Found the release already in the download client",
            ["download.grab.rejected"] = "Download client turned the release down",
            ["download.grab.timeout"] = "Never appeared in the download client",
            ["download.release.dead-link"] = "Release link was dead and has been blocked",
            ["release.blocked"] = "Release blocked",
            ["release.block.proposed"] = "Release suggested for blocking",
            ["release.block-lifted"] = "Release unblocked",
            ["connection.updated"] = "App update",

            // Download clients
            ["download.client.connectivity-retry-scheduled"] = "Will retry the download client shortly",
            ["download.client.connectivity-retry-succeeded"] = "Download client came back and the release was sent",
            ["download.client.connectivity-retry-exhausted"] = "Gave up waiting for the download client",
            ["download.client.duplicate"] = "Download client reported a duplicate",
            ["download.client.failed"] = "Download client reported a failed download",
            ["download.duplicate.failed"] = "An extra copy of a download failed, the release was kept",
            ["download.client.needs-attention"] = "Download needs attention in the client",
            ["download.client.setup-problem"] = "Download client has a setup problem",
            ["download.client.script-failed"] = "Download client script failed",
            ["download.client.script-warning"] = "Download client script gave a warning",
            ["download.health.connectivity-check-skipped"] = "Download health check skipped while offline",
            ["download.health.remediated"] = "Fixed a stuck download",
            ["download.health.remediation-previewed"] = "Showed how a stuck download would be fixed",
            ["download.health.strikes-purged"] = "Cleared download health strikes",
            ["download.queue.removed"] = "Removed from the download queue",
            ["download.client.resumed"] = "Resumed a paused download client",
            ["download.client.warnings-cleared"] = "Cleared a download client's own warnings",
            ["download.vanished"] = "Download disappeared from the client",
            ["download.vanished.recovered"] = "Found its files after it left the client",
            ["download.detection.timeout"] = "Finished downloading but was never imported",
            ["download.detection.waiting-for-processor"] = "Finished downloading, waiting for the processor",
            ["download.import.timeout"] = "Import started but never finished",
            ["download.import.failed"] = "Import of a download failed",
            ["download.integrity.failed"] = "Download failed its file check",
            ["download.integrity.passed"] = "Download passed its file check",
            ["download.cleanup.cleared"] = "Cleaned up a refused download",
            ["download.cleanup.left-alone"] = "Left a refused download alone",
            ["download.orphan.kept"] = "Kept a download Deluno did not start",
            ["download.orphan.removed"] = "Removed a download Deluno did not start",
            ["download.sharing.reclaim-previewed"] = "Showed which finished downloads could be cleared",
            ["download.sharing.reclaimed"] = "Cleared finished downloads that were no longer sharing",
            ["download.sharing.stopped"] = "Stopped sharing a finished download",
            ["downloads.folder-changed"] = "A download client's folder changed",

            // Processing
            ["processing"] = "Processing update",
            ["processing.waiting"] = "Waiting for the processor",
            ["processing.submitted"] = "Sent to the processor",
            ["processing.accepted"] = "Processor accepted it",
            ["processing.started"] = "Processor started work",
            ["processing.completed"] = "Processor finished",
            ["processing.completed.import-queued"] = "Processor finished, import queued",
            ["processing.failed"] = "Processing failed",
            ["processing.rejected"] = "Processor turned it down",
            ["processing.rejected.not-acted"] = "Processor turned it down, nothing changed",
            ["processing.timed-out"] = "Processor went quiet for too long",
            ["processing.stalled"] = "Processor stopped making progress",
            ["processing.lost"] = "Processor never received it",
            ["processing.cancelled"] = "Processing cancelled",
            ["processing.released"] = "Importing the original, unprocessed",
            ["processing.dismissed"] = "Processing problem dismissed",
            ["processing.submission-failed"] = "Could not send to the processor",
            ["processing.no-progress"] = "Processor reported no progress",
            ["processing.status-forbidden"] = "Processor refused to report progress",
            ["processing.processor-unreachable"] = "Processor could not be reached",
            ["processing.kept-waiting"] = "Kept waiting for the processor",
            ["processing.retry-requested"] = "Asked the processor to try again",
            ["processing.search-again"] = "Searching for another copy",
            ["processing.output.deleted"] = "Deleted the processor's leftover output",
            ["processing.output.already-imported"] = "Deleted a second copy the processor sent",
            ["processing.handoff.closed-imported"] = "Closed a hand-off for a file already imported",
            ["processing.waiting-too-long"] = "The processor never picked a file up",
            ["processing.resubmitted"] = "Sent to the processor again",
            ["processing.source-gone"] = "Download gone before the processor could clean it",
            ["processing.source-gone.settled"] = "Closed a hand-off whose download was gone",
            ["processing.superseded"] = "Hand-off closed, title imported another way",
            ["processing.superseded-settled"] = "Closed an earlier send of a release sent again",
            ["processing.output.matched.import-queued"] = "Found the processed file, import queued",
            ["processing.output.scan-failed"] = "Could not look through the processor's output",
            ["processing.output.unmatched"] = "Processor output did not match anything",
            ["processing.output.misplaced"] = "Processor output is in the wrong place",
            ["processing.outputs-missing"] = "Some processed files were missing",
            ["processing.partial-import"] = "Only some processed files were imported",
            ["processing.extras-left-out"] = "A small extra file was left out",
            ["processing.outcome-reported"] = "Told the processor what happened",
            ["recovery.case-settled"] = "Closed a problem the library had already fixed",
            ["download.import-failure-settled"] = "Closed an import failure the library had already fixed",
            ["processing.outcome-kept"] = "The processor kept its earlier answer",
            ["processing.outcome-delayed"] = "Could not tell the processor yet, will keep trying",
            ["processing.outcome-not-reported"] = "Could not tell the processor what happened",
            ["processor.connection.tested"] = "Processor connection tested",
            ["connection.checked"] = "Connection check",
            ["processor.health-address.added"] = "Added a health address for the processor",

            // Importing
            ["filesystem.import.started"] = "Import started",
            ["filesystem.import.completed"] = "Imported into the library",
            ["filesystem.import.failed"] = "Import failed",
            ["filesystem.import.forced"] = "Import forced by hand",
            ["filesystem.import.named"] = "Renamed by the naming rules",
            ["filesystem.import.auto-queued"] = "Import queued automatically",
            ["filesystem.import.not-imported"] = "Not imported",
            ["filesystem.import.held-by-file-check"] = "Import held by the file check",
            ["filesystem.import.restart-recovered"] = "Import finished after a restart",
            ["filesystem.import.restart-abandoned"] = "Import stopped by a restart",
            ["filesystem.import.season-pack.completed"] = "Season pack imported",
            ["filesystem.import.season-pack.leftovers-deleted"] = "Deleted a season pack's leftover files",
            ["filesystem.import.unmatched-given-up"] = "Gave up matching a file to a title",
            ["filesystem.reconciliation.repaired"] = "Repaired a library record",
            ["filesystem.reconciliation.unmonitored"] = "Stopped watching a title whose files were gone",
            ["filesystem.reconciliation.repair-failed"] = "Could not repair a library record",
            ["library.import.existing"] = "Imported an existing library",
            ["library.import.existing.selected"] = "Imported chosen titles from an existing library",

            // Library
            ["library.file.missing"] = "A library file is missing",
            ["library.files-gone"] = "Library files disappeared",
            ["library.files-gone-failed"] = "Could not record that library files disappeared",
            ["library.episode.file-deleted"] = "Moved an episode's file to the recycle bin",
            ["library.movie.file-deleted"] = "Moved a film's file to the recycle bin",
            ["library.movie.file-language-edited"] = "Changed a film file's language",
            ["library.movie.file-quality-edited"] = "Changed a film file's quality",
            ["library.movie.move-records-failed"] = "Moved film files but could not update their records",
            ["library.movie.reassigned"] = "Moved a film to another library",
            ["library.movie.rename-records-failed"] = "Renamed film files but could not update their records",
            ["library.movie.renamed"] = "Renamed a film's files",
            ["library.movie.status-repaired"] = "Corrected a film's status",
            ["library.movie.untracked-files-moved"] = "Moved files Deluno was not tracking",
            ["library.movie.wanted-again"] = "Film wanted again",
            ["library.series.rename-records-failed"] = "Renamed show files but could not update their records",
            ["library.series.renamed"] = "Renamed a show's files",
            ["library.quality.recalculated"] = "Recalculated library quality",
            ["library.title.rescanned"] = "Rescanned a title's files",
            ["library.paused"] = "Library paused",
            ["library.resumed"] = "Library resumed",
            ["library.reachable"] = "Library folder is reachable again",
            ["library.unreachable"] = "Library folder cannot be reached",
            ["collection.edited"] = "Collection edited",
            ["collection.removed"] = "Collection removed",
            ["collection.tagged"] = "Collection tagged",
            ["movie.removed"] = "Film removed",
            ["series.removed"] = "Show removed",
            ["movie.file-matched"] = "Matched a film's file already in the library",
            ["series.file-matched"] = "Matched a show's files already in the library",
            ["movie.download-cancelled"] = "Cancelled a film's download",
            ["series.download-cancelled"] = "Cancelled a show's download",
            ["series.season-pack-kept-running"] = "Kept a season pack downloading for other episodes",
            ["external.file-changed"] = "A file was changed outside Deluno",
            ["external.redownload"] = "A file replaced outside Deluno will be downloaded again",

            // Metadata, tags and quality
            ["metadata.artwork.cleanup"] = "Cleaned up artwork",
            ["metadata.movie.linked"] = "Linked a film to its details",
            ["metadata.movie.overridden"] = "Film details changed by hand",
            ["metadata.movie.provider-record-missing"] = "Film details no longer found",
            ["metadata.movie.provider-record-missing.acknowledged"] = "Missing film details acknowledged",
            ["metadata.movie.refreshed"] = "Film details refreshed",
            ["metadata.series.catalogue"] = "Show episode order chosen",
            ["metadata.series.catalogue.held"] = "Show episode order held for a decision",
            ["metadata.series.catalogue.unreadable"] = "Show episode list could not be read",
            ["metadata.series.linked"] = "Linked a show to its details",
            ["metadata.series.overridden"] = "Show details changed by hand",
            ["metadata.series.provider-record-missing"] = "Show details no longer found",
            ["metadata.series.provider-record-missing.acknowledged"] = "Missing show details acknowledged",
            ["metadata.series.refreshed"] = "Show details refreshed",
            ["subtitle.provider.paused"] = "Subtitle source paused",
            ["subtitle.rejected"] = "Subtitle rejected",
            ["subtitle.fetched"] = "Subtitle fetched",
            ["quality.model.reset"] = "Release ranking reset",
            ["quality.custom-format.guide-moved"] = "Quality rules moved to the new guide",
            ["tags.auto"] = "Tags applied automatically",
            ["tags.bulk.connections"] = "Tags changed on several connections",
            ["tags.deleted"] = "Tag deleted",
            ["tags.renamed"] = "Tag renamed",
            ["tags.unused.deleted"] = "Unused tags deleted",

            // Lists and requests
            ["intake.entry.excluded"] = "List entry excluded",
            ["intake.off-list-tagged"] = "Titles no longer on a list were tagged",
            ["intake.preview.approved"] = "List preview approved",
            ["intake.sync.completed"] = "List synced",
            ["intake.sync.failed"] = "List sync failed",
            ["intake.sync.skipped"] = "List sync skipped",
            ["intake.tagged"] = "List titles tagged",
            ["seerr.acquired"] = "Request picked up",
            ["seerr.acquire-failed"] = "Could not act on a request",

            // Retention
            ["retention.action"] = "Retention rule acted on a download",
            ["retention.added-by-hand"] = "Download added to retention by hand",
            ["retention.check"] = "Retention check ran",
            ["retention.deferred"] = "Retention put off for a download",
            ["retention.kept"] = "Kept a download past its retention",
            ["retention.postponed"] = "Retention postponed",
            ["retention.removed"] = "Removed a download under retention",
            ["retention.restarted"] = "Restarted a download's retention",
            ["retention.undone"] = "Undid a retention removal",

            // System
            ["app.update"] = "Deluno updated",
            ["system"] = "System update",
            ["integration"] = "An outside app asked for a refresh",
            ["security.local-network-auth"] = "Local network sign-in setting changed",
            ["installer.components.connected"] = "Installed apps connected",
            ["connectivity.outage.started"] = "Internet connection lost",
            ["connectivity.outage.resolved"] = "Internet connection back",
            ["health.problem"] = "Health problem found",
            ["health.resolved"] = "Health problem cleared",
            ["media_server.refresh.caught_up"] = "Media server told about missed changes",
            ["media_server.refreshed"] = "Media server told about new files",
            ["recycle.restored"] = "Restored from the recycle bin",
            ["recycle.deleted"] = "Deleted for good from the recycle bin",
            ["release.turned-down"] = "Release turned down",
            ["trackers.definitions.rejected"] = "Indexer definitions update turned down",
            ["trackers.definitions.switch"] = "Switched to another indexer definition",
            ["trackers.definitions.update"] = "Indexer definitions updated"
        };
    }
}
