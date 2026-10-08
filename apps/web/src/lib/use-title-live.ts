import { useCallback, useEffect, useRef, useState } from "react";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "./use-signalr";

/**
 * Keeps a film's or show's page current while it is open (#1179).
 *
 * A title page loads many things at once. When the title changes from any path
 * (an import finishing, a metadata job, a search, a subtitle scan, an edit in
 * another tab) the server tells the page's own subject, `title:{id}`. The page
 * then re-reads only the groups of data that kind of change can move, once per
 * burst, and shows the new answer with no reload and no loading state.
 */
export type TitleGroups<T> = Record<string, () => Promise<Partial<T>>>;

/** What a change to the title moves: the names of the groups to read again. */
export type TitleEventGroups = Partial<Record<TitleEventName, readonly string[]>>;

export type TitleEventName =
  | "MovieChanged"
  | "SeriesChanged"
  | "ImportStateChanged"
  | "JourneyStageChanged"
  | "RecoveryCaseChanged"
  | "ReleasesTurnedDown";

/** How long a burst of title events is gathered before the page reads again. */
const GATHER_MS = 400;

export function useTitleLive<T extends object>(
  titleId: string,
  loaderData: T,
  groups: TitleGroups<T>,
  eventGroups: TitleEventGroups
): T {
  const [overrides, setOverrides] = useState<Partial<T>>({});
  const generation = useRef(0);
  const pending = useRef(new Set<string>());
  const groupsRef = useRef(groups);
  groupsRef.current = groups;

  // The page's own data just reloaded (an action, a navigation, a reconnect): it is the freshest there is.
  useEffect(() => {
    generation.current += 1;
    pending.current.clear();
    setOverrides({});
  }, [loaderData, titleId]);

  const flush = useCoalescedRevalidate(() => {
    const names = [...pending.current];
    pending.current.clear();
    if (names.length === 0) return;
    const mine = generation.current;
    void Promise.all(names.map((name) => groupsRef.current[name]?.().catch(() => ({}) as Partial<T>))).then((parts) => {
      if (mine !== generation.current) return;
      const merged: Partial<T> = {};
      for (const part of parts) Object.assign(merged, part);
      setOverrides((previous) => ({ ...previous, ...merged }));
    });
  }, GATHER_MS);

  const on = useCallback(
    (event: TitleEventName) => {
      const names = eventGroups[event];
      if (!names || names.length === 0) return;
      for (const name of names) pending.current.add(name);
      flush();
    },
    [eventGroups, flush]
  );

  const subject = RealtimeGroups.Title(titleId);
  useSignalREvent("MovieChanged", subject, () => on("MovieChanged"));
  useSignalREvent("SeriesChanged", subject, () => on("SeriesChanged"));
  useSignalREvent("ImportStateChanged", subject, () => on("ImportStateChanged"));
  useSignalREvent("JourneyStageChanged", subject, () => on("JourneyStageChanged"));
  useSignalREvent("RecoveryCaseChanged", subject, () => on("RecoveryCaseChanged"));
  useSignalREvent("ReleasesTurnedDown", subject, () => on("ReleasesTurnedDown"));

  return { ...loaderData, ...overrides };
}
