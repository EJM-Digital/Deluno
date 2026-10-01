/**
 * The indexers Deluno is asking (#1098): what each one did today, from the stored record of every query (no indexer is asked
 * while the screen waits), drawn on the Sources map. An indexer that is not answering links to System,
 * where its Test again button is.
 */
import type { IndexerItem } from "../../../lib/api";
import type { Tone } from "../../../lib/status-tones";
import { indexerNeedsButton } from "../attention";

/** One indexer's row of today's scoreboard (`GET /api/indexers/scoreboard?days=1`). */
export interface IndexerToday {
  id: string;
  name: string;
  totalQueries: number;
  failedQueries: number;
  successfulGrabs: number;
  lastQueryUtc?: string | null;
}

export interface IndexerScoreboardToday {
  indexers: IndexerToday[];
}

/** An indexer that takes longer than this to answer is slow, and its light is amber. */
const SLOW_MS = 2000;

/** What an indexer is doing right now, from the pushed events: being asked, or just answered (and how). */
export interface IndexerPulse {
  phase: "asking" | "answered";
  tone: Tone;
  words: string;
}

/** An answer that took longer than this is slow, and its flash is amber. */
export const SLOW_ANSWER_MS = 2000;

/** How a pushed answer reads: green when it came back fast, amber when it was slow or empty-handed on a failure, red when it failed. */
export function pulseOf(event: { phase: "asked" | "answered"; resultCount: number | null; elapsedMs: number | null; failed: boolean; failure: string | null }): IndexerPulse {
  if (event.phase === "asked") return { phase: "asking", tone: "info", words: "being asked" };
  if (event.failed) return { phase: "answered", tone: "bad", words: event.failure ? `failed: ${event.failure}` : "failed" };
  const slow = (event.elapsedMs ?? 0) > SLOW_ANSWER_MS;
  const found = event.resultCount === null ? "answered" : `${event.resultCount.toLocaleString("en-AU")} ${event.resultCount === 1 ? "result" : "results"}`;
  return { phase: "answered", tone: slow ? "warn" : "ok", words: `${found} in ${event.elapsedMs ?? 0} ms` };
}

/** What an indexer says about itself today: "46 of 100 today · 320 ms", "switched off", "not answering · 5 today". */
export function indexerMeta(indexer: IndexerItem, today: IndexerToday | undefined, now: number): { text: string; tone: Tone } {
  if (!indexer.isEnabled) return { text: "switched off", tone: "idle" };
  const asked = today?.totalQueries ?? 0;
  const status = indexer.queryLimitStatus;
  const limit = status?.limit ?? indexer.queryLimit ?? null;
  // A daily limit says how much of it is used today; an hourly one is not a count of today, so the tile says only what was asked.
  const daily = (status?.unit ?? indexer.limitsUnit ?? "day").toLowerCase() !== "hour";
  const used = status ? status.count : asked;
  const queries = daily && limit !== null && limit > 0 ? `${used.toLocaleString("en-AU")} of ${limit.toLocaleString("en-AU")} today` : `${asked.toLocaleString("en-AU")} today`;
  if (indexerNeedsButton(indexer)) return { text: `not answering · ${queries}`, tone: indexer.healthStatus === "unreachable" ? "bad" : "warn" };
  if (indexer.rateLimitedUntilUtc && Date.parse(indexer.rateLimitedUntilUtc) > now) return { text: `asked too often, waiting · ${queries}`, tone: "warn" };
  const latency = indexer.lastHealthLatencyMs ?? null;
  const slow = latency !== null && latency > SLOW_MS;
  return { text: latency !== null ? `${queries} · ${latency.toLocaleString("en-AU")} ms` : queries, tone: slow ? "warn" : "ok" };
}

/** The header, short enough never to be cut: "4 answering · 1 off · 128 today". */
export function indexersSummary(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, fallbackQueries?: number): string {
  const enabled = indexers.filter((indexer) => indexer.isEnabled);
  const answering = enabled.filter((indexer) => !indexerNeedsButton(indexer)).length;
  const off = indexers.length - enabled.length;
  const queries = today.size > 0 ? [...today.values()].reduce((sum, row) => sum + (row.totalQueries ?? 0), 0) : fallbackQueries;
  const parts = [`${answering.toLocaleString("en-AU")} answering`];
  if (off > 0) parts.push(`${off.toLocaleString("en-AU")} off`);
  if (queries !== undefined) parts.push(`${queries.toLocaleString("en-AU")} today`);
  return parts.join(" · ");
}
