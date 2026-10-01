/**
 * Small hooks the Live board shares (#1096): the clock that countdowns tick
 * from, when the summary last arrived, the library titles on the board,
 * and which cards have just arrived or changed lane.
 */
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import type { DashboardSummary } from "../../../lib/api";
import { useLiveInterval } from "../../../hooks/use-live-interval";
import { movedCards, type JourneyCard, type Stage } from "./journey";
import { fetchByName, fetchMovieTitles, fetchSeriesTitles, NO_TITLES, nameSearchOf, wantedTitles, type Entity, type NameSearch, type TitleInfo, type TitleLookup } from "./titles";

/** The clock, advanced every `intervalMs` from the shared ticker (which keeps running while the tab is hidden). */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useLiveInterval(() => setNow(Date.now()), intervalMs);
  return now;
}

/**
 * When the summary last arrived here. The server stamps each summary, so a
 * summary that is already in the cache on arrival keeps its own time; a newer
 * one (a realtime nudge, the safety-net refresh) resets it to now.
 */
export function useSummaryUpdatedAt(summary: DashboardSummary | undefined): number {
  const [updatedAt, setUpdatedAt] = useState(() => {
    const stamped = Date.parse(summary?.asOfUtc ?? "");
    return Number.isFinite(stamped) ? Math.min(Date.now(), stamped) : Date.now();
  });
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    setUpdatedAt(Date.now());
  }, [summary]);
  return updatedAt;
}

/**
 * The library title behind each card on the board (its name, year, poster and
 * episode number), asked for in one request per kind of media. The server
 * answers only for titles that still exist, so a title that was deleted (its
 * download is still in the history) is left out and its card keeps the
 * release's own name and its stage icon, with no failing request.
 */
export function useTitles(cards: JourneyCard[], extra: ReadonlyArray<Entity | undefined> = NO_EXTRA): TitleLookup {
  const wanted = useMemo(() => wantedTitles(cards, extra), [cards, extra]);
  const movieIds = wanted.movies.join(",");
  const showIds = wanted.series.join(",");
  const episodeIds = wanted.episodes.join(",");
  const movies = useQuery({
    queryKey: ["dashboard", "titles", "movies", movieIds],
    enabled: wanted.movies.length > 0,
    queryFn: () => fetchMovieTitles(wanted.movies),
    staleTime: 30 * 60_000,
    // While a new set of ids is asked for, the last answer stays on screen (titles already known never blink back to release names).
    placeholderData: keepPreviousData,
    retry: false
  });
  const series = useQuery({
    queryKey: ["dashboard", "titles", "series", showIds, episodeIds],
    enabled: wanted.series.length > 0 || wanted.episodes.length > 0,
    queryFn: () => fetchSeriesTitles(wanted.series, wanted.episodes),
    staleTime: 30 * 60_000,
    // While a new set of ids is asked for, the last answer stays on screen (titles already known never blink back to release names).
    placeholderData: keepPreviousData,
    retry: false
  });
  // Downloads that no sent download names (put in the client some other way) are matched to the library by the release's own name.
  const searches = useMemo(() => {
    const found = new Map<string, NameSearch>();
    for (const card of cards) {
      const search = nameSearchOf(card);
      if (search) found.set(card.key, search);
    }
    return found;
  }, [cards]);
  const searchKey = useMemo(() => [...new Set([...searches.values()].map((search) => `${search.kind}|${search.name}|${search.year ?? ""}`))].sort().join("\n"), [searches]);
  const byName = useQuery({
    queryKey: ["dashboard", "titles", "by-name", searchKey],
    enabled: searchKey.length > 0,
    queryFn: async () => {
      const answers: Record<string, { kind: "movie" | "series"; id: string; info: TitleInfo } | null> = {};
      for (const key of searchKey.split("\n").slice(0, 24)) {
        const [kind, name, year] = key.split("|");
        const search: NameSearch = { kind: kind as NameSearch["kind"], name, year: year ? Number(year) : undefined };
        const hit = await fetchByName(search);
        answers[key] = hit ? { kind: search.kind, ...hit } : null;
      }
      return answers;
    },
    staleTime: 30 * 60_000,
    placeholderData: keepPreviousData,
    retry: false
  });
  return useMemo(() => {
    if (!movies.data && !series.data && !byName.data) return NO_TITLES;
    const movieMap = new Map(Object.entries(movies.data ?? {}));
    const seriesMap = new Map(Object.entries(series.data?.series ?? {}));
    const matched = new Map<string, Entity>();
    for (const [cardKey, search] of searches) {
      const hit = byName.data?.[`${search.kind}|${search.name}|${search.year ?? ""}`];
      if (!hit) continue;
      (hit.kind === "movie" ? movieMap : seriesMap).set(hit.id, hit.info);
      matched.set(cardKey, { kind: hit.kind, id: hit.id });
    }
    // Answered means answered for the titles asked now: while a new title is being looked up, the last answer (kept on
    // screen) does not have it, and it must not be taken for a title that has left the library.
    const answered = { movies: wanted.movies.length === 0 || (movies.data !== undefined && !movies.isPlaceholderData), series: (wanted.series.length === 0 && wanted.episodes.length === 0) || (series.data !== undefined && !series.isPlaceholderData) };
    return { movies: movieMap, series: seriesMap, episodes: new Map(Object.entries(series.data?.episodes ?? {})), matched, answered };
  }, [movies.data, movies.isPlaceholderData, series.data, series.isPlaceholderData, byName.data, searches, wanted]);
}

const NO_EXTRA: ReadonlyArray<Entity | undefined> = [];

/** How long a card keeps its arrival glow. */
const ARRIVAL_MS = 2400;

/**
 * The cards that have arrived on the board, or moved to another lane, since it
 * was first drawn. They are marked for a moment so they can be shown arriving.
 */
export function useArrivals(cards: JourneyCard[]): ReadonlySet<string> {
  const previous = useRef<ReadonlyMap<string, Stage> | null>(null);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(() => new Set());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const before = previous.current;
    previous.current = new Map(cards.map((card) => [card.key, card.stage]));
    if (before === null) return;
    const arrived = movedCards(before, cards);
    for (const card of cards) if (!before.has(card.key)) arrived.add(card.key);
    if (arrived.size === 0) return;
    setFresh(arrived);
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = setTimeout(() => setFresh(new Set()), ARRIVAL_MS);
  }, [cards]);

  useEffect(() => () => {
    if (timer.current !== null) clearTimeout(timer.current);
  }, []);

  return fresh;
}

/**
 * Keys that have appeared in a list since it was first drawn, for a moment each, so what arrives can be shown arriving.
 * Nothing is marked the first time the list is drawn: only a real change is.
 */
export function useFreshKeys(keys: readonly string[], holdMs = 1600): ReadonlySet<string> {
  const previous = useRef<ReadonlySet<string> | null>(null);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(() => new Set());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const signature = keys.join("|");
  useEffect(() => {
    const before = previous.current;
    previous.current = new Set(keys);
    if (before === null) return;
    const arrived = new Set(keys.filter((key) => !before.has(key)));
    if (arrived.size === 0) return;
    setFresh(arrived);
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = setTimeout(() => setFresh(new Set()), holdMs);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);
  useEffect(() => () => {
    if (timer.current !== null) clearTimeout(timer.current);
  }, []);
  return fresh;
}

/** The height of an element, kept up to date as it changes (0 until it has been laid out). */
export function useElementHeight<T extends HTMLElement>(): [RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [height, setHeight] = useState(0);
  useEffect(() => {
    const element = ref.current;
    if (!element || typeof ResizeObserver === "undefined") return undefined;
    const read = () => setHeight(Math.round(element.getBoundingClientRect().height));
    read();
    const observer = new ResizeObserver(read);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return [ref, height];
}

/**
 * The width and height of an element, kept up to date as it changes (0 until it has been laid out). It is read before the
 * first paint (the layout built from it is settled when the page first shows, so nothing shifts after load) and again when
 * the fonts have loaded, whose metrics can move the text that sizes the space.
 */
export function useElementSize<T extends HTMLElement>(): [RefObject<T | null>, { width: number; height: number }] {
  const ref = useRef<T | null>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element || typeof ResizeObserver === "undefined") return undefined;
    let live = true;
    const read = () => {
      if (!live) return;
      const box = element.getBoundingClientRect();
      const next = { width: Math.round(box.width), height: Math.round(box.height) };
      setSize((current) => (current.width === next.width && current.height === next.height ? current : next));
    };
    read();
    const observer = new ResizeObserver(read);
    observer.observe(element);
    void document.fonts?.ready.then(read);
    return () => {
      live = false;
      observer.disconnect();
    };
  }, []);
  return [ref, size];
}

/** The size of one rem in pixels, read once (the app's own type scale is in rem, so measured space is compared in rem). */
export function useRemPx(): number {
  const [rem] = useState(() => (typeof window === "undefined" ? 16 : parseFloat(getComputedStyle(document.documentElement).fontSize) || 16));
  return rem;
}

/** True while the window is at least the `lg` breakpoint wide, where the page is held to the window and its cards must fit the space they are given. Below it the page scrolls and every card shows everything. */
export function useIsWide(): boolean {
  const query = "(min-width: 1024px)";
  const [wide, setWide] = useState(() => typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia(query).matches);
  useEffect(() => {
    if (typeof window.matchMedia !== "function") return undefined;
    const list = window.matchMedia(query);
    const read = () => setWide(list.matches);
    read();
    list.addEventListener?.("change", read);
    return () => list.removeEventListener?.("change", read);
  }, []);
  return wide;
}
