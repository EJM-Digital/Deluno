/**
 * The download clients as lights with a live speed bar each (#1096), beside the
 * indexer lights. Green when it answers and nothing is moving, blue and pulsing
 * while it is moving data (the bar is its speed against the fastest the day has
 * seen), amber when its own pause is on, red when it does not answer, grey when
 * it is switched off. The speed follows the readings the clients push; nothing
 * here asks a client while the screen waits.
 */
import { useMemo } from "react";
import type { DownloadClientItem } from "../../../lib/api";
import type { DashboardClientRow } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { formatSpeed } from "../downloads-kit";
import { shortNamer } from "./journey";
import { Tile, TileGroup } from "./tile";

export interface ClientLight {
  id: string;
  name: string;
  tone: Tone;
  moving: boolean;
  /** Plain words for the tooltip and the accessible name. */
  words: string;
  speedMbps: number;
  /** Torrents this client is seeding that Deluno sent. */
  seeding: number;
}

/** One light for each client: the ones the summary reads, and any that are switched off. */
export function clientLights(rows: DashboardClientRow[], all: DownloadClientItem[], speeds: ReadonlyMap<string, number>, seeding: ReadonlyMap<string, number> = new Map()): ClientLight[] {
  const short = shortNamer([...rows.map((row) => row.name), ...all.map((client) => client.name)]);
  const byId = new Map(rows.map((row) => [row.id, row]));
  const lights: ClientLight[] = [];
  for (const client of all) {
    const row = byId.get(client.id);
    const name = short(client.name) ?? client.name;
    if (!client.isEnabled) {
      lights.push({ id: client.id, name, tone: "idle", moving: false, words: "switched off", speedMbps: 0, seeding: seeding.get(client.id) ?? 0 });
      continue;
    }
    const speed = speeds.get(client.id) ?? 0;
    if (row && !row.reachable) lights.push({ id: client.id, name, tone: "bad", moving: false, words: "not answering", speedMbps: 0, seeding: seeding.get(client.id) ?? 0 });
    else if (row?.paused) lights.push({ id: client.id, name, tone: "warn", moving: false, words: "paused", speedMbps: 0, seeding: seeding.get(client.id) ?? 0 });
    else if (speed > 0) lights.push({ id: client.id, name, tone: "info", moving: true, words: `moving ${formatSpeed(speed)}`, speedMbps: speed, seeding: seeding.get(client.id) ?? 0 });
    else lights.push({ id: client.id, name, tone: "ok", moving: false, words: row && row.active > 0 ? "connected, nothing moving" : "connected, idle", speedMbps: 0, seeding: seeding.get(client.id) ?? 0 });
  }
  // A client the summary reads that the list does not hold yet is still shown.
  for (const row of rows) {
    if (all.some((client) => client.id === row.id)) continue;
    const speed = speeds.get(row.id) ?? 0;
    const name = short(row.name) ?? row.name;
    lights.push(!row.reachable ? { id: row.id, name, tone: "bad", moving: false, words: "not answering", speedMbps: 0, seeding: seeding.get(row.id) ?? 0 } : { id: row.id, name, tone: speed > 0 ? "info" : "ok", moving: speed > 0, words: speed > 0 ? `moving ${formatSpeed(speed)}` : "connected, idle", speedMbps: speed, seeding: seeding.get(row.id) ?? 0 });
  }
  return lights.sort((a, b) => Number(b.moving) - Number(a.moving) || a.name.localeCompare(b.name));
}

/** The header: "3 moving · 14 seeding". */
export function clientsSummary(lights: readonly ClientLight[]): string {
  const moving = lights.filter((light) => light.moving).length;
  const seeding = lights.reduce((sum, light) => sum + light.seeding, 0);
  return `${moving.toLocaleString("en-AU")} moving · ${seeding.toLocaleString("en-AU")} seeding`;
}

/** A client's second line: its speed while it moves, and how many torrents it is seeding. */
export function clientMeta(light: ClientLight): string {
  const seeding = light.seeding > 0 ? `${light.seeding.toLocaleString("en-AU")} seeding` : null;
  if (light.moving) return [formatSpeed(light.speedMbps), seeding].filter(Boolean).join(" · ");
  if (light.tone === "idle") return "switched off";
  if (light.tone === "bad") return "not answering";
  if (light.tone === "warn") return "paused";
  return seeding ?? "idle";
}

/** The download clients as tiles, one each: speed while moving with a bar against the fastest the day has seen, and how many it is seeding. */
export function ClientTiles({ rows, all, speeds, scaleMbps, seeding }: { rows: DashboardClientRow[]; all: DownloadClientItem[]; speeds: ReadonlyMap<string, number>; scaleMbps: number; seeding: ReadonlyMap<string, number> }) {
  const lights = useMemo(() => clientLights(rows, all, speeds, seeding), [rows, all, speeds, seeding]);
  const scale = Math.max(scaleMbps, ...lights.map((light) => light.speedMbps), 1);
  return (
    <TileGroup title="Download clients" count={clientsSummary(lights)} to="/indexers/download-clients" empty="No download client is set up, so nothing can be downloaded.">
      {lights.map((light) => (
        <Tile
          key={light.id}
          to={light.tone === "bad" ? "/?tab=system" : "/indexers/download-clients"}
          name={light.name}
          tone={light.tone}
          state={light.tone === "idle" ? "off" : null}
          pulse={light.moving}
          meta={clientMeta(light)}
          bar={light.moving ? Math.max(4, (light.speedMbps / scale) * 100) : 0}
          label={`${light.name}: ${light.words}${light.seeding > 0 ? `, seeding ${light.seeding}` : ""}`}
        />
      ))}
    </TileGroup>
  );
}
