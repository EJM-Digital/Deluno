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

/**
 * The poster of each title on the board, asked for in one request per kind of
 * media. The server answers only for titles that still exist, so a title that
 * was deleted (its download is still in the history) is left out and its card
 * keeps its stage icon, with no failing request. An older server without the
 * batch answer is asked one title at a time instead.
 */
export function usePosters(cards: JourneyCard[]): ReadonlyMap<string, string> {
  const wanted = useMemo(() => {
    const movies = new Set<string>();
    const shows = new Set<string>();
    for (const card of cards) {
      if (!card.entityId || card.mediaType === "other") continue;
      (card.mediaType === "movie" ? movies : shows).add(card.entityId);
    }
    return [
      { mediaType: "movie" as const, ids: [...movies].sort().slice(0, 24) },
      { mediaType: "tv" as const, ids: [...shows].sort().slice(0, 24) }
    ];
  }, [cards]);
  const results = useQueries({
    queries: wanted.map(({ mediaType, ids }) => ({
      queryKey: ["dashboard", "posters", mediaType, ids.join(",")],
      enabled: ids.length > 0,
      queryFn: () => fetchPosters(mediaType, ids),
      staleTime: 30 * 60_000,
      retry: false
    }))
  });
  return useMemo(() => {
    const map = new Map<string, string>();
    wanted.forEach(({ mediaType }, index) => {
      for (const [id, url] of Object.entries(results[index]?.data ?? {})) if (url) map.set(`${mediaType}:${id}`, url);
    });
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wanted, results.map((result) => result.dataUpdatedAt).join("|")]);
}

async function fetchPosters(mediaType: "movie" | "tv", ids: string[]): Promise<Record<string, string | null>> {
  const root = mediaType === "movie" ? "/api/movies" : "/api/series";
  try {
    return await fetchJson<Record<string, string | null>>(`${root}/posters?ids=${encodeURIComponent(ids.join(","))}`);
  } catch {
    // An older server: one title at a time, and a title that is gone simply has no poster.
    const found: Record<string, string | null> = {};
    await Promise.all(ids.map(async (id) => {
      try {
        found[id] = (await fetchJson<{ posterUrl?: string | null }>(`${root}/${id}`)).posterUrl ?? null;
      } catch {
        found[id] = null;
      }
    }));
    return found;
  }
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
