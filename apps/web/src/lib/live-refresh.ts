import { useCallback, useContext, useRef } from "react";
import { queryClient, type RouteSource } from "./route-cache";
import { UNSAFE_DataRouterContext } from "react-router-dom";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvents, type SignalREventName } from "./use-signalr";

/**
 * What makes a screen read its data again (#1179). A screen left open shows a change made anywhere
 * else (another tab, the other end of an automation) the moment the server says so, with no reload
 * and no timer.
 */
export interface LiveRefreshSpec {
  /** Events about one kind of thing: `SettingsChanged`, `LibraryChanged`, `IndexerChanged` and so on. */
  events?: readonly SignalREventName[];
  /** Settings areas: a `ConfigChanged` naming one of these also refreshes the screen. */
  areas?: readonly string[];
}

/** Waits this long for a burst of changes to finish before reading, so a save that touches five things reads once. */
const SETTLE_MS = 400;

const NO_EVENTS: readonly SignalREventName[] = [];

/**
 * Reads the screen's data again when one of the events in `spec` arrives. With no `refresh` it
 * revalidates the route's loader. Pass a constant `spec`, declared outside the component.
 */
export function useLiveRefresh(spec: LiveRefreshSpec, refresh?: () => void) {
  // Read straight from the router's context so a screen drawn outside a data router (a test) simply has nothing to revalidate.
  const router = useContext(UNSAFE_DataRouterContext)?.router;
  const run = useCallback(() => {
    if (refresh) refresh();
    else void router?.revalidate();
  }, [refresh, router]);
  const settle = useCoalescedRevalidate(run, SETTLE_MS);
  const areas = spec.areas;
  const events = areas && areas.length > 0 ? [...(spec.events ?? NO_EVENTS), "ConfigChanged" as const] : (spec.events ?? NO_EVENTS);
  useSignalREvents(events, RealtimeGroups.Dashboard, (name, payload) => {
    if (name === "ConfigChanged") {
      const area = (payload as { area?: string } | null)?.area;
      if (!area || !areas?.includes(area)) return;
    }
    settle();
  });
}

/**
 * Marks the stored lists a screen reads as out of date, once per burst (#1179). Several events in
 * `delayMs` ask for each list once, and only the lists some event named are read again, so a health
 * change reads four lists and not the whole screen.
 */
export function useCoalescedSourceRefresh(delayMs = 500): (...sources: RouteSource<any>[]) => void {
  const pending = useRef(new Map<string, RouteSource<any>>());
  const flush = useCoalescedRevalidate(() => {
    const batch = [...pending.current.values()];
    pending.current.clear();
    for (const source of batch) void queryClient.invalidateQueries({ queryKey: source.key, exact: true });
  }, delayMs);
  return useCallback((...sources: RouteSource<any>[]) => {
    for (const source of sources) pending.current.set(JSON.stringify(source.key), source);
    flush();
  }, [flush]);
}
