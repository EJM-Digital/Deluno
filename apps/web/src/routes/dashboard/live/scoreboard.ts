/**
 * The Sources card (#1098, option D, the Scoreboard): what each indexer delivered today as a table, the download clients as
 * one stacked speed bar, and the connections as pills with today's counts. Plain functions of the readings the Live tab
 * already has; nothing here draws or asks anything. Tested on their own.
 */
import type { IndexerItem } from "../../../lib/api";
import type { LiveConnection } from "../../../lib/api/types/operations";
import type { ClientLight } from "./clients";
import { connectionKey, connectionWords, sortConnections, tileName } from "./connections";
import { SLOW_ANSWER_MS, type IndexerToday } from "./indexers";

/** An enabled indexer that is not known to be healthy gets Test again (the bell's rule on the server is the same). */
export function indexerNeedsButton(indexer: Pick<IndexerItem, "isEnabled" | "healthStatus">): boolean {
  return indexer.isEnabled && indexer.healthStatus !== "healthy";
}

/** An indexer's standing, as the word on its pill: Good, Slow, Down or Off. */
export type Standing = "good" | "slow" | "down" | "off";

export const STANDING_WORD: Record<Standing, string> = { good: "Good", slow: "Slow", down: "Down", off: "Off" };

export interface ScoreRow {
  /** `indexer:<id>`, unique on the page. */
  key: string;
  id: string;
  name: string;
  standing: Standing;
  /** Releases Deluno took from this indexer today. */
  grabs: number;
  /** Matching releases it returned across today's searches. */
  results: number;
  /** Its average answer today in ms; its last health check's when it has not been asked today; null when neither is known. */
  ms: number | null;
  /** The response bar, 4 to 100: quicker is fuller. */
  speed: number;
  /** The share of today's searches it answered, 0 to 100; null when it has not been asked today. */
  answeredPct: number | null;
  /** The row's hover: all of it in a sentence. */
  title: string;
  /** What the status pill says on hover. */
  standingTitle: string;
  /** Where a click goes: its settings, or System where its Test again button is. */
  to: string;
}

const number = (value: number) => value.toLocaleString("en-AU");

function standingOf(indexer: IndexerItem, ms: number | null, now: number): Standing {
  if (!indexer.isEnabled) return "off";
  if (indexerNeedsButton(indexer)) return "down";
  const waiting = indexer.rateLimitedUntilUtc ? Date.parse(indexer.rateLimitedUntilUtc) > now : false;
  return waiting || (ms ?? 0) > SLOW_ANSWER_MS ? "slow" : "good";
}

/** The response bar's length: 4% for the slowest, nearly full for the quickest (the mockup's `100 - ms / 30`). */
export function speedBar(ms: number | null): number {
  return ms === null ? 0 : Math.max(4, 100 - Math.min(96, ms / 30));
}

/** One row per indexer, ranked by what it delivered today (grabs, then results); switched-off ones last, then by name. */
export function scoreRows(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, now: number): ScoreRow[] {
  return indexers
    .map((indexer): ScoreRow => {
      const row = today.get(indexer.id);
      const asked = row?.totalQueries ?? 0;
      const average = row?.averageResponseMilliseconds;
      const ms = asked > 0 && typeof average === "number" && average > 0 ? Math.round(average) : indexer.lastHealthLatencyMs ?? null;
      const standing = standingOf(indexer, ms, now);
      const answeredPct = asked > 0 ? Math.round(((asked - (row?.failedQueries ?? 0)) / asked) * 100) : null;
      const grabs = row?.successfulGrabs ?? 0;
      const results = row?.candidatesReturned ?? 0;
      const said = standing === "off"
        ? "switched off"
        : [`${number(grabs)} ${grabs === 1 ? "grab" : "grabs"} won`, `${number(results)} ${results === 1 ? "result" : "results"}`, ms !== null && standing !== "down" ? `${number(ms)} ms average response` : null, answeredPct !== null ? `answered ${answeredPct}% today` : "not asked yet today"].filter(Boolean).join(", ");
      return {
        key: `indexer:${indexer.id}`,
        id: indexer.id,
        name: indexer.name,
        standing,
        grabs,
        results,
        ms: standing === "down" ? null : ms,
        speed: standing === "down" || standing === "off" ? 0 : speedBar(ms),
        answeredPct,
        title: `${indexer.name}: ${said}`,
        standingTitle: answeredPct !== null ? `Answered ${answeredPct}% of today's searches` : "Not asked yet today",
        to: standing === "down" ? "/?tab=system" : "/indexers/indexers"
      };
    })
    .sort((a, b) => Number(b.standing !== "off") - Number(a.standing !== "off") || b.grabs - a.grabs || b.results - a.results || a.name.localeCompare(b.name));
}

/** The header: "11 of 12 answering · 2,846 searches today". */
export function scoreSummary(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, fallbackQueries?: number): string {
  const enabled = indexers.filter((indexer) => indexer.isEnabled);
  const answering = enabled.filter((indexer) => !indexerNeedsButton(indexer)).length;
  const searches = today.size > 0 ? [...today.values()].reduce((sum, row) => sum + (row.totalQueries ?? 0), 0) : fallbackQueries;
  const parts = [`${number(answering)} of ${number(enabled.length)} answering`];
  if (searches !== undefined) parts.push(`${number(searches)} ${searches === 1 ? "search" : "searches"} today`);
  return parts.join(" · ");
}

/**
 * Which client, not how it is doing: shades that are never green, amber or red, because those mean status everywhere on Live.
 * A client keeps its colour as speeds change (they follow the name order).
 */
export const CLIENT_COLOURS = ["hsl(205 85% 55%)", "hsl(265 70% 62%)", "hsl(320 65% 58%)", "hsl(232 60% 60%)", "hsl(188 75% 42%)"] as const;

/** A client's name goes on its segment of the bar when it holds more than this share of the speed. */
export const SEGMENT_NAMED_SHARE = 0.16;

export interface ClientSegment {
  id: string;
  name: string;
  colour: string;
  speedMbps: number;
  /** Its share of the total speed, 0 to 1. */
  share: number;
  /** The segment carries the client's name (it is wide enough). */
  named: boolean;
  /** The legend's words: "6.8 MB/s · 8 seeding", "idle · 2 seeding". */
  words: string;
}

export interface ClientBar {
  segments: ClientSegment[];
  totalMbps: number;
  moving: number;
  /** The header: "12.4 MB/s across 2 of 3". */
  summary: string;
}

function clientWords(light: ClientLight): string {
  const seeding = light.seeding > 0 ? `${number(light.seeding)} seeding` : null;
  const state = light.moving ? `${light.speedMbps.toFixed(1)} MB/s` : light.tone === "idle" ? "switched off" : light.tone === "bad" ? "not answering" : light.tone === "warn" ? "paused" : "idle";
  return [state, seeding].filter(Boolean).join(" · ");
}

export function clientBar(lights: readonly ClientLight[]): ClientBar {
  const ordered = [...lights].sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
  const totalMbps = ordered.reduce((sum, light) => sum + (light.moving ? light.speedMbps : 0), 0);
  const segments = ordered.map((light, index): ClientSegment => {
    const speedMbps = light.moving ? light.speedMbps : 0;
    const share = totalMbps > 0 ? speedMbps / totalMbps : 0;
    return { id: light.id, name: light.name, colour: CLIENT_COLOURS[index % CLIENT_COLOURS.length], speedMbps, share, named: share > SEGMENT_NAMED_SHARE, words: clientWords(light) };
  });
  const moving = ordered.filter((light) => light.moving).length;
  return { segments, totalMbps, moving, summary: ordered.length === 0 ? "none set up" : `${totalMbps.toFixed(1)} MB/s across ${number(moving)} of ${number(ordered.length)}` };
}

export interface ConnectionPill {
  key: string;
  name: string;
  /** What it did today: the count on the pill. */
  count: number;
  off: boolean;
  down: boolean;
  /** A processor with work in hand right now. */
  working: boolean;
  /** Its role and what it did, on hover. */
  title: string;
}

/** One pill per connection, in the order the tiles used (by role, then name; switched-off last), with today's count. */
export function connectionPills(connections: readonly LiveConnection[]): ConnectionPill[] {
  return sortConnections(connections).map((connection) => ({
    key: connectionKey(connection),
    name: tileName(connection),
    count: connection.countToday,
    off: !connection.enabled,
    down: connection.enabled && !connection.healthy,
    working: connection.enabled && connection.busy > 0,
    title: `${connection.name}: ${connectionWords(connection)}`
  }));
}
