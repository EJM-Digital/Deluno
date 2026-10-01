/**
 * The Sources map (#1098): what is drawn, and where. The nodes (one per indexer, download client and connection) come from
 * the same readings the old tiles used, so their words did not change; the layout puts Deluno in the middle, the indexers on
 * an arc across the top, the download clients on the lower left and the connections on the lower right. Nothing here draws
 * or animates; it is plain functions, tested on their own.
 */
import type { IndexerItem } from "../../../lib/api";
import type { LiveConnection } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { indexerNeedsButton } from "../attention";
import { formatSpeed } from "../downloads-kit";
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
  /** The one small number: queries today, speed or seeding, or today's count. */
  number: string;
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

const fmt = (n: number) => n.toLocaleString("en-AU");

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
        number: indexer.isEnabled ? fmt(row?.totalQueries ?? 0) : "off",
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
    const number = off ? "off" : light.moving ? formatSpeed(light.speedMbps) : light.seeding > 0 ? `${fmt(light.seeding)} seeding` : "idle";
    return {
      key: `client:${light.id}`,
      group: "clients" as const,
      name: light.name,
      number,
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
    const count = connection.kind === "processor" && connection.busy > 0 ? connection.busy : connection.countToday;
    return {
      key: connectionKey(connection),
      group: "connections" as const,
      name: tileName(connection),
      number: off ? "off" : fmt(count),
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

/** A line from Deluno to a node: a quadratic curve. */
export interface Curve { from: Point; control: Point; to: Point }

export interface PlacedNode extends MapNode {
  x: number;
  y: number;
  r: number;
  curve: Curve;
  /**
   * The label runs along the line out from Deluno, so crowded nodes never share a line of text: the (truncated) name and the
   * number, set at `x, y` and turned by `rotate` degrees. `anchor` is where the text starts (right half) or ends (left half).
   */
  text: { x: number; y: number; rotate: number; anchor: "start" | "end"; name: string; number: string; numberFirst: boolean };
}

export interface GroupLabel { group: MapGroup; x: number; y: number; anchor: "start" | "end"; }

export interface MapLayout {
  width: number;
  height: number;
  hub: Point & { r: number };
  nodes: PlacedNode[];
  groups: GroupLabel[];
}

/** The arc each group takes, in degrees clockwise from the right (y points down): across the top, the lower left and the lower right. */
const ARC: Record<MapGroup, [number, number]> = {
  indexers: [-186, 6],
  clients: [160, 104],
  connections: [26, 74]
};

const RAD = Math.PI / 180;

/** A point on the quadratic curve at `t`. */
export function pointOn(curve: Curve, t: number): Point {
  const u = 1 - t;
  return {
    x: u * u * curve.from.x + 2 * u * t * curve.control.x + t * t * curve.to.x,
    y: u * u * curve.from.y + 2 * u * t * curve.control.y + t * t * curve.to.y
  };
}

export function curveBetween(from: Point, to: Point, bend: number): Curve {
  const mid = { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2 };
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  // Bend each line to one side of the straight path, a little more the longer it is, so the fan reads as curves.
  return { from, control: { x: mid.x - dy * bend, y: mid.y + dx * bend }, to };
}

const CHAR_W = 6.1;
const NUMBER_CHAR_W = 5.4;
/** The bands at the top and bottom of the card that hold the group labels; no node's label runs into them. */
const BAND = 44;

function clip(name: string, chars: number): string {
  if (name.length <= chars) return name;
  return `${name.slice(0, Math.max(1, chars - 1)).trimEnd()}…`;
}

/** How far a ray from (x, y) in direction (dx, dy) goes before it leaves the rectangle. */
function rayRoom(x: number, y: number, dx: number, dy: number, W: number, H: number): number {
  const tx = dx > 0.001 ? (W - 4 - x) / dx : dx < -0.001 ? (x - 4) / -dx : Infinity;
  const ty = dy > 0.001 ? (H - BAND - y) / dy : dy < -0.001 ? (y - BAND) / -dy : Infinity;
  return Math.max(0, Math.min(tx, ty));
}

/**
 * Puts the nodes of every group on its arc around Deluno. Circles are 18 to 26 px across (radius 9 to 13), smaller when an arc
 * is crowded. Each node's label runs outward along its line, so crowded arcs stay readable; the group labels sit in the bands
 * at the top and bottom.
 */
export function layoutMap(width: number, height: number, nodes: readonly MapNode[]): MapLayout {
  const W = Math.max(1, width);
  const H = Math.max(1, height);
  const cx = W / 2;
  const cy = H / 2;
  const hubR = Math.max(17, Math.min(28, Math.min(W, H) * 0.07));
  const marginY = BAND + Math.min(92, H * 0.19);
  const rx = Math.max(hubR + 34, W / 2 - Math.min(104, W * 0.25));
  const ry = Math.max(hubR + 26, Math.min(H / 2 - marginY, rx * 1.5));
  const placed: PlacedNode[] = [];
  const meanR = (rx + ry) / 2;
  const chordOf = (group: MapGroup) => {
    const n = nodes.filter((node) => node.group === group).length;
    const [from, to] = ARC[group];
    return n > 1 ? (Math.abs(to - from) * RAD * meanR) / (n - 1) : Infinity;
  };
  // One circle size for every node, set by the most crowded arc.
  const r = Math.max(9, Math.min(13, Math.min(chordOf("indexers"), chordOf("clients"), chordOf("connections"), 40) * 0.36));
  for (const group of ["indexers", "clients", "connections"] as const) {
    const members = nodes.filter((node) => node.group === group);
    const [from, to] = ARC[group];
    const n = members.length;
    members.forEach((node, index) => {
      const angle = (n === 1 ? (from + to) / 2 : from + ((to - from) * index) / (n - 1)) * RAD;
      const dirX = Math.cos(angle);
      const dirY = Math.sin(angle);
      const x = cx + dirX * rx;
      const y = cy + dirY * ry;
      // The label runs out along the line from Deluno, as far as the card allows.
      const left = dirX < -0.001;
      const labelX = x + dirX * (r + 5);
      const labelY = y + dirY * (r + 5);
      const room = rayRoom(labelX, labelY, dirX, dirY, W, H) - 2;
      const numberWidth = (node.number.length + 1) * NUMBER_CHAR_W;
      const chars = Math.max(4, Math.floor((room - numberWidth) / CHAR_W));
      const curve = curveBetween({ x: cx, y: cy }, { x, y }, 0.14);
      placed.push({
        ...node,
        x,
        y,
        r,
        curve,
        text: { x: labelX, y: labelY, rotate: (angle / RAD + (left ? 180 : 0)), anchor: left ? "end" : "start", name: clip(node.name, chars), number: node.number, numberFirst: left }
      });
    });
  }
  return {
    width: W,
    height: H,
    hub: { x: cx, y: cy, r: hubR },
    nodes: placed,
    groups: [
      { group: "indexers", x: 14, y: 20, anchor: "start" },
      { group: "clients", x: 14, y: H - 24, anchor: "start" },
      { group: "connections", x: W - 14, y: H - 24, anchor: "end" }
    ]
  };
}

/** How the particles of a moving client run: how many a second and how long each takes, scaled to its MB/s (about 0.5 to 40). */
export function flowOf(mbps: number): { perSecond: number; travelMs: number } {
  const t = Math.max(0, Math.min(1, Math.log10(1 + Math.max(0, mbps)) / Math.log10(41)));
  return { perSecond: 1.2 + t * 8, travelMs: 1900 - t * 1200 };
}
