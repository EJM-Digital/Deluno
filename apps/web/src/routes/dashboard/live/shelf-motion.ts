/**
 * How the shelf's posters move (Weir's Just finished shelf, ported): a poster that is new to the shelf fades and rises into
 * place (`useTileArrivals`), and when one is added or taken the posters that stay slide to their new places instead of
 * jumping (`useSlideNeighbours`). Nothing moves while the window is being resized, or when movement is not allowed.
 */
import { useLayoutEffect, useRef, type RefObject } from "react";
import { motionAllowed } from "../../../lib/page-motion";
import { HELD_ATTRIBUTE, SLOTS_CHANGED_EVENT } from "./shelf-slots";

/** Set by the shelf on each poster's list item, to its key: how a poster is told apart from the others. */
export const TILE_KEY_ATTRIBUTE = "data-shelf-key";

const RESIZING_CLASS = "live-resizing";
const SLIDE_MS = 300;
const SLIDE_EASING = "cubic-bezier(.2, .8, .2, 1)";
/** A poster that moved less than this has not moved. */
const MOVED_PX = 1;
const ARRIVE_MS = 800;
const ARRIVE_EASING = "cubic-bezier(.2, .8, .2, 1)";
const ARRIVE_FRAMES: Keyframe[] = [
  { opacity: 0, transform: "translateY(14px) scale(0.92)" },
  { opacity: 1, transform: "none" }
];

/** Where each poster of the row is, from the row's left edge. */
function leftsOf(host: HTMLElement): Map<string, number> {
  const rowLeft = host.getBoundingClientRect().left;
  const lefts = new Map<string, number>();
  for (const tile of host.querySelectorAll<HTMLElement>(`[${TILE_KEY_ATTRIBUTE}]`)) lefts.set(tile.getAttribute(TILE_KEY_ATTRIBUTE) ?? "", tile.getBoundingClientRect().left - rowLeft);
  return lefts;
}

/** Slides each poster that was somewhere else in `before` from there to where it is now. */
function slideFrom(host: HTMLElement, before: ReadonlyMap<string, number>) {
  if (!host.isConnected) return;
  const now = leftsOf(host);
  for (const tile of host.querySelectorAll<HTMLElement>(`[${TILE_KEY_ATTRIBUTE}]`)) {
    const key = tile.getAttribute(TILE_KEY_ATTRIBUTE) ?? "";
    const was = before.get(key);
    const left = now.get(key) ?? 0;
    if (was !== undefined && Math.abs(was - left) > MOVED_PX && typeof tile.animate === "function") tile.animate([{ transform: `translateX(${was - left}px)` }, { transform: "none" }], { duration: SLIDE_MS, easing: SLIDE_EASING });
  }
}

/**
 * When a poster is added to the shelf or taken from it, the ones that stay are moved back to where they were and let go. A
 * poster added in a closed slot moves nothing, and one whose slot opens moves the ones beside it by layout, in step with the
 * poster flying to it; where the posters stand is noted again as each slot closes, opens and settles.
 */
export function useSlideNeighbours(row: RefObject<HTMLElement | null>, order: string, tileWidth: number | null): void {
  const lefts = useRef(new Map<string, number>());
  const lastOrder = useRef(order);
  useLayoutEffect(() => {
    const host = row.current;
    if (!host) return;
    const slide = order !== lastOrder.current && motionAllowed() && !document.body.classList.contains(RESIZING_CLASS);
    const before = lefts.current;
    lefts.current = leftsOf(host);
    lastOrder.current = order;
    // Measured once the whole commit is done: a poster added for a title still on its way is put in a closed slot by another effect of the same commit.
    if (slide) queueMicrotask(() => slideFrom(host, before));
  }, [row, order, tileWidth]);
  useLayoutEffect(() => {
    const note = () => {
      if (row.current) lefts.current = leftsOf(row.current);
    };
    document.addEventListener(SLOTS_CHANGED_EVENT, note);
    return () => document.removeEventListener(SLOTS_CHANGED_EVENT, note);
  }, [row]);
}

/**
 * A poster that is new to the shelf arrives: it fades and rises into place. One the shelf only now has room for is not new;
 * a poster held for the poster that is about to fly to it has no arrival of its own, the flight is its arrival. `filed` is the
 * key of every poster the shelf has, joined, shown or not.
 */
export function useTileArrivals(row: RefObject<HTMLElement | null>, filed: string): void {
  const before = useRef<ReadonlySet<string> | null>(null);
  useLayoutEffect(() => {
    const host = row.current;
    if (!host) return;
    const had = before.current;
    before.current = new Set(filed.split("|"));
    if (!motionAllowed() || document.body.classList.contains(RESIZING_CLASS)) return;
    for (const tile of host.querySelectorAll<HTMLElement>(`[${TILE_KEY_ATTRIBUTE}]`)) {
      if (had?.has(tile.getAttribute(TILE_KEY_ATTRIBUTE) ?? "")) continue;
      if (tile.querySelector(`[${HELD_ATTRIBUTE}]`)) continue;
      if (typeof tile.animate === "function") tile.animate(ARRIVE_FRAMES, { duration: ARRIVE_MS, easing: ARRIVE_EASING });
    }
  }, [row, filed]);
}
