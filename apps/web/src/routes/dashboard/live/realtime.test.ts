import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { DASHBOARD_SUMMARY_KEY } from "../../../lib/realtime-cache";
import { sharedSources } from "../../../lib/shared-sources";
import { liveSources } from "../sources";
import { fullSummary } from "../downloads-fixtures";
import { pulseOf } from "./indexers";
import { applyClientState, applyDriveSpace, applyHandoffChanged, applyIndexerAnswer, applyJourneyStage, applyQueueRemoved, applyQueueStatus, applyRecoveryCase, applySpeedSample, handoffStatusOf } from "./realtime";

const at = "2026-10-01T10:00:00.000Z";

describe("pushed Live events, applied to the lists the board reads", () => {
  it("puts a hand-off in the lane its new state says, in the words the list uses", () => {
    const qc = new QueryClient();
    qc.setQueryData(sharedSources.handoffs.key, [{ id: "h1", queueItemId: "q1", releaseName: "A", status: "waiting", failureMessage: null, processorName: "Cleaner", updatedUtc: "x", createdUtc: "x" }]);
    applyHandoffChanged(qc, { handoffId: "h1", queueItemId: "q1", libraryId: null, releaseName: "A", state: "failed", processorName: "Cleaner", message: "It timed out.", at });
    expect(qc.getQueryData<Array<{ status: string; failureMessage: string }>>(sharedSources.handoffs.key)![0]).toMatchObject({ status: "failed", failureMessage: "It timed out." });
    applyHandoffChanged(qc, { handoffId: "h2", queueItemId: "q2", libraryId: "l", releaseName: "B", state: "working", processorName: null, message: null, at });
    const list = qc.getQueryData<Array<{ id: string; status: string }>>(sharedSources.handoffs.key)!;
    expect(list.map((item) => [item.id, item.status])).toEqual([["h2", "started"], ["h1", "failed"]]);
    expect(handoffStatusOf("passed-through")).toBe("completed");
  });

  it("marks a sent download imported, adds a new grab, and drops an archived one", () => {
    const qc = new QueryClient();
    qc.setQueryData(sharedSources.dispatches.key, [{ id: "d1", releaseName: "A", importCompletedUtc: null }]);
    applyJourneyStage(qc, { dispatchId: "d1", releaseName: "A", mediaType: "movies", entityType: "movie", entityId: "m1", libraryId: "l", stage: "import_succeeded", detail: null, at });
    expect(qc.getQueryData<Array<{ importCompletedUtc: string }>>(sharedSources.dispatches.key)![0].importCompletedUtc).toBe(at);
    applyJourneyStage(qc, { dispatchId: "d2", releaseName: "B", mediaType: "movies", entityType: "movie", entityId: "m2", libraryId: "l", stage: "grab_succeeded", detail: null, at });
    expect(qc.getQueryData<Array<{ id: string }>>(sharedSources.dispatches.key)!.map((item) => item.id)).toEqual(["d2", "d1"]);
    applyJourneyStage(qc, { dispatchId: "d1", releaseName: "A", mediaType: null, entityType: null, entityId: null, libraryId: null, stage: "archived", detail: null, at });
    expect(qc.getQueryData<Array<{ id: string }>>(sharedSources.dispatches.key)!.map((item) => item.id)).toEqual(["d2"]);
  });

  it("gives a pushed arrival the library it landed in, named from the library counts the summary holds", () => {
    const qc = new QueryClient();
    qc.setQueryData(liveSources.liveExtras.key, { arrivals: [], importsToday: 0 });
    qc.setQueryData(DASHBOARD_SUMMARY_KEY, { library: { libraryCounts: [{ libraryId: "kids", libraryName: "Kids" }] } });
    applyJourneyStage(qc, { dispatchId: "d1", releaseName: "Moon.2009", mediaType: "movies", entityType: "movie", entityId: "m1", libraryId: "kids", stage: "import_succeeded", detail: null, at });
    expect(qc.getQueryData<{ arrivals: unknown[] }>(liveSources.liveExtras.key)!.arrivals[0]).toMatchObject({ libraryId: "kids", libraryName: "Kids" });
    applyJourneyStage(qc, { dispatchId: "d2", releaseName: "Primer.2004", mediaType: "movies", entityType: "movie", entityId: "m2", libraryId: "other", stage: "import_succeeded", detail: null, at: "2026-10-01T10:05:00.000Z" });
    expect(qc.getQueryData<{ arrivals: unknown[] }>(liveSources.liveExtras.key)!.arrivals[0]).toMatchObject({ libraryId: "other", libraryName: null });
  });

  it("puts an import that has just landed on the arrivals wall at once", () => {
    const qc = new QueryClient();
    qc.setQueryData(liveSources.liveExtras.key, { arrivals: [], importsToday: 3 });
    applyJourneyStage(qc, { dispatchId: "d1", releaseName: "Moon.2009", mediaType: "movies", entityType: "movie", entityId: "m1", libraryId: "l", stage: "import_succeeded", detail: null, at });
    expect(qc.getQueryData<{ arrivals: unknown[]; importsToday: number }>(liveSources.liveExtras.key)).toMatchObject({ importsToday: 4, arrivals: [{ entityId: "m1", importedUtc: at }] });
  });

  it("opens and closes an import case in the right list, counting it once", () => {
    const qc = new QueryClient();
    const empty = { openCount: 0, recentCases: [] as Array<{ id: string }> };
    qc.setQueryData(sharedSources.movieRecovery.key, { ...empty });
    qc.setQueryData(sharedSources.seriesRecovery.key, { ...empty });
    applyRecoveryCase(qc, { caseId: "r1", state: "opened", kind: "importFailed", title: "A", entityType: "series", entityId: null, at });
    applyRecoveryCase(qc, { caseId: "r1", state: "opened", kind: "importFailed", title: "A", entityType: "series", entityId: null, at });
    expect(qc.getQueryData<typeof empty>(sharedSources.seriesRecovery.key)).toMatchObject({ openCount: 1, recentCases: [{ id: "r1" }] });
    expect(qc.getQueryData<typeof empty>(sharedSources.movieRecovery.key)!.openCount).toBe(0);
    applyRecoveryCase(qc, { caseId: "r1", state: "closed", kind: null, title: null, entityType: "series", entityId: null, at });
    expect(qc.getQueryData<typeof empty>(sharedSources.seriesRecovery.key)).toMatchObject({ openCount: 0, recentCases: [] });
  });

  it("changes a queue item status, and takes a removed one out", () => {
    const qc = new QueryClient();
    qc.setQueryData(sharedSources.telemetry.key, { clients: [{ clientId: "c1", queue: [{ id: "q1", status: "downloading", errorMessage: null }, { id: "q2", status: "queued", errorMessage: null }] }] });
    applyQueueStatus(qc, { id: "q1", status: "stalled", errorMessage: "No peers" });
    applyQueueRemoved(qc, "q2");
    expect(qc.getQueryData<{ clients: Array<{ queue: unknown[] }> }>(sharedSources.telemetry.key)!.clients[0].queue).toEqual([{ id: "q1", status: "stalled", errorMessage: "No peers" }]);
  });

  it("turns a client light when it stops answering, and ticks a drive free space down", () => {
    const qc = new QueryClient();
    const summary = structuredClone(fullSummary);
    summary.downloads!.clients = [{ id: "c1", name: "Deluge", protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0 }];
    summary.storage = { asOfUtc: at, readingUtc: at, volumes: [{ id: "v1", label: "Films", roles: ["library"], paths: ["D:\\Films"], available: true, totalBytes: 1000, freeBytes: 600 }, { id: "v2", label: "Shows", roles: ["library"], paths: ["E:/Shows"], available: true, totalBytes: 1000, freeBytes: 500 }] };
    qc.setQueryData(DASHBOARD_SUMMARY_KEY, summary);
    applyClientState(qc, { clientId: "c1", clientName: "Deluge", reachable: false, paused: null, message: "refused", at });
    applyDriveSpace(qc, { path: "d:/films/Moon (2009)/Moon.mkv", freeBytes: 580, totalBytes: null, at });
    const next = qc.getQueryData<typeof summary>(DASHBOARD_SUMMARY_KEY)!;
    expect(next.downloads!.clients[0].reachable).toBe(false);
    expect(next.storage!.volumes.map((volume) => volume.freeBytes)).toEqual([580, 500]);
  });

  it("extends the speed trace with each reading and makes it the speed now", () => {
    const qc = new QueryClient();
    qc.setQueryData(DASHBOARD_SUMMARY_KEY, structuredClone(fullSummary));
    qc.setQueryData(liveSources.throughput.key, { hours: 6, samples: [{ capturedUtc: "2026-10-01T03:00:00.000Z", speedMbps: 1, activeCount: 1, uploadMbps: 0 }] });
    applySpeedSample(qc, { downloadMbps: 9.5, uploadMbps: 1.2, active: 2, at });
    const window = qc.getQueryData<{ samples: Array<{ speedMbps: number }> }>(liveSources.throughput.key)!;
    // The reading from seven hours before is out of the six-hour window; the new one is in.
    expect(window.samples.map((sample) => sample.speedMbps)).toEqual([9.5]);
    expect(qc.getQueryData<typeof fullSummary>(DASHBOARD_SUMMARY_KEY)!.downloads).toMatchObject({ downloadMbps: 9.5, uploadMbps: 1.2, active: 2 });
    // A reading that is not newer is ignored.
    applySpeedSample(qc, { downloadMbps: 3, uploadMbps: 0, active: 1, at });
    expect(qc.getQueryData<{ samples: unknown[] }>(liveSources.throughput.key)!.samples).toHaveLength(1);
  });

  it("counts an indexer answer on today's scoreboard and reads its colour from how it went", () => {
    const qc = new QueryClient();
    qc.setQueryData(liveSources.scoreboard.key, { indexers: [{ id: "i1", name: "A", totalQueries: 5, failedQueries: 1, successfulGrabs: 0, lastQueryUtc: null }] });
    applyIndexerAnswer(qc, { indexerId: "i1", indexerName: "A", phase: "answered", query: "x", resultCount: 3, elapsedMs: 300, failed: true, failure: "HTTP 504", at });
    applyIndexerAnswer(qc, { indexerId: "i1", indexerName: "A", phase: "asked", query: "x", resultCount: null, elapsedMs: null, failed: false, failure: null, at });
    expect(qc.getQueryData<{ indexers: unknown[] }>(liveSources.scoreboard.key)!.indexers[0]).toMatchObject({ totalQueries: 6, failedQueries: 2, lastQueryUtc: at });
    expect(pulseOf({ phase: "asked", resultCount: null, elapsedMs: null, failed: false, failure: null })).toMatchObject({ phase: "asking" });
    expect(pulseOf({ phase: "answered", resultCount: 4, elapsedMs: 300, failed: false, failure: null })).toMatchObject({ tone: "ok", words: "4 results in 300 ms" });
    expect(pulseOf({ phase: "answered", resultCount: 4, elapsedMs: 4200, failed: false, failure: null }).tone).toBe("warn");
    expect(pulseOf({ phase: "answered", resultCount: null, elapsedMs: 10, failed: true, failure: "HTTP 504" }).tone).toBe("bad");
  });
});
