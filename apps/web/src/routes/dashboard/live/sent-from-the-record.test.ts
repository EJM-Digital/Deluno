import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { sharedSources } from "../../../lib/shared-sources";
import { buildJourney, byStage } from "./journey";
import { applyJourneyStage, applySearchesInFlight } from "./realtime";
import { liveSources } from "../sources";
import { fullSummary } from "../downloads-fixtures";

const NOW = Date.parse("2026-10-31T13:37:00Z");
const iso = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();
const RELEASE = "His.Girl.Friday.1940.1080p.BluRay.x264-SiNNERS";

/** The rig's own reading on 31 Oct (#1449): a magnet still fetching its details, one no-progress strike. */
const queue = (overrides: Record<string, unknown> = {}) => ({
  id: "abc123", clientId: "qb", clientName: "qBittorrent", protocol: "torrent", mediaType: "movie", title: "His Girl Friday", releaseName: RELEASE, category: "", status: "downloading",
  progress: 0, speedMbps: 0, etaSeconds: 0, sizeBytes: 0, downloadedBytes: 0, peers: 0, indexerName: "", errorMessage: null, addedUtc: iso(-60_000), sourcePath: null, libraryId: null, healthFindings: null, ...overrides
}) as never;
const finding = (overrides: Record<string, unknown> = {}) => ({
  severity: "warning", kind: "no-progress", summary: "This download has no observed throughput", evidence: "", recommendedAction: "", canSafelyRetry: true, canSafelyRemove: true,
  strikeCount: 1, candidateBlocked: false, ignoredUntilUtc: null, ruleId: "default-no-progress", ruleActionPreset: "replace", ruleStrikeLimit: 3, nextStrikeUtc: iso(2 * 60_000), ...overrides
});
const dispatch = (overrides: Record<string, unknown> = {}) => ({
  id: "d1", libraryId: "l", mediaType: "movies", entityType: "movie", entityId: "m1", releaseName: RELEASE, indexerName: "Indexer", downloadClientId: "qb", downloadClientName: "qBittorrent",
  status: "sent", grabStatus: "succeeded", grabAttemptedUtc: iso(-5_000), torrentHashOrItemId: "abc123", notesJson: null, createdUtc: iso(-5_000), ...overrides
}) as never;
const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };

describe("a release just sent is a Grabbed card from the sent record (#1442)", () => {
  it("is on the Grabbed lane before the client lists it, and the client's card replaces it with the same key", () => {
    const sent = byStage(buildJourney({ ...base, dispatches: [dispatch()] }));
    expect(sent.grabbed).toHaveLength(1);
    expect(sent.grabbed[0]).toMatchObject({ key: "qb:abc123", state: { label: "Sent to qBittorrent · waiting for it to start" }, entity: { kind: "movie", id: "m1" } });
    const listed = byStage(buildJourney({ ...base, dispatches: [dispatch()], queue: [queue()] }));
    expect(listed.grabbed).toHaveLength(0);
    expect(listed.downloading.map((card) => card.key)).toEqual(["qb:abc123"]);
  });

  it("has no frame with neither Searching nor Grabbed when the search ends: the sent record arrives first", () => {
    const qc = new QueryClient();
    const searching = { entityType: "movie", entityId: "m1", title: "His Girl Friday", startedBy: "you", label: "Checking 4 indexers", startedUtc: iso(-9_000) };
    qc.setQueryData(liveSources.liveExtras.key, { searching: [searching] });
    qc.setQueryData(sharedSources.dispatches.key, []);
    const frame = () => {
      const sent = qc.getQueryData(sharedSources.dispatches.key) as never;
      return (qc.getQueryData<{ searching: unknown[] }>(liveSources.liveExtras.key)?.searching.length ?? 0) + byStage(buildJourney({ ...base, dispatches: sent })).grabbed.length;
    };
    const frames = [frame()];
    // The order the server pushes: the release is sent (recorded), then the search ends.
    applyJourneyStage(qc, { dispatchId: "d1", releaseName: RELEASE, mediaType: "movies", entityType: "movie", entityId: "m1", libraryId: "l", stage: "grab_succeeded", detail: null, at: iso(-2_000) });
    frames.push(frame());
    applySearchesInFlight(qc, { searches: [], at: iso(-1_000) });
    frames.push(frame());
    // The client lists it ten seconds later: still one card, never none.
    const listed = byStage(buildJourney({ ...base, dispatches: qc.getQueryData(sharedSources.dispatches.key) as never, queue: [queue()] }));
    frames.push(listed.grabbed.length + listed.downloading.length);
    expect(frames).toEqual([1, 2, 1, 1]);
  });

  it("draws nothing for a refused send, one sent long ago, or one that was imported", () => {
    for (const record of [dispatch({ grabStatus: "failed" }), dispatch({ grabAttemptedUtc: iso(-10 * 60_000), createdUtc: iso(-10 * 60_000) }), dispatch({ importStatus: "imported" })]) {
      expect(byStage(buildJourney({ ...base, dispatches: [record] })).grabbed).toHaveLength(0);
    }
  });
});
