/**
 * What moves on the Live tab comes from real changes and nothing else (#1087).
 *
 *  - `useLiveProgress`: the download clients' own readings as the server pushes
 *    them (`DownloadProgress`), laid over the stored queue so a bar and a speed
 *    follow the download instead of waiting for the next poll.
 *  - `useLiveActivity`: new activity as it arrives (`ActivityEventAdded`), put
 *    above what the summary already holds so a row can slide in.
 *  - `useWorkingNow`: what Deluno is doing this moment: a grab on its way to a
 *    client, an import under way, a search running, a download in the processor.
 *    A grab or an import is added by the event that starts it and removed by the
 *    event that ends it, so nothing stays on screen after it finished.
 *
 * None of it animates by itself: a value changes, or a row arrives, and the
 * screen follows. When the page is hidden or motion is reduced it simply shows
 * the new value.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { DashboardActivityItem, DashboardSummary } from "../../lib/api";
import { RealtimeGroups, useSignalREvent } from "../../lib/use-signalr";

/* ---------------------------------------------------------- progress */

export interface LiveProgress {
  progress: number;
  speedMbps: number;
  status: string;
  /** When the reading arrived here. Only a reading newer than the stored queue is trusted over it. */
  at: number;
}

/** How often a burst of readings is shown: a few times a second is plenty to follow a download. */
const PROGRESS_FLUSH_MS = 250;

export function useLiveProgress(): ReadonlyMap<string, LiveProgress> {
  const [readings, setReadings] = useState<ReadonlyMap<string, LiveProgress>>(() => new Map());
  const buffer = useRef(new Map<string, LiveProgress>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useSignalREvent("DownloadProgress", RealtimeGroups.Queue, (event) => {
    buffer.current.set(event.id, { progress: event.progress, speedMbps: event.speedMbps, status: event.status, at: Date.now() });
    if (timer.current !== null) return;
    timer.current = setTimeout(() => {
      timer.current = null;
      setReadings(new Map(buffer.current));
    }, PROGRESS_FLUSH_MS);
  });

  useEffect(() => () => {
    if (timer.current !== null) clearTimeout(timer.current);
  }, []);

  return readings;
}

/* ---------------------------------------------------------- activity */

export interface LiveActivityRow {
  id: string;
  message: string;
  label?: string | null;
  severity?: string | null;
  category: string;
  createdUtc: string;
  relatedEntityType?: string | null;
  relatedEntityId?: string | null;
  /** Arrived while the screen was open, so it slides in. */
  fresh: boolean;
}

const LIVE_ACTIVITY_LIMIT = 60;

/**
 * The newest activity: what arrived live above what the summary holds. A row is
 * one row however it reached the screen, so the summary catching up with an
 * event that already slid in does not show it twice.
 */
export function useLiveActivity(recent: DashboardActivityItem[]): LiveActivityRow[] {
  const [arrived, setArrived] = useState<LiveActivityRow[]>([]);

  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    setArrived((current) => {
      if (current.some((row) => row.id === event.id)) return current;
      return [{ id: event.id, message: event.message, label: event.label, severity: event.severity, category: event.category, createdUtc: event.createdUtc, fresh: true }, ...current].slice(0, LIVE_ACTIVITY_LIMIT);
    });
  });

  return useMemo(() => {
    const seen = new Set<string>();
    const rows: LiveActivityRow[] = [];
    for (const row of arrived) {
      if (seen.has(row.id)) continue;
      seen.add(row.id);
      rows.push(row);
    }
    for (const item of recent) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      rows.push({ id: item.id, message: item.message, label: item.label, category: item.category, createdUtc: item.createdUtc, relatedEntityType: item.relatedEntityType, relatedEntityId: item.relatedEntityId, fresh: false });
    }
    return rows;
  }, [arrived, recent]);
}

/* ------------------------------------------------------- working now */

export type WorkKind = "search" | "grab" | "import" | "processor" | "jobs";

export interface WorkItem {
  key: string;
  kind: WorkKind;
  /** One plain sentence saying what is happening. */
  text: string;
  /** The release and client behind a grab or an import, so the board can show it as a card. */
  release?: string;
  client?: string;
  /** The screen where it can be watched. */
  to: string;
}

/** A grab or an import that never reported its end is not shown for ever. */
const WORK_EXPIRES_MS = 10 * 60_000;

interface Started {
  kind: "grab" | "import";
  release: string;
  client?: string;
  text: string;
  at: number;
}

/**
 * What Deluno is working on this moment. Grabs and imports come from the events
 * that start and end them; a search running, downloads in the processor and
 * jobs running come from the summary, which is refreshed whenever any of them
 * change. Nothing here is invented: with nothing running the list is empty.
 */
export function useWorkingNow(summary: DashboardSummary | undefined): WorkItem[] {
  const [started, setStarted] = useState<ReadonlyMap<string, Started>>(() => new Map());
  const begin = useCallback((key: string, entry: Started) => setStarted((current) => new Map(current).set(key, entry)), []);
  const end = useCallback((key: string) => setStarted((current) => {
    if (!current.has(key)) return current;
    const next = new Map(current);
    next.delete(key);
    return next;
  }), []);

  useSignalREvent("DispatchGrabAttempt", RealtimeGroups.Queue, (event) => begin(`grab:${event.dispatchId}`, { kind: "grab", text: `Sending ${event.releaseName} to ${event.clientName}`, release: event.releaseName, client: event.clientName, at: Date.now() }));
  useSignalREvent("DispatchGrabCompleted", RealtimeGroups.Queue, (event) => end(`grab:${event.dispatchId}`));
  useSignalREvent("DispatchImportStarted", RealtimeGroups.Queue, (event) => begin(`import:${event.dispatchId}`, { kind: "import", text: `Importing ${event.releaseName}`, release: event.releaseName, at: Date.now() }));
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, (event) => end(`import:${event.dispatchId}`));

  return useMemo(() => {
    const items: WorkItem[] = [];
    const now = Date.now();
    for (const library of summary?.automation?.libraries ?? []) {
      if (library.status === "running") items.push({ key: `search:${library.libraryId}`, kind: "search", text: `Searching ${library.name} for what is missing or could be better`, to: "/search-cycles" });
    }
    for (const [key, entry] of started) {
      if (now - entry.at > WORK_EXPIRES_MS) continue;
      items.push({ key, kind: entry.kind, text: entry.text, release: entry.release, client: entry.client, to: "/queue" });
    }
    const downloads = summary?.downloads;
    if ((downloads?.processing ?? 0) > 0) {
      items.push({ key: "processor", kind: "processor", text: `${downloads?.processing} ${downloads?.processing === 1 ? "download is" : "downloads are"} being cleaned by the processor`, to: "/queue" });
    }
    if ((downloads?.importReady ?? 0) > 0) {
      items.push({ key: "import-ready", kind: "import", text: `${downloads?.importReady} ${downloads?.importReady === 1 ? "download is" : "downloads are"} ready to import`, to: "/queue" });
    }
    const running = summary?.system?.jobs.running ?? 0;
    if (running > 0) items.push({ key: "jobs", kind: "jobs", text: `${running} background ${running === 1 ? "job is" : "jobs are"} running`, to: "/activity" });
    return items;
  }, [summary, started]);
}
