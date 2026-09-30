/**
 * Small hooks the Live board shares (#1096): the clock that countdowns tick
 * from, when the summary last arrived, the library titles on the board,
 * and which cards have just arrived or changed lane.
 */
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import type { DashboardSummary } from "../../../lib/api";
import { useLiveInterval } from "../../../hooks/use-live-interval";
import { movedCards, type JourneyCard, type Stage } from "./journey";
import { fetchMovieTitles, fetchSeriesTitles, NO_TITLES, wantedTitles, type TitleLookup } from "./titles";

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
export function useTitles(cards: JourneyCard[]): TitleLookup {
  const wanted = useMemo(() => wantedTitles(cards), [cards]);
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
  return useMemo(() => {
    if (!movies.data && !series.data) return NO_TITLES;
    return {
      movies: new Map(Object.entries(movies.data ?? {})),
      series: new Map(Object.entries(series.data?.series ?? {})),
      episodes: new Map(Object.entries(series.data?.episodes ?? {}))
    };
  }, [movies.data, series.data]);
}

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
