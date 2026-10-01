/**
 * The indexers Deluno is asking (#1098): what each one did today, from the stored record of every query (no indexer is asked
 * while the screen waits), and a flash on its tile as it is asked and answers. An indexer that is not answering links to System,
 * where its Test again button is.
 */
import { useEffect, useMemo, useRef, useState } from "react";
import type { IndexerItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { indexerNeedsButton } from "../attention";
import { ago } from "../format";
import { useRealtimeConnected } from "../../../lib/realtime-state";
import { useLiveEvent } from "../../../lib/use-signalr";
import { Tile, TileGroup, type TileState } from "./tile";

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

/** Indexers asked since the last look, so each can flash once as it is asked and answers. */
function useFlashing(todayById: ReadonlyMap<string, IndexerToday>): ReadonlySet<string> {
  const previous = useRef<ReadonlyMap<string, string> | null>(null);
  const [flashing, setFlashing] = useState<ReadonlySet<string>>(() => new Set());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const signature = [...todayById.values()].map((row) => `${row.id}=${row.lastQueryUtc ?? ""}:${row.totalQueries}`).join("|");
  useEffect(() => {
    const current = new Map([...todayById.values()].map((row) => [row.id, `${row.lastQueryUtc ?? ""}:${row.totalQueries}`]));
    const before = previous.current;
    previous.current = current;
    if (before === null) return;
    const asked = new Set<string>();
    for (const [id, stamp] of current) if (before.has(id) && before.get(id) !== stamp) asked.add(id);
    if (asked.size === 0) return;
    setFlashing(asked);
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = setTimeout(() => setFlashing(new Set()), 2400);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);
  useEffect(() => () => {
    if (timer.current !== null) clearTimeout(timer.current);
  }, []);
  return flashing;
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
  const found = event.resultCount === null ? "answered" : `${event.resultCount.toLocaleString("en-AU")} ${event.resultCount === 1 ? "result" : "results"}`;
  return { phase: "answered", tone: slow ? "warn" : "ok", words: `${found} in ${event.elapsedMs ?? 0} ms` };
}

/** Each indexer asked or answered just now: a light pulses while it is asked, and flashes its answer for a moment. */
function usePulses(): ReadonlyMap<string, IndexerPulse> {
  const [pulses, setPulses] = useState<ReadonlyMap<string, IndexerPulse>>(() => new Map());
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  useLiveEvent("IndexerQueryRecorded", (event) => {
    const pulse = pulseOf(event);
    setPulses((current) => new Map(current).set(event.indexerId, pulse));
    const known = timers.current.get(event.indexerId);
    if (known) clearTimeout(known);
    // A question that never reports its answer, or an answer shown long enough, goes back to the light's own colour.
    timers.current.set(event.indexerId, setTimeout(() => {
      timers.current.delete(event.indexerId);
      setPulses((current) => {
        const next = new Map(current);
        next.delete(event.indexerId);
        return next;
      });
    }, pulse.phase === "asking" ? 15_000 : 2400));
  });
  useEffect(() => () => {
    for (const timer of timers.current.values()) clearTimeout(timer);
  }, []);
  return pulses;
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

/** The header: "4 answering · 1 switched off · 128 queries today". */
export function indexersSummary(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, fallbackQueries?: number): string {
  const enabled = indexers.filter((indexer) => indexer.isEnabled);
  const answering = enabled.filter((indexer) => !indexerNeedsButton(indexer)).length;
  const off = indexers.length - enabled.length;
  const queries = today.size > 0 ? [...today.values()].reduce((sum, row) => sum + (row.totalQueries ?? 0), 0) : fallbackQueries;
  const parts = [`${answering.toLocaleString("en-AU")} answering`];
  if (off > 0) parts.push(`${off.toLocaleString("en-AU")} switched off`);
  if (queries !== undefined) parts.push(`${queries.toLocaleString("en-AU")} ${queries === 1 ? "query" : "queries"} today`);
  return parts.join(" · ");
}

/**
 * The indexers as tiles, one each (#1098): the busy ones first, the switched-off ones grey at the end. A tile flashes when its
 * indexer is asked, then green or red on the answer; where a daily limit is set it says how much of it is used.
 */
export function IndexerTiles({ indexers, scoreboard, summary, now }: { indexers: IndexerItem[]; scoreboard?: IndexerScoreboardToday | null; summary: DashboardSummary | undefined; now: number }) {
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = useMemo(() => new Map((rows ?? []).map((row) => [row.id, row])), [rows]);
  const connected = useRealtimeConnected();
  const pulses = usePulses();
  // Connected, each tile follows the questions as they are asked and answered; disconnected, a tile flashes when its count moved at the next read.
  const polled = useFlashing(todayById);
  const flashing = connected ? new Set<string>() : polled;
  const sorted = [...indexers].sort((a, b) => Number(b.isEnabled) - Number(a.isEnabled) || a.name.localeCompare(b.name));
  return (
    <TileGroup title="Indexers" count={indexersSummary(indexers, todayById, summary?.indexers?.queriesToday)} to="/indexers/indexers" empty="No indexers are set up, so nothing can be searched for.">
      {sorted.map((indexer) => {
        const today = todayById.get(indexer.id);
        const meta = indexerMeta(indexer, today, now);
        const pulse = pulses.get(indexer.id);
        const state: TileState | null = !indexer.isEnabled
          ? "off"
          : pulse?.phase === "asking" || flashing.has(indexer.id)
            ? "asked"
            : pulse?.phase === "answered"
              ? pulse.tone === "bad" ? "failed" : "answered"
              : null;
        const tone: Tone = pulse?.phase === "answered" && pulse.tone === "bad" ? "bad" : meta.tone;
        return (
          <Tile
            key={indexer.id}
            to={indexerNeedsButton(indexer) ? "/?tab=system" : "/indexers/indexers"}
            name={indexer.name}
            tone={tone}
            state={state}
            pulse={pulse?.phase === "asking"}
            meta={pulse && pulse.phase === "answered" ? pulse.words : meta.text}
            label={`${indexer.name}: ${meta.text}${pulse ? `, ${pulse.words}` : ""}${today?.lastQueryUtc ? `, last asked ${ago(today.lastQueryUtc, now)}` : ""}`}
          />
        );
      })}
    </TileGroup>
  );
}
