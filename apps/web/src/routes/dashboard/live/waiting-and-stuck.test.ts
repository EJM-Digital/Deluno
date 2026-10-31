import { describe, expect, it } from "vitest";
import { isStuck, stuckNextStep, waitingWords } from "../../../lib/download-wait";
import { queueItemNeedsAttention } from "../../../lib/needs-action";
import { statusOf } from "./card";
import { buildJourney, byStage, stationNoun } from "./journey";
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
const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };

describe("a new download is waiting, not stuck, until the stall rule decides (#1449)", () => {
  it("says it is getting the torrent's details, then which strike it is on and when the next check is", () => {
    expect(waitingWords(queue(), NOW)).toBe("Getting the torrent's details");
    expect(waitingWords(queue({ healthFindings: [finding()] }), NOW)).toBe("Getting the torrent's details · checking again in 2 min (strike 1 of 3)");
    expect(waitingWords(queue({ sizeBytes: 2e9, healthFindings: [finding()], peers: 4 }), NOW)).toBe("No data yet · checking again in 2 min (strike 1 of 3)");
  });

  it("is not stuck for the server's low-availability note, which the server itself never counts as a strike", () => {
    // The rig on 31 Oct, The General (1926) a minute after it was sent: size 0, two peers, one no-progress strike and a
    // low-availability reading at strike 0. It read "Stuck · needs a look".
    const general = queue({ peers: 2, healthFindings: [finding(), finding({ kind: "low-availability", strikeCount: 0, ruleId: null, ruleStrikeLimit: null })] });
    expect(isStuck(general, NOW)).toBe(false);
    expect(queueItemNeedsAttention(general)).toBe(false);
    expect(waitingWords(general, NOW)).toBe("Getting the torrent's details · checking again in 2 min (strike 1 of 3)");
    expect(isStuck(queue({ sizeBytes: 2e9, peers: 2, speedMbps: 1, healthFindings: [finding({ kind: "low-availability", strikeCount: 0 })] }), NOW)).toBe(false);
  });

  it("says it is looking for people sharing it once it has its details but no peers", () => {
    expect(waitingWords(queue({ sizeBytes: 2e9 }), NOW)).toBe("Looking for people sharing it");
    expect(waitingWords(queue({ sizeBytes: 2e9, peers: 5, speedMbps: 2 }), NOW)).toBeNull();
    expect(waitingWords(queue({ protocol: "usenet" }), NOW)).toBeNull();
  });

  it("is not a problem for the bell on a first strike, and is on the board as a waiting card with its own words", () => {
    const struck = queue({ healthFindings: [finding()] });
    expect(queueItemNeedsAttention(struck)).toBe(false);
    expect(isStuck(struck, NOW)).toBe(false);
    const cards = buildJourney({ ...base, queue: [struck] });
    expect(cards[0]).toMatchObject({ stage: "downloading", waiting: true, stuck: false });
    expect(statusOf(cards[0], 4).text).toBe("Getting the torrent's details · checking again in 2 min (strike 1 of 3)");
    expect(stationNoun("downloading", byStage(cards).downloading)).toBe("waiting");
  });

  it("is stuck only when the rule has decided, saying what Deluno does next, and the station agrees", () => {
    const real = queue({ progress: 12, sizeBytes: 2e9, healthFindings: [finding({ strikeCount: 3, candidateBlocked: true })] });
    expect(queueItemNeedsAttention(real)).toBe(true);
    expect(stuckNextStep(real, NOW)).toBe("refused, finding another copy");
    const cards = buildJourney({ ...base, queue: [real, queue({ id: "ok", releaseName: "Other.Film.2001.1080p", progress: 50, sizeBytes: 1e9, speedMbps: 3, peers: 9 })] });
    expect(statusOf(cards.find((card) => card.stuck)!, 4).text).toBe("Stuck at 12% · refused, finding another copy");
    const downloading = byStage(cards).downloading;
    expect(stationNoun("downloading", downloading)).toBe("here");
    expect(stationNoun("downloading", downloading.filter((card) => !card.stuck))).toBe("moving");
    expect(stationNoun("downloading", downloading.filter((card) => card.stuck))).toBe("stuck");
  });

  it("holds for the other rules: a limit reached without a block, a rule that only tells, a client that says stalled with nothing watching", () => {
    expect(isStuck(queue({ healthFindings: [finding({ strikeCount: 3 })] }), NOW)).toBe(true);
    expect(stuckNextStep(queue({ healthFindings: [finding({ strikeCount: 3, ruleActionPreset: "just-tell-me" })] }), NOW)).toBe("not moving, waiting for you");
    expect(isStuck(queue({ status: "stalled" }), NOW)).toBe(true);
    expect(isStuck(queue({ healthFindings: [finding({ kind: "post-processing-failed", strikeCount: 0, ruleStrikeLimit: null })] }), NOW)).toBe(true);
    expect(queueItemNeedsAttention(queue({ errorMessage: "I/O error" }))).toBe(true);
  });
});
