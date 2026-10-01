/**
 * The indexers Deluno is asking (#1096): what each one did today, from the
 * stored record of every query (no indexer is asked while the screen waits):
 * how many times it was asked, how many releases were grabbed from it, how many
 * asks failed, and when it last answered. A quiet one says it was not asked.
 *
 * An indexer that is not answering shows as a red light that links to System, where
 * its Test again button is.
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { StatusLed } from "../../../components/ui/status-led";
import type { IndexerItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { cn } from "../../../lib/utils";
import { indexerNeedsButton } from "../attention";
import { ago } from "../format";
import { motionAllowed } from "../../../lib/page-motion";
import { useRealtimeConnected } from "../../../lib/realtime-state";
import { useLiveEvent } from "../../../lib/use-signalr";

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

export function indexerAnswer(indexer: IndexerItem, today: IndexerToday | undefined, now: number): { tone: Tone; text: string } {
  if (!indexer.isEnabled) return { tone: "idle", text: "switched off" };
  if (indexer.rateLimitedUntilUtc && Date.parse(indexer.rateLimitedUntilUtc) > now) return { tone: "warn", text: "asked too often, waiting" };
  if (indexer.healthStatus === "unknown" || !indexer.healthStatus) return { tone: "idle", text: "not asked yet" };
  const asked = today?.totalQueries ?? 0;
  if (asked === 0) return { tone: "ok", text: "not asked today" };
  const failed = today?.failedQueries ?? 0;
  const grabbed = today?.successfulGrabs ?? 0;
  const parts = [`${asked.toLocaleString("en-AU")} ${asked === 1 ? "query" : "queries"}`];
  if (grabbed > 0) parts.push(`${grabbed.toLocaleString("en-AU")} grabbed`);
  if (failed > 0) parts.push(`${failed.toLocaleString("en-AU")} failed`);
  return { tone: failed > 0 ? "warn" : "ok", text: parts.join(" · ") };
}

const MAX_LIGHTS = 12;

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

/**
 * The indexers as a row of named lights (the last cell of the Right now band): green
 * when answering, amber when some asks failed, red when not answering, grey when
 * switched off, and a light flashes when that indexer has just been asked.
 * Each says what it did today in its tooltip. A broken one links to System, where
 * its Test again button is.
 */
export function IndexerLights({ indexers, scoreboard, summary, now }: { indexers: IndexerItem[]; scoreboard?: IndexerScoreboardToday | null; summary: DashboardSummary | undefined; now: number }) {
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = useMemo(() => new Map((rows ?? []).map((row) => [row.id, row])), [rows]);
  const connected = useRealtimeConnected();
  const pulses = usePulses();
  // Connected, each light follows the questions as they are asked and answered; disconnected, a light flashes when its count moved at the next read.
  const polled = useFlashing(todayById);
  const flashing = connected ? new Set<string>() : polled;
  const asked = rows ? rows.reduce((sum, row) => sum + (row.totalQueries ?? 0), 0) : summary?.indexers?.queriesToday;
  const failed = rows ? rows.reduce((sum, row) => sum + (row.failedQueries ?? 0), 0) : summary?.indexers?.failedQueriesToday;
  const sorted = [...indexers].sort(
    (a, b) => Number(indexerNeedsButton(b)) - Number(indexerNeedsButton(a)) || Number(b.isEnabled) - Number(a.isEnabled) || (todayById.get(b.id)?.totalQueries ?? 0) - (todayById.get(a.id)?.totalQueries ?? 0) || a.priority - b.priority
  );
  const shown = sorted.slice(0, MAX_LIGHTS);
  const hidden = sorted.length - shown.length;
  return (
    <section aria-label="Indexers" className="flex min-h-0 min-w-0 flex-col px-[var(--card-pad-x)] py-2.5">
      <div className="flex items-baseline justify-between gap-2">
        <Link to="/indexers/indexers" aria-label="Manage: Indexers" className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">Indexers</Link>
        {asked !== undefined ? (
          <span className="whitespace-nowrap text-[length:var(--type-caption)] tabular-nums text-muted-foreground">
            {asked.toLocaleString("en-AU")} {asked === 1 ? "query" : "queries"} today{failed ? <span className="text-warning"> · {failed.toLocaleString("en-AU")} failed</span> : null}
          </span>
        ) : null}
      </div>
      {sorted.length === 0 ? (
        <p className="mt-1.5 text-[length:var(--type-body-sm)] text-muted-foreground">No indexers are set up, so nothing can be searched for.</p>
      ) : (
        <ul className="mt-1.5 flex flex-wrap content-start gap-1.5">
          {shown.map((indexer) => {
            const today = todayById.get(indexer.id);
            const broken = indexerNeedsButton(indexer);
            const answer = indexerAnswer(indexer, today, now);
            const latency = indexer.lastHealthLatencyMs ?? null;
            // Grey when switched off, green when it answers, amber when it answers slowly, red when it does not answer.
            const slow = !broken && indexer.isEnabled && latency !== null && latency > SLOW_MS;
            const pulse = pulses.get(indexer.id);
            const restingTone: Tone = broken ? (indexer.healthStatus === "unreachable" ? "bad" : "warn") : slow ? "warn" : answer.tone;
            // A question just answered shows its own colour for a moment; one being asked shows as asking.
            const tone: Tone = pulse && pulse.phase === "answered" ? pulse.tone : restingTone;
            const last = today?.lastQueryUtc ?? null;
            const text = broken ? `not answering${indexer.lastHealthMessage ? `: ${indexer.lastHealthMessage}` : ""}` : slow ? `slow, ${latency} ms to answer` : answer.text;
            const queries = today?.totalQueries ?? 0;
            const tip = [
              `${indexer.name}: ${pulse ? pulse.words : text}.`,
              indexer.isEnabled && latency !== null ? `Last answer ${latency} ms${indexer.lastHealthTestUtc ? `, ${ago(indexer.lastHealthTestUtc, now)}` : ""}.` : null,
              `Today ${queries.toLocaleString("en-AU")} ${queries === 1 ? "query" : "queries"}${today?.failedQueries ? `, ${today.failedQueries.toLocaleString("en-AU")} failed` : ""}${last ? `, last asked ${ago(last, now)}` : ""}.`
            ].filter(Boolean).join(" ");
            const asking = flashing.has(indexer.id) || pulse?.phase === "asking" || pulse?.phase === "answered";
            return (
              <li key={indexer.id}>
                <Link
                  to={broken ? "/?tab=system" : "/indexers/indexers"}
                  title={tip}
                  aria-label={`${indexer.name}: ${text}`}
                  className={cn(
                    "inline-flex max-w-[9.5rem] items-center gap-1.5 rounded-full border border-hairline bg-surface-1/60 px-2 py-0.5 text-[length:var(--type-caption)] text-foreground transition-colors hover:bg-primary/[0.06] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    asking && "border-primary/50 bg-primary/[0.08]",
                    tone === "bad" && "border-destructive/40"
                  )}
                >
                  <span className="relative inline-flex shrink-0">
                    {asking && motionAllowed() ? <span aria-hidden className="live-beat absolute -inset-1 rounded-full bg-primary/50" /> : null}
                    <StatusLed tone={tone} size={7} pulse={tone === "warn" || tone === "bad"} />
                  </span>
                  <span className="truncate">{indexer.name}</span>
                </Link>
              </li>
            );
          })}
          {hidden > 0 ? (
            <li>
              <Link to="/indexers/indexers" className="inline-flex items-center rounded-full px-2 py-0.5 text-[length:var(--type-caption)] font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">and {hidden.toLocaleString("en-AU")} more</Link>
            </li>
          ) : null}
        </ul>
      )}
    </section>
  );
}
