/**
 * Small hooks the Live board shares (#1096): the clock that countdowns tick
 * from, when the summary last arrived, the posters of the titles on the board,
 * and which cards have just arrived or changed lane.
 */
import { useQueries } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { fetchJson, type DashboardSummary } from "../../../lib/api";
import { useLiveInterval } from "../../../hooks/use-live-interval";
import { movedCards, type JourneyCard, type Stage } from "./journey";

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

/** The poster of each title on the board, read from its own stored record (cheap, cached). */
export function usePosters(cards: JourneyCard[]): ReadonlyMap<string, string> {
  const wanted = useMemo(() => {
    const seen = new Map<string, { mediaType: "movie" | "tv"; id: string }>();
    for (const card of cards) {
      if (!card.entityId || card.mediaType === "other") continue;
      seen.set(`${card.mediaType}:${card.entityId}`, { mediaType: card.mediaType, id: card.entityId });
    }
    return [...seen.entries()].slice(0, 24);
  }, [cards]);
  const results = useQueries({
    queries: wanted.map(([key, item]) => ({
      queryKey: ["dashboard", "poster", key],
      queryFn: () => fetchJson<{ posterUrl?: string | null }>(item.mediaType === "movie" ? `/api/movies/${item.id}` : `/api/series/${item.id}`).then((record) => record.posterUrl ?? null),
      staleTime: 30 * 60_000,
      retry: false
    }))
  });
  return useMemo(() => {
    const map = new Map<string, string>();
    wanted.forEach(([key], index) => {
      const url = results[index]?.data;
      if (url) map.set(key, url);
    });
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wanted, results.map((result) => result.data ?? "").join("|")]);
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
