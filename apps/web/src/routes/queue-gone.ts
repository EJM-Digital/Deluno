import { useCallback, useEffect, useMemo, useState } from "react";

/**
 * Rows a person has acted on and the server has accepted (#1538). A row leaves the screen on the success reply and is held off
 * it, so a read that began before the action cannot draw it again. The hold ends as soon as a list that is loaded afterwards
 * no longer names the row (the server agrees) or names a download that was added after the action (it came back as a new
 * item), and in any case after this long, so a real reappearance is always shown.
 */
export const GONE_HOLD_MS = 90_000;

export const goneKeys = {
  download: (clientId: string, id: string) => `queue:${clientId}:${id}`.toLowerCase(),
  finding: (clientId: string, id: string, kind: string) => `finding:${clientId}:${id}:${kind}`.toLowerCase(),
  held: (historyId: string) => `held:${historyId}`,
  handoff: (id: string) => `handoff:${id}`,
  dispatch: (id: string) => `dispatch:${id}`,
  job: (id: string) => `job:${id}`,
  recovery: (mediaType: string, id: string) => `recovery:${mediaType}:${id}`,
  orphan: (path: string) => `orphan:${path}`
};

export interface GoneRows {
  /** True while the row is held off the screen. */
  has: (key: string) => boolean;
  /** The server accepted what was done to these rows. */
  mark: (...keys: string[]) => void;
  /** A newly loaded list: ends the hold on every row it no longer names, or names as a download added since. */
  settle: (listed: ReadonlyMap<string, number | null>) => void;
}

export function useGoneRows(): GoneRows {
  const [held, setHeld] = useState<ReadonlyMap<string, number>>(new Map());
  const mark = useCallback((...keys: string[]) => {
    if (keys.length === 0) return;
    const at = Date.now();
    setHeld((current) => {
      const next = new Map(current);
      for (const key of keys) next.set(key, at);
      return next;
    });
  }, []);
  const settle = useCallback((listed: ReadonlyMap<string, number | null>) => {
    setHeld((current) => {
      let next: Map<string, number> | null = null;
      for (const [key, at] of current) {
        const addedMs = listed.get(key);
        if (addedMs === undefined || (addedMs !== null && addedMs > at)) {
          next ??= new Map(current);
          next.delete(key);
        }
      }
      return next ?? current;
    });
  }, []);
  // The hold ends by itself, so a row that is back for good is not hidden for the life of the page.
  useEffect(() => {
    if (held.size === 0) return;
    const oldest = Math.min(...held.values());
    const timer = setTimeout(() => {
      const cutoff = Date.now() - GONE_HOLD_MS;
      setHeld((current) => new Map([...current].filter(([, at]) => at > cutoff)));
    }, Math.max(0, oldest + GONE_HOLD_MS - Date.now()) + 50);
    return () => clearTimeout(timer);
  }, [held]);
  return useMemo(() => ({ has: (key: string) => held.has(key), mark, settle }), [held, mark, settle]);
}

interface WithId { id: string }
interface QueueRow extends WithId { clientId: string; addedUtc?: string | null; healthFindings?: { kind: string }[] | null }
interface ClientRow { clientId: string; queue: QueueRow[] }

/** The lists the Transfers screen reads, with the rows held off them removed. Anything else on the data passes through. */
export interface GoneShaped {
  telemetry: { clients: ClientRow[] };
  dispatches: WithId[];
  movieRecovery: { recentCases: WithId[] };
  seriesRecovery: { recentCases: WithId[] };
  failedJobs: WithId[];
  processorHandoffs: WithId[];
  orphans: { path: string }[];
  heldReleases: { historyId: string }[];
}

export function withoutGoneRows<T extends GoneShaped>(data: T, gone: GoneRows): T {
  const clients = (data.telemetry?.clients ?? []).map((client) => {
    let changed = false;
    const queue: QueueRow[] = [];
    for (const item of client.queue) {
      if (gone.has(goneKeys.download(item.clientId, item.id))) { changed = true; continue; }
      const findings = item.healthFindings;
      if (findings && findings.some((finding) => gone.has(goneKeys.finding(item.clientId, item.id, finding.kind)))) {
        changed = true;
        queue.push({ ...item, healthFindings: findings.filter((finding) => !gone.has(goneKeys.finding(item.clientId, item.id, finding.kind))) });
        continue;
      }
      queue.push(item);
    }
    return changed ? { ...client, queue } : client;
  });
  const cases = (summary: { recentCases: WithId[] }, mediaType: string) => {
    if (!summary?.recentCases) return summary;
    const recentCases = summary.recentCases.filter((item) => !gone.has(goneKeys.recovery(mediaType, item.id)));
    return recentCases.length === summary.recentCases.length ? summary : { ...summary, recentCases };
  };
  return {
    ...data,
    telemetry: clients.some((client, index) => client !== data.telemetry.clients[index]) ? { ...data.telemetry, clients } : data.telemetry,
    dispatches: data.dispatches?.filter((item) => !gone.has(goneKeys.dispatch(item.id))),
    movieRecovery: cases(data.movieRecovery, "movie"),
    seriesRecovery: cases(data.seriesRecovery, "series"),
    failedJobs: data.failedJobs?.filter((item) => !gone.has(goneKeys.job(item.id))),
    processorHandoffs: data.processorHandoffs?.filter((item) => !gone.has(goneKeys.handoff(item.id))),
    orphans: data.orphans?.filter((item) => !gone.has(goneKeys.orphan(item.path))),
    heldReleases: data.heldReleases?.filter((item) => !gone.has(goneKeys.held(item.historyId)))
  };
}

/** Every row the lists name, by key; a download carries when it was added, so one that comes back as a new item is told apart. */
export function listedRows(data: GoneShaped): Map<string, number | null> {
  const listed = new Map<string, number | null>();
  for (const client of data.telemetry?.clients ?? []) {
    for (const item of client.queue) {
      const added = item.addedUtc ? Date.parse(item.addedUtc) : Number.NaN;
      listed.set(goneKeys.download(item.clientId, item.id), Number.isFinite(added) ? added : null);
      for (const finding of item.healthFindings ?? []) listed.set(goneKeys.finding(item.clientId, item.id, finding.kind), null);
    }
  }
  for (const item of data.dispatches ?? []) listed.set(goneKeys.dispatch(item.id), null);
  for (const item of data.movieRecovery?.recentCases ?? []) listed.set(goneKeys.recovery("movie", item.id), null);
  for (const item of data.seriesRecovery?.recentCases ?? []) listed.set(goneKeys.recovery("series", item.id), null);
  for (const item of data.failedJobs ?? []) listed.set(goneKeys.job(item.id), null);
  for (const item of data.processorHandoffs ?? []) listed.set(goneKeys.handoff(item.id), null);
  for (const item of data.orphans ?? []) listed.set(goneKeys.orphan(item.path), null);
  for (const item of data.heldReleases ?? []) listed.set(goneKeys.held(item.historyId), null);
  return listed;
}
