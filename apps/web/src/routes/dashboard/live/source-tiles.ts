/**
 * The Sources card (#1098): what each tile says. One tile per indexer, download client and connection, from the same readings
 * the Live tab has always used, so the words never changed. Nothing here draws or animates; plain functions, tested on their own.
 */
import type { IndexerItem } from "../../../lib/api";
import type { LiveConnection } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { indexerNeedsButton } from "../attention";
import { clientMeta, type ClientLight } from "./clients";
import { connectionKey, connectionWords, tileName, type LiveConnectionKind } from "./connections";
import { indexerMeta, type IndexerToday } from "./indexers";

export type SourceGroup = "indexers" | "clients" | "connections";

/** A tile's status: green answering, amber slow or paused, red not answering, grey switched off. */
export type TileTone = "ok" | "warn" | "bad" | "idle";

export interface SourceNode {
  /** Unique across the card: `indexer:<id>`, `client:<id>`, or the connection's own key. */
  key: string;
  group: SourceGroup;
  name: string;
  /** The small muted words under the name: "46 of 100 today · 320 ms", "6.8 MB/s · 8 seeding", "Processor · working on 1". */
  sub: string;
  tone: TileTone;
  off: boolean;
  /** A client moving data now, and how fast (MB/s). */
  speedMbps: number;
  /** A processor with work in hand right now. */
  working: boolean;
  /** The accessible name, unique among the tiles: "NZBgeek: 46 of 100 today · 320 ms". */
  label: string;
  /** The hover detail, a line each. */
  detail: string[];
  /** Where a click goes. */
  to: string;
}

const MANAGE: Record<LiveConnectionKind, string> = {
  processor: "/indexers/companion-tools",
  mediaServer: "/indexers/media-servers",
  requests: "/indexers/media-servers",
  subtitles: "/indexers/subtitle-providers",
  lists: "/settings/lists"
};

function tileTone(tone: Tone): TileTone {
  return tone === "bad" ? "bad" : tone === "warn" ? "warn" : tone === "idle" ? "idle" : "ok";
}

export function indexerNodes(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, now: number, agoOf: (iso: string) => string): SourceNode[] {
  return [...indexers]
    .sort((a, b) => Number(b.isEnabled) - Number(a.isEnabled) || a.name.localeCompare(b.name))
    .map((indexer) => {
      const row = today.get(indexer.id);
      const meta = indexerMeta(indexer, row, now);
      const detail = [meta.text];
      if (row?.lastQueryUtc) detail.push(`last asked ${agoOf(row.lastQueryUtc)}`);
      return {
        key: `indexer:${indexer.id}`,
        group: "indexers" as const,
        name: indexer.name,
        sub: meta.text,
        tone: tileTone(meta.tone),
        off: !indexer.isEnabled,
        speedMbps: 0,
        working: false,
        label: `${indexer.name}: ${meta.text}`,
        detail,
        to: indexerNeedsButton(indexer) ? "/?tab=system" : "/indexers/indexers"
      };
    });
}

export function clientNodes(lights: readonly ClientLight[]): SourceNode[] {
  return lights.map((light) => {
    const off = light.tone === "idle";
    return {
      key: `client:${light.id}`,
      group: "clients" as const,
      name: light.name,
      sub: clientMeta(light),
      // A client moving data is answering, so it is green; the line and its particles say it is moving.
      tone: tileTone(light.tone === "info" ? "ok" : light.tone),
      off,
      speedMbps: light.moving ? light.speedMbps : 0,
      working: false,
      label: `${light.name}: ${light.words}${light.seeding > 0 ? `, seeding ${light.seeding}` : ""}`,
      detail: [light.words, ...(light.moving || light.seeding > 0 ? [clientMeta(light)] : [])].filter((line, index, all) => all.indexOf(line) === index),
      to: light.tone === "bad" ? "/?tab=system" : "/indexers/download-clients"
    };
  });
}

export function connectionNodes(connections: readonly LiveConnection[]): SourceNode[] {
  return connections.map((connection) => {
    const off = !connection.enabled;
    const words = connectionWords(connection);
    return {
      key: connectionKey(connection),
      group: "connections" as const,
      name: tileName(connection),
      sub: words,
      tone: off ? "idle" : connection.healthy ? "ok" : "bad",
      off,
      speedMbps: 0,
      working: !off && connection.busy > 0,
      label: `${connection.name}: ${words}`,
      detail: [words],
      to: MANAGE[connection.kind]
    };
  });
}

/**
 * One tile height for every group, as tall as the card allows (30 to 54 px, as the mockup), from the card body's measured
 * height. The body is as tall as the three headings, the two dividers, the gaps between rows and its bottom padding plus the
 * rows of tiles; what is left over is spread evenly around the dividers by the stylesheet. A busy install in a short card can
 * need less than 30: a tile then goes down to ONE_LINE (a name alone), and a narrow card that would still not fit in two columns
 * puts three across, so nothing is ever cut off or scrolled.
 */
export const TILE_MIN = 30;
export const TILE_MAX = 54;
export const TILE_FLOOR = 24;
/** A tile shorter than this is one line: the name alone, with its detail in the tooltip. */
export const TILE_TIGHT = 34;
/** A tile shorter than this has no speed bar (a client's two lines and the bar need about 52). */
export const TILE_SNUG = 52;
/** A card narrower than this puts two tiles across instead of three. */
export const NARROW_CARD = 400;

export function tileHeight(bodyHeight: number, columns: number, counts: readonly [number, number, number], floor = TILE_MIN): number {
  const rows = counts.reduce((sum, count) => sum + Math.ceil(count / columns), 0);
  if (bodyHeight <= 0 || rows === 0) return 44;
  const fixed = 3 * 24 + 2 * 21 + (rows - 3) * 7 + 14;
  return Math.max(floor, Math.min(TILE_MAX, Math.floor((bodyHeight - fixed) / rows)));
}

export function tileLayout(bodyWidth: number, bodyHeight: number, counts: readonly [number, number, number]): { columns: number; tile: number } {
  const narrow = bodyWidth > 0 && bodyWidth <= NARROW_CARD;
  const wide = { columns: 3, tile: tileHeight(bodyHeight, 3, counts, TILE_FLOOR) };
  if (!narrow) return wide;
  const two = { columns: 2, tile: tileHeight(bodyHeight, 2, counts, TILE_FLOOR) };
  // Two across is the mockup's narrow rule; it gives way to three only when two would have to shrink under a one-line tile.
  return two.tile >= TILE_MIN || two.tile >= wide.tile ? two : wide;
}
