/**
 * The download clients as lights with a live speed bar each (#1096), beside the
 * indexer lights. Green when it answers and nothing is moving, blue and pulsing
 * while it is moving data (the bar is its speed against the fastest the day has
 * seen), amber when its own pause is on, red when it does not answer, grey when
 * it is switched off. The speed follows the readings the clients push; nothing
 * here asks a client while the screen waits.
 */
import { useMemo } from "react";
import { Link } from "react-router-dom";
import { StatusLed } from "../../../components/ui/status-led";
import type { DownloadClientItem } from "../../../lib/api";
import type { DashboardClientRow } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { formatSpeed } from "../downloads-kit";
import { shortNamer } from "./journey";

export interface ClientLight {
  id: string;
  name: string;
  tone: Tone;
  moving: boolean;
  /** Plain words for the tooltip and the accessible name. */
  words: string;
  speedMbps: number;
}

/** One light for each client: the ones the summary reads, and any that are switched off. */
export function clientLights(rows: DashboardClientRow[], all: DownloadClientItem[], speeds: ReadonlyMap<string, number>): ClientLight[] {
  const short = shortNamer([...rows.map((row) => row.name), ...all.map((client) => client.name)]);
  const byId = new Map(rows.map((row) => [row.id, row]));
  const lights: ClientLight[] = [];
  for (const client of all) {
    const row = byId.get(client.id);
    const name = short(client.name) ?? client.name;
    if (!client.isEnabled) {
      lights.push({ id: client.id, name, tone: "idle", moving: false, words: "switched off", speedMbps: 0 });
      continue;
    }
    const speed = speeds.get(client.id) ?? 0;
    if (row && !row.reachable) lights.push({ id: client.id, name, tone: "bad", moving: false, words: "not answering", speedMbps: 0 });
    else if (row?.paused) lights.push({ id: client.id, name, tone: "warn", moving: false, words: "paused", speedMbps: 0 });
    else if (speed > 0) lights.push({ id: client.id, name, tone: "info", moving: true, words: `moving ${formatSpeed(speed)}`, speedMbps: speed });
    else lights.push({ id: client.id, name, tone: "ok", moving: false, words: row && row.active > 0 ? "connected, nothing moving" : "connected, idle", speedMbps: 0 });
  }
  // A client the summary reads that the list does not hold yet is still shown.
  for (const row of rows) {
    if (all.some((client) => client.id === row.id)) continue;
    const speed = speeds.get(row.id) ?? 0;
    const name = short(row.name) ?? row.name;
    lights.push(!row.reachable ? { id: row.id, name, tone: "bad", moving: false, words: "not answering", speedMbps: 0 } : { id: row.id, name, tone: speed > 0 ? "info" : "ok", moving: speed > 0, words: speed > 0 ? `moving ${formatSpeed(speed)}` : "connected, idle", speedMbps: speed });
  }
  return lights.sort((a, b) => Number(b.moving) - Number(a.moving) || a.name.localeCompare(b.name));
}

const MAX_CLIENTS = 6;

export function ClientLights({ rows, all, speeds, scaleMbps }: { rows: DashboardClientRow[]; all: DownloadClientItem[]; speeds: ReadonlyMap<string, number>; scaleMbps: number }) {
  const lights = useMemo(() => clientLights(rows, all, speeds), [rows, all, speeds]);
  const shown = lights.slice(0, MAX_CLIENTS);
  const hidden = lights.length - shown.length;
  const scale = Math.max(scaleMbps, ...lights.map((light) => light.speedMbps), 1);
  return (
    <section aria-label="Download clients" className="flex min-h-0 min-w-0 flex-col px-[var(--card-pad-x)] pb-2.5 pt-2">
      <div className="flex items-baseline justify-between gap-2">
        <Link to="/indexers/download-clients" aria-label="Manage: Download clients" className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">Download clients</Link>
      </div>
      {lights.length === 0 ? (
        <p className="mt-1 text-[length:var(--type-body-sm)] text-muted-foreground">No download client is set up, so nothing can be downloaded.</p>
      ) : (
        <ul className="mt-1.5 grid grid-cols-[repeat(auto-fill,minmax(8.5rem,1fr))] gap-x-3 gap-y-1.5">
          {shown.map((light) => (
            <li key={light.id}>
              <Link
                to={light.tone === "bad" ? "/?tab=system" : "/indexers/download-clients"}
                title={`${light.name}: ${light.words}.`}
                aria-label={`${light.name}: ${light.words}`}
                className="block rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <span className="flex items-center gap-1.5 text-[length:var(--type-caption)] text-foreground">
                  <StatusLed tone={light.tone} size={7} pulse={light.moving ? motionAllowed() : light.tone === "bad" || light.tone === "warn"} />
                  <span className="min-w-0 flex-1 truncate">{light.name}</span>
                  <span className={cn("shrink-0 tabular-nums", light.moving ? "text-info" : "text-muted-foreground")}>{light.moving ? formatSpeed(light.speedMbps) : light.tone === "idle" ? "off" : light.tone === "bad" ? "down" : light.tone === "warn" ? "paused" : "idle"}</span>
                </span>
                <span aria-hidden className="mt-0.5 block h-1 overflow-hidden rounded-full bg-surface-3">
                  <span className={cn("live-gauge block h-full rounded-full", light.tone === "bad" ? "bg-destructive" : light.tone === "warn" ? "bg-warning" : "bg-info", light.moving && "live-shimmer")} style={{ width: `${light.moving ? Math.max(4, Math.min(100, (light.speedMbps / scale) * 100)) : 0}%` }} />
                </span>
              </Link>
            </li>
          ))}
          {hidden > 0 ? (
            <li className="self-center">
              <Link to="/indexers/download-clients" className="text-[length:var(--type-caption)] font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">and {hidden.toLocaleString("en-AU")} more</Link>
            </li>
          ) : null}
        </ul>
      )}
    </section>
  );
}
