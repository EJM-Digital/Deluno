import { useCallback, useEffect, useMemo, useState } from "react";

/**
 * Rows a person has acted on and the server has accepted (#1538). A row leaves the screen on the success reply and stays gone:
 * a read that was started before the action, or a server list that has not caught up, must not draw it again. Each row is
 * held by its key until the server's own list stops naming it (the next lists simply do not contain it) or this long passes,
 * whichever is first, so a row that really does come back later is shown.
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
  return useMemo(() => ({ has: (key: string) => held.has(key), mark }), [held, mark]);
}

interface WithId { id: string }
interface QueueRow extends WithId { clientId: string; healthFindings?: { kind: string }[] | null }
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
  const clients = data.telemetry.clients.map((client) => {
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
    const recentCases = summary.recentCases.filter((item) => !gone.has(goneKeys.recovery(mediaType, item.id)));
    return recentCases.length === summary.recentCases.length ? summary : { ...summary, recentCases };
  };
  return {
    ...data,
    telemetry: clients.some((client, index) => client !== data.telemetry.clients[index]) ? { ...data.telemetry, clients } : data.telemetry,
    dispatches: data.dispatches.filter((item) => !gone.has(goneKeys.dispatch(item.id))),
    movieRecovery: cases(data.movieRecovery, "movie"),
    seriesRecovery: cases(data.seriesRecovery, "series"),
    failedJobs: data.failedJobs.filter((item) => !gone.has(goneKeys.job(item.id))),
    processorHandoffs: data.processorHandoffs.filter((item) => !gone.has(goneKeys.handoff(item.id))),
    orphans: data.orphans.filter((item) => !gone.has(goneKeys.orphan(item.path))),
    heldReleases: data.heldReleases.filter((item) => !gone.has(goneKeys.held(item.historyId)))
  };
}
