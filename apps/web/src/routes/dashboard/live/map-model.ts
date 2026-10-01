/**
 * The Sources map (#1098): what is drawn, and where. The nodes (one per indexer, download client and connection) come from
 * the same readings the old tiles used, so their words did not change; the layout puts Deluno in the middle, the indexers in a
 * list on the left and the download clients and connections in lists on the right. Nothing here draws or animates; it is
 * plain functions, tested on their own.
 */
import type { IndexerItem } from "../../../lib/api";
import type { LiveConnection } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { indexerNeedsButton } from "../attention";
import { clientMeta, type ClientLight } from "./clients";
import { connectionKey, connectionWords, tileName, type LiveConnectionKind } from "./connections";
import { indexerMeta, type IndexerToday } from "./indexers";

export type MapGroup = "indexers" | "clients" | "connections";

/** A node's status: green answering, amber slow or paused, red not answering, grey switched off. */
export type NodeTone = "ok" | "warn" | "bad" | "idle";

export interface MapNode {
  /** Unique across the map: `indexer:<id>`, `client:<id>`, or the connection's own key. */
  key: string;
  group: MapGroup;
  name: string;
  /** The small muted words under the name: "46 of 100 today · 320 ms", "6.8 MB/s · 8 seeding", "Processor · working on 1". */
  sub: string;
  tone: NodeTone;
  off: boolean;
  /** A client moving data now, and how fast (MB/s). */
  speedMbps: number;
  /** The accessible name, unique among the nodes: "NZBgeek: 46 of 100 today · 320 ms". */
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

function nodeTone(tone: Tone): NodeTone {
  return tone === "bad" ? "bad" : tone === "warn" ? "warn" : tone === "idle" ? "idle" : "ok";
}

export function indexerNodes(indexers: readonly IndexerItem[], today: ReadonlyMap<string, IndexerToday>, now: number, agoOf: (iso: string) => string): MapNode[] {
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
        tone: nodeTone(meta.tone),
        off: !indexer.isEnabled,
        speedMbps: 0,
        label: `${indexer.name}: ${meta.text}`,
        detail,
        to: indexerNeedsButton(indexer) ? "/?tab=system" : "/indexers/indexers"
      };
    });
}

export function clientNodes(lights: readonly ClientLight[]): MapNode[] {
  return lights.map((light) => {
    const off = light.tone === "idle";
    return {
      key: `client:${light.id}`,
      group: "clients" as const,
      name: light.name,
      sub: clientMeta(light),
      // A client moving data is answering, so it is green; the line and its particles say it is moving.
      tone: nodeTone(light.tone === "info" ? "ok" : light.tone),
      off,
      speedMbps: light.moving ? light.speedMbps : 0,
      label: `${light.name}: ${light.words}${light.seeding > 0 ? `, seeding ${light.seeding}` : ""}`,
      detail: [light.words, ...(light.moving || light.seeding > 0 ? [clientMeta(light)] : [])].filter((line, index, all) => all.indexOf(line) === index),
      to: light.tone === "bad" ? "/?tab=system" : "/indexers/download-clients"
    };
  });
}

export function connectionNodes(connections: readonly LiveConnection[]): MapNode[] {
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
      label: `${connection.name}: ${words}`,
      detail: [words],
      to: MANAGE[connection.kind]
    };
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Layout

export interface Point { x: number; y: number }

/** A line from Deluno to a node's dot: a quadratic curve. */
export interface Curve { from: Point; control: Point; to: Point }

export type MapSide = "left" | "right" | "stack";

export interface PlacedNode extends MapNode {
  /** Which list the row is in. */
  side: MapSide;
  /** The row, in its list's own coordinates (the list scrolls when there are too many rows to fit). */
  top: number;
  height: number;
  /** Rows too short for two lines of words put the name and the small words on one line. */
  compact: boolean;
  /** Where the row's status dot is on the card, with the list's scroll taken into account, and the line from Deluno to it. */
  x: number;
  y: number;
  curve: Curve;
}

export interface MapColumn {
  side: MapSide;
  left: number;
  top: number;
  width: number;
  height: number;
  /** The height of everything in the list; more than `height` means it scrolls. */
  innerHeight: number;
  /** The height of each heading: two lines of counts when the list is narrow. */
  headH: number;
  narrow: boolean;
  heads: { group: MapGroup; top: number }[];
}

export interface MapLayout {
  width: number;
  height: number;
  /** "fan" is the lists either side of Deluno; "stack" is one list with no hub, for a phone. */
  mode: "fan" | "stack";
  hub: Point & { r: number };
  columns: MapColumn[];
  nodes: PlacedNode[];
}

const PAD = 12;
const GROUP_GAP = 12;
const ROW_GAP = 4;
const MIN_PITCH = 20;
const MAX_PITCH = 46;
/** A row this tall holds two lines of words; shorter ones put the name and the small words on one line. */
const TWO_LINES = 28;
const STACK_BELOW = 300;
/** Under this a list is narrow: its heading gets two lines for its counts and its rows drop the small words. */
export const NARROW_COLUMN = 190;

/** A point on the quadratic curve at `t`. */
export function pointOn(curve: Curve, t: number): Point {
  const u = 1 - t;
  return {
    x: u * u * curve.from.x + 2 * u * t * curve.control.x + t * t * curve.to.x,
    y: u * u * curve.from.y + 2 * u * t * curve.control.y + t * t * curve.to.y
  };
}

/** A smooth line from Deluno to a dot: it arrives level, like the strands of a fan. */
export function curveBetween(from: Point, to: Point): Curve {
  return { from, control: { x: from.x + (to.x - from.x) * 0.6, y: to.y }, to };
}

const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));

/**
 * Lays the map out: the indexers in a list on the left, the download clients and the connections in lists on the right, Deluno
 * between them. Rows share the card's height (a row is 20 to 46 px, and a list with more rows than fit scrolls inside its
 * column); the middle column narrows on a narrow card; on a phone (or under 300 px) the lists stack and there is no hub. `scroll` is how
 * far each list has scrolled, so a line follows its row (a row scrolled out of sight ends its line at the list's edge).
 */
export function layoutMap(width: number, height: number, nodes: readonly MapNode[], scroll: { left?: number; right?: number; stack?: number } = {}, phone = false): MapLayout {
  const W = Math.max(1, width);
  const H = Math.max(1, height);
  const colH = Math.max(1, H - PAD * 2);
  const stack = W < STACK_BELOW || phone;
  const members = (group: MapGroup) => nodes.filter((node) => node.group === group);
  const placed: PlacedNode[] = [];
  const columns: MapColumn[] = [];
  const hubW = stack ? 0 : clamp(W * 0.16, 56, 120);
  const colW = stack ? W - PAD * 2 : Math.max(60, (W - PAD * 2 - hubW) / 2);
  const hub = { x: W / 2, y: H / 2, r: clamp(Math.min(hubW / 2 - 8, H * 0.07), 15, 28) };
  const narrow = colW < NARROW_COLUMN;
  const headH = narrow ? 46 : 36;

  const sizeOf = (groups: readonly MapGroup[], fixedPitch: number | null) => {
    const count = groups.reduce((sum, group) => sum + members(group).length, 0);
    const heads = groups.length * headH + (groups.length - 1) * GROUP_GAP;
    const pitch = fixedPitch ?? clamp((colH - heads) / Math.max(1, count), MIN_PITCH, MAX_PITCH);
    return { pitch, inner: heads + count * pitch };
  };
  const leftGroups: MapGroup[] = ["indexers"];
  const rightGroups: MapGroup[] = ["clients", "connections"];
  const sizes = stack ? [sizeOf(["indexers", "clients", "connections"], 40)] : [sizeOf(leftGroups, null), sizeOf(rightGroups, null)];
  // A block that is shorter than the card sits in the middle of it, so Deluno is level with the middle of the lists.
  const offset = Math.max(0, Math.floor((colH - Math.max(...sizes.map((size) => size.inner))) / 2));

  const build = (side: MapSide, left: number, groups: readonly MapGroup[], size: { pitch: number; inner: number }) => {
    const { pitch } = size;
    const top0 = PAD + offset;
    const scrolled = scroll[side] ?? 0;
    const placedHeads: MapColumn["heads"] = [];
    let cursor = 0;
    for (const group of groups) {
      placedHeads.push({ group, top: cursor });
      cursor += headH;
      members(group).forEach((node, index) => {
        const top = cursor + index * pitch;
        const rowH = pitch - ROW_GAP;
        const dotX = side === "left" ? left + colW - 12 : left + 12;
        const dotY = clamp(top0 + top + rowH / 2 - scrolled, PAD, PAD + colH);
        placed.push({ ...node, side, top, height: rowH, compact: rowH < TWO_LINES, x: dotX, y: dotY, curve: curveBetween({ x: hub.x, y: hub.y }, { x: dotX, y: dotY }) });
      });
      cursor += members(group).length * pitch + GROUP_GAP;
    }
    const innerHeight = Math.max(0, cursor - GROUP_GAP);
    columns.push({ side, left, top: top0, width: colW, height: offset > 0 ? innerHeight : colH, innerHeight, headH, narrow, heads: placedHeads });
  };

  if (stack) build("stack", PAD, ["indexers", "clients", "connections"], sizes[0]);
  else {
    build("left", PAD, leftGroups, sizes[0]);
    build("right", W - PAD - colW, rightGroups, sizes[1]);
  }
  return { width: W, height: H, mode: stack ? "stack" : "fan", hub, columns, nodes: placed };
}

/** How the particles of a moving client run: how many a second and how long each takes, scaled to its MB/s (about 0.5 to 40). */
export function flowOf(mbps: number): { perSecond: number; travelMs: number } {
  const t = Math.max(0, Math.min(1, Math.log10(1 + Math.max(0, mbps)) / Math.log10(41)));
  return { perSecond: 1.2 + t * 8, travelMs: 1900 - t * 1200 };
}
