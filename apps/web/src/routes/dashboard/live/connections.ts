/**
 * Connections (#1098): everything else Deluno is connected to, one tile each with
 * its role and what it did today. The server lists them; `ConnectionUsed` carries
 * the new count when Deluno uses one, and the tile lights up. A pushed count is only
 * trusted over the stored one when it is newer than it.
 */
import type { LiveConnection } from "../../../lib/api/types/operations";
import type { ConnectionUsedEvent } from "../../../lib/api/types/live-events";

export type LiveConnectionKind = LiveConnection["kind"];

export const ROLE_LABEL: Record<LiveConnectionKind, string> = {
  processor: "Processor",
  mediaServer: "Media server",
  requests: "Requests",
  subtitles: "Subtitles",
  lists: "Lists"
};

/** The order tiles are drawn in: processors, then the media server, requests, subtitles and lists. */
export const KIND_ORDER: LiveConnectionKind[] = ["processor", "mediaServer", "requests", "subtitles", "lists"];

/** What the connection did today, in plain words ("working on 1", "5 sent today"). */
export function didToday(connection: Pick<LiveConnection, "kind" | "countToday" | "busy">): string {
  const n = connection.countToday.toLocaleString("en-AU");
  switch (connection.kind) {
    case "processor":
      return connection.busy > 0 ? `working on ${connection.busy.toLocaleString("en-AU")}` : "idle";
    case "mediaServer":
      return `${n} sent today`;
    case "requests":
      return `${n} today`;
    case "subtitles":
      return `${n} fetched today`;
    case "lists":
      return `watching ${n}`;
  }
}

/** The tile's second line: "Processor · working on 1". A switched-off or silent connection says so instead. */
export function connectionWords(connection: LiveConnection): string {
  if (!connection.enabled) return `${ROLE_LABEL[connection.kind]} · switched off`;
  if (!connection.healthy) return `${ROLE_LABEL[connection.kind]} · not answering`;
  return `${ROLE_LABEL[connection.kind]} · ${didToday(connection)}`;
}

/** Tiles in the order they are drawn: by role, then by name; switched-off ones last. */
export function sortConnections(connections: readonly LiveConnection[]): LiveConnection[] {
  return [...connections].sort((a, b) => Number(b.enabled) - Number(a.enabled) || KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind) || a.name.localeCompare(b.name));
}

/** The header: "6 connected · all answering", with how many are not answering or switched off. */
export function connectionsSummary(connections: readonly LiveConnection[]): string {
  const on = connections.filter((connection) => connection.enabled);
  if (connections.length === 0) return "nothing connected yet";
  const down = on.filter((connection) => !connection.healthy).length;
  const off = connections.length - on.length;
  return [`${on.length.toLocaleString("en-AU")} connected`, down > 0 ? `${down.toLocaleString("en-AU")} not answering` : on.length > 0 ? "all answering" : null, off > 0 ? `${off.toLocaleString("en-AU")} off` : null].filter(Boolean).join(" · ");
}

export const connectionKey = (connection: Pick<LiveConnection, "kind" | "id">) => `${connection.kind}:${connection.id}`;

/** The stored list with the newest pushed counts laid over it. A connection the list does not hold yet is added. */
export function applyConnectionEvents(base: readonly LiveConnection[], used: ReadonlyMap<string, ConnectionUsedEvent>, asOfUtc: string | null | undefined): LiveConnection[] {
  const since = Date.parse(asOfUtc ?? "");
  const out = base.map((connection) => {
    const event = used.get(connectionKey(connection));
    if (!event) return connection;
    const at = Date.parse(event.atUtc);
    if (Number.isFinite(since) && Number.isFinite(at) && at <= since) return connection;
    return { ...connection, countToday: event.countToday, busy: event.busy };
  });
  for (const event of used.values()) {
    if (base.some((connection) => connectionKey(connection) === connectionKey(event))) continue;
    out.push({ kind: event.kind, id: event.id, name: event.name, enabled: true, healthy: true, countToday: event.countToday, busy: event.busy });
  }
  return out;
}

/**
 * The name a tile shows. Deluno names a connection after what it is and where it runs ("Processor on WIN-PC"); the host is
 * in the tile's tooltip, and the tile says the name the person would use. A name that is only the role ("Processor") is not said again in the line under it.
 */
export function tileName(connection: Pick<LiveConnection, "name">): string {
  const named = connection.name.replace(/\s+on\s+\S+$/i, "").trim();
  return named || connection.name;
}
