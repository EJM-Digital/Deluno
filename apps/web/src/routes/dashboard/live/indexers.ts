/**
 * The indexers Deluno is asking (#1098): what each one did today, from the stored record of every query (no indexer is asked
 * while the screen waits), for the Sources scoreboard and the flash when one is asked and answers.
 */
import type { Tone } from "../../../lib/status-tones";
import { plural } from "../../../lib/plural";

/** One indexer's row of today's scoreboard (`GET /api/indexers/scoreboard?days=1`). */
export interface IndexerToday {
  id: string;
  name: string;
  totalQueries: number;
  failedQueries: number;
  successfulGrabs: number;
  lastQueryUtc?: string | null;
  /** Matching releases it returned across today's searches. */
  candidatesReturned?: number;
  /** Its average answer today, in ms. */
  averageResponseMilliseconds?: number | null;
}

export interface IndexerScoreboardToday {
  indexers: IndexerToday[];
}

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
  const found = event.resultCount === null ? "answered" : plural(event.resultCount, "result");
  return { phase: "answered", tone: slow ? "warn" : "ok", words: `${found} in ${event.elapsedMs ?? 0} ms` };
}
