/**
 * Connections on System (#1098): every indexer, download client, processor, media server, request server, subtitle
 * source, list and Deluno's metadata service as one row with its state (answering, slow, not answering, off), the plain
 * reason, when it was last checked and where its Test button goes. Built from the stored lists the other screens
 * already read, with the newest test result (a person's, or the regular check's pushed as ConnectionChecked) laid over.
 */
import type { DownloadClientItem, IndexerItem } from "../../../lib/api";
import type { DashboardClientRow, LiveConnection } from "../../../lib/api/types/operations";
import type { ConnectionCheckedEvent } from "../../../lib/api/types/system-overview";
import { plural } from "../format";
import { sizeText } from "./logic";

export type ConnKind = "indexer" | "client" | "processor" | "mediaServer" | "requests" | "subtitles" | "lists" | "metadata";
export type ConnState = "ok" | "slow" | "down" | "off";

export interface ConnRow {
  key: string;
  kind: ConnKind;
  id: string;
  name: string;
  state: ConnState;
  sub: string;
  checkedUtc: string | null;
  /** The POST that tests this connection; null when it has no test of its own. */
  testPath: string | null;
}

export interface ConnGroup {
  key: string;
  title: string;
  rows: ConnRow[];
  tone: "green" | "amber" | "red";
  /** "4/5 OK": the answering ones of those switched on. */
  badge: string;
}

/** The newest answer for a connection, laid over what the stored list says. */
export interface ConnResult {
  state: Exclude<ConnState, "off">;
  message: string | null;
  checkedUtc: string;
}

export const GROUPS: ReadonlyArray<{ key: string; title: string; kinds: ConnKind[] }> = [
  { key: "indexers", title: "Indexers", kinds: ["indexer"] },
  { key: "clients", title: "Download clients", kinds: ["client"] },
  { key: "processors", title: "Processors", kinds: ["processor"] },
  { key: "mediaServers", title: "Media servers", kinds: ["mediaServer"] },
  { key: "requests", title: "Requests", kinds: ["requests"] },
  { key: "metadata", title: "Metadata", kinds: ["metadata"] },
  { key: "subtitles", title: "Subtitles", kinds: ["subtitles"] },
  { key: "lists", title: "Lists", kinds: ["lists"] }
];

const ORDER: Record<ConnState, number> = { down: 0, slow: 1, ok: 2, off: 3 };
export const SLOW_MS = 2000;
const FAILING = /unreach|fail|error|unhealth|invalid|denied|auth|refus|timed?-?out/i;

export const connKey = (kind: string, id: string) => `${normaliseKind(kind)}:${id}`;

/** The pushed event's kind in the words used here (the server may say "downloadClient" or "download-client"). */
export function normaliseKind(kind: string): string {
  const k = kind.replace(/[-_ ]/g, "").toLowerCase();
  if (k === "downloadclient" || k === "client") return "client";
  if (k === "mediaserver") return "mediaServer";
  if (k === "seerr" || k === "requests" || k === "request") return "requests";
  if (k === "subtitle" || k === "subtitles" || k === "subtitleprovider") return "subtitles";
  if (k === "list" || k === "lists" || k === "listprovider") return "lists";
  return k;
}

/** Where a person's Test goes for each kind of connection: the endpoints the other screens already use. */
export function testPathFor(kind: ConnKind, id: string): string | null {
  const encoded = encodeURIComponent(id);
  switch (kind) {
    case "indexer": return `/api/indexers/${encoded}/test`;
    case "client": return `/api/download-clients/${encoded}/test`;
    case "processor": return `/api/integrations/processors/connections/${encoded}/test`;
    case "mediaServer": return `/api/media-servers/${encoded}/test`;
    case "requests": return `/api/seerr-servers/${encoded}/test`;
    case "subtitles": return `/api/subtitle-providers/${encoded}/test`;
    default: return null;
  }
}

/** A result from a pushed event. */
export function resultOfEvent(event: ConnectionCheckedEvent): ConnResult {
  return { state: !event.ok ? "down" : event.slow ? "slow" : "ok", message: event.message, checkedUtc: event.checkedUtc };
}

function healthState(status: string | null | undefined, latency: number | null | undefined): Exclude<ConnState, "off"> {
  if (status && FAILING.test(status)) return "down";
  if (status === "degraded" || (latency ?? 0) > SLOW_MS) return "slow";
  return "ok";
}

function reason(state: ConnState, message: string | null | undefined, okText: string, latency?: number | null): string {
  if (state === "off") return "switched off";
  if (state === "down") return message?.trim() || "not answering";
  if (state === "slow") return message?.trim() || (latency ? `slow: about ${(latency / 1000).toFixed(1)} s to answer` : "slow to answer");
  return okText;
}

export interface ConnInputs {
  indexers: readonly IndexerItem[];
  clients: readonly DownloadClientItem[];
  clientRows: readonly DashboardClientRow[];
  connections: readonly LiveConnection[] | null | undefined;
  /** Time to the metadata service from the overview, when it could be read. */
  metadataMs: number | null | undefined;
  results: ReadonlyMap<string, ConnResult>;
  now: number;
}

export function buildConnections(input: ConnInputs): ConnRow[] {
  const rows: ConnRow[] = [];
  const add = (row: Omit<ConnRow, "key" | "testPath"> & { testPath?: string | null }) => {
    const key = connKey(row.kind, row.id);
    // A newer answer wins: one seeded from the stored checks never covers a fresher reading the row already carries.
    const known = row.state === "off" ? undefined : input.results.get(key);
    const result = known && (!row.checkedUtc || Date.parse(known.checkedUtc) >= Date.parse(row.checkedUtc)) ? known : undefined;
    rows.push({
      ...row,
      key,
      state: result ? result.state : row.state,
      sub: result ? reason(result.state, result.message, "answering") : row.sub,
      checkedUtc: result ? result.checkedUtc : row.checkedUtc,
      testPath: row.testPath === undefined ? testPathFor(row.kind, row.id) : row.testPath
    });
  };

  for (const indexer of input.indexers) {
    const state: ConnState = !indexer.isEnabled ? "off" : healthState(indexer.healthStatus, indexer.lastHealthLatencyMs);
    add({ kind: "indexer", id: indexer.id, name: indexer.name, state, sub: reason(state, indexer.lastHealthMessage, indexer.lastHealthTestUtc ? "answering" : "not checked yet", indexer.lastHealthLatencyMs), checkedUtc: indexer.lastHealthTestUtc ?? null });
  }

  const reading = new Map(input.clientRows.map((client) => [client.id, client]));
  for (const client of input.clients) {
    const live = reading.get(client.id);
    const state: ConnState = !client.isEnabled ? "off" : live && !live.reachable ? "down" : healthState(client.healthStatus, client.lastHealthLatencyMs);
    const busy = live ? live.active + live.queued : 0;
    const okText = live && live.reachable ? [busy > 0 ? plural(busy, "item") : "answering", live.freeSpaceBytes ? `${sizeText(live.freeSpaceBytes)} free` : null].filter(Boolean).join(" · ") : "answering";
    add({ kind: "client", id: client.id, name: client.name, state, sub: reason(state, client.lastHealthMessage, okText, client.lastHealthLatencyMs), checkedUtc: client.lastHealthTestUtc ?? null });
  }

  for (const connection of input.connections ?? []) {
    const kind = normaliseKind(connection.kind) as ConnKind;
    const state: ConnState = !connection.enabled ? "off" : connection.healthy ? "ok" : "down";
    add({ kind, id: connection.id, name: connection.name, state, sub: reason(state, null, "answering"), checkedUtc: null });
  }

  if (input.metadataMs !== null && input.metadataMs !== undefined) {
    const state: ConnState = input.metadataMs > SLOW_MS ? "slow" : "ok";
    add({ kind: "metadata", id: "metadata", name: "Deluno metadata", state, sub: state === "slow" ? "slow to answer" : "TMDb and TheTVDB through Deluno’s service", checkedUtc: new Date(input.now).toISOString(), testPath: null });
  }
  return rows;
}

const byProblems = (a: ConnRow, b: ConnRow) => ORDER[a.state] - ORDER[b.state] || a.name.localeCompare(b.name);

/** The rows in their groups, the worst of each group first; groups with nothing in them are left out. */
export function groupConnections(rows: readonly ConnRow[]): ConnGroup[] {
  return GROUPS.map((group) => {
    const own = rows.filter((row) => group.kinds.includes(row.kind)).sort(byProblems);
    const on = own.filter((row) => row.state !== "off").length;
    const ok = own.filter((row) => row.state === "ok").length;
    return { key: group.key, title: group.title, rows: own, tone: own.some((row) => row.state === "down") ? "red" : own.some((row) => row.state === "slow") ? "amber" : "green", badge: `${ok}/${on} OK` } satisfies ConnGroup;
  }).filter((group) => group.rows.length > 0);
}

/** "12/14 answering · 1 slow · 1 down". */
export function connectionsLine(rows: readonly ConnRow[]): string {
  const on = rows.filter((row) => row.state !== "off");
  const down = on.filter((row) => row.state === "down").length;
  const slow = on.filter((row) => row.state === "slow").length;
  if (on.length === 0) return rows.length === 0 ? "nothing connected yet" : "everything is switched off";
  return `${on.length - down}/${on.length} answering${slow ? ` · ${slow} slow` : ""}${down ? ` · ${down} down` : ""}`;
}

/** What a test answered, in the words of the rows: the server says healthy, degraded or something that fails. */
export function resultOfTest(response: { healthStatus?: string; message?: string } | null, nowIso: string): ConnResult {
  const state = healthState(response?.healthStatus ?? "healthy", null);
  return { state, message: response?.message ?? null, checkedUtc: nowIso };
}
