import { useEffect, useMemo } from "react";
import { QueryClient, useQueries, type QueryKey } from "@tanstack/react-query";

/**
 * One cache for the whole app (#1081).
 *
 * The client used to be created inside the app layout, out of reach of the
 * route loaders. A loader could only fetch and wait, so every screen change
 * paid for the slowest request behind it, and coming back to a screen threw
 * away everything it had just shown.
 *
 * Now loaders and components share this one client. A loader starts what the
 * screen needs and returns at once; the screen reads the same queries, shows
 * what the cache already holds straight away, and refreshes behind it.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    // Deluno stays live while its tab is hidden (#641), so there is nothing
    // to catch up on when it is looked at again. Refetching on focus would
    // put back the refresh-on-return #641 removed.
    //
    // refetchIntervalInBackground is a global default (#937): any query that
    // sets its own `refetchInterval` is a safety net behind realtime, so it is
    // safe for the poll to keep running while hidden. A query that opts out
    // can still set the option to false itself.
    queries: { retry: 1, refetchOnWindowFocus: false, refetchIntervalInBackground: true }
  }
});

/** How long a screen's last answer is kept after it is left. */
const KEEP_MS = 30 * 60_000;
/**
 * A loader and the screen it opens ask for the same thing within a few
 * milliseconds; this is what keeps that one request rather than two.
 */
const SHARED_FRESH_MS = 3_000;

/**
 * One thing a screen reads.
 *
 * A source with no `fallback` is one the screen cannot draw without: it is
 * waited for, and if it fails the screen says so in place with a Retry. A
 * source with a `fallback` never holds the screen back: the screen draws with
 * the fallback (or the last answer) and the real answer fills in.
 */
export interface RouteSource<T> {
  key: QueryKey;
  /** What this is called when it has to be named in a sentence. */
  label: string;
  fetch: () => Promise<T>;
  fallback?: T;
  /** Refresh on this cadence while the screen is open. */
  refetchInterval?: number;
  /** Stop that cadence while the tab is hidden, and catch up the moment it is shown. */
  pauseWhenHidden?: boolean;
  /** Only read on these sections of a screen that hosts several (the caller names the section). */
  sections?: readonly string[];
  /** On these sections the screen cannot draw without it, fallback or not. */
  requiredIn?: readonly string[];
}

export type RouteSources = Record<string, RouteSource<any>>;
export type RouteSourceData<S extends RouteSources> = { [K in keyof S]: S[K] extends RouteSource<infer T> ? T : never };

export function defineSources<S extends RouteSources>(sources: S): S {
  return sources;
}

function activeNames<S extends RouteSources>(sources: S, section?: string): (keyof S & string)[] {
  return (Object.keys(sources) as (keyof S & string)[]).filter((name) => {
    const only = sources[name].sections;
    return !only || !section || only.includes(section);
  });
}

function queryOptions(source: RouteSource<unknown>) {
  return {
    queryKey: source.key,
    queryFn: source.fetch,
    // fetchJson already retries a transient failure with its own back-off;
    // retrying again here only made a failing source hold the screen longer.
    retry: false,
    staleTime: SHARED_FRESH_MS,
    gcTime: KEEP_MS
  };
}

/**
 * The route loader for a screen that reads its data through `useRouteSources`.
 * It starts every source that is not already fresh and returns without
 * waiting for any of them, so the address changes and the screen appears in
 * the same breath. It never throws: a failing source is the screen's to show.
 */
export function sourceLoader<S extends RouteSources>(sources: S, section?: (request: Request) => string | undefined) {
  return function loader(args?: { request?: Request }) {
    startSources(sources, args?.request && section ? section(args.request) : undefined);
    // React Router treats a null loader result as "not loaded yet".
    return { cached: true as const };
  };
}

/** Starts (or joins) the requests for a screen's sources, without waiting. */
export function startSources<S extends RouteSources>(sources: S, section?: string) {
  for (const name of activeNames(sources, section)) {
    void queryClient.prefetchQuery(queryOptions(sources[name]));
  }
}

export interface RouteSourceState<S extends RouteSources> {
  /** Every source's value: the answer, the last answer, or its fallback. */
  data: RouteSourceData<S>;
  /** Everything the screen cannot draw without has arrived (or failed). */
  ready: boolean;
  /** Sources that are still on their first answer. */
  pending: ReadonlySet<keyof S & string>;
  /** Sources that failed, by name, with a sentence to show. */
  failed: { name: keyof S & string; label: string; message: string }[];
  /** A required source failed and there is nothing to draw with. */
  blocked: boolean;
  isFetching: boolean;
  /** Fetch every source again now. */
  refetch: () => Promise<void>;
  /** Drop-in for the router's revalidator: same two members the screens already call. */
  revalidator: { revalidate: () => void; state: "idle" | "loading" };
}

export function useRouteSources<S extends RouteSources>(sources: S, section?: string): RouteSourceState<S> {
  const names = useMemo(() => activeNames(sources, section), [sources, section]);
  const results = useQueries({
    queries: names.map((name) => ({
      ...queryOptions(sources[name]),
      refetchInterval: sources[name].refetchInterval,
      refetchIntervalInBackground: !sources[name].pauseWhenHidden,
      // Coming back to a screen shows its last answer and asks again.
      refetchOnMount: true
    }))
  });

  // A source that pauses while the tab is hidden is brought up to date the
  // moment the tab is shown again.
  useEffect(() => {
    const paused = names.filter((name) => sources[name].pauseWhenHidden && sources[name].refetchInterval);
    if (paused.length === 0 || typeof document === "undefined") return undefined;
    const onVisible = () => {
      if (document.visibilityState !== "visible") return;
      for (const name of paused) void queryClient.invalidateQueries({ queryKey: sources[name].key, exact: true });
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => document.removeEventListener("visibilitychange", onVisible);
  }, [names, sources]);

  // useQueries hands back a fresh array every render; the pieces below only
  // depend on what actually changed.
  const signature = results.map((result) => `${result.dataUpdatedAt}|${result.status}|${result.fetchStatus}`).join(",");

  return useMemo(() => {
    const data: Record<string, unknown> = {};
    const pending = new Set<string>();
    const failed: RouteSourceState<S>["failed"] = [];
    let ready = true;
    let blocked = false;
    let isFetching = false;

    names.forEach((name, index) => {
      const source = sources[name];
      const result = results[index];
      if (result.isFetching) isFetching = true;
      const hasFallback = "fallback" in source && !(section && source.requiredIn?.includes(section));

      if (result.data !== undefined) {
        data[name] = result.data;
      } else {
        data[name] = source.fallback;
        pending.add(name);
        if (!hasFallback && result.status !== "error") ready = false;
      }

      if (result.isError && result.data === undefined) {
        failed.push({ name, label: source.label, message: result.error instanceof Error ? result.error.message : `${source.label} could not be read.` });
        if (!hasFallback) blocked = true;
      }
    });

    // Sources this section does not read still have a value: their fallback.
    for (const name of Object.keys(sources)) {
      if (!(name in data)) data[name] = sources[name].fallback;
    }

    const refetch = async () => {
      await Promise.all(names.map((name) => queryClient.refetchQueries({ queryKey: sources[name].key, exact: true })));
    };

    return {
      data: data as RouteSourceData<S>,
      ready,
      pending: pending as ReadonlySet<keyof S & string>,
      failed,
      blocked,
      isFetching,
      refetch,
      revalidator: {
        revalidate: () => { void refetch(); },
        state: isFetching ? "loading" : "idle"
      } as RouteSourceState<S>["revalidator"]
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [names, sources, signature]);
}

/** Fetches the sources of a screen again now, for a card that acts on its own and has no state of the screen. */
export function refetchSources(sources: RouteSources): Promise<void> {
  return Promise.all(
    Object.values(sources).map((source) => queryClient.refetchQueries({ queryKey: source.key, exact: true, type: "active" }))
  ).then(() => undefined);
}
