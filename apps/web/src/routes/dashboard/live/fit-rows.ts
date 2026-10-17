/**
 * Shows only the rows that fit (#1098): a band card lists what it has (what is running, what comes next) and the card's
 * height decides how many whole rows it shows, so a short band shows fewer and a tall one more, and a row is never cut
 * through. Rows mark themselves with `data-fit`; the ones that do not fit are hidden (they keep their place, so what
 * fits is measured the same way every time) and the hook says how many do. Measured before the first paint, whenever the
 * card changes size, and again when the fonts are in.
 */
import { useLayoutEffect, useRef, useState, type RefObject } from "react";

/** How many of a host's `data-fit` rows fit inside its height, hiding the rest. Without `enabled` (a card that grows with its rows) every row fits. `most` caps the count (see `useFittingRows`). */
export function fitRows(host: HTMLElement, enabled = true, most = Number.MAX_SAFE_INTEGER): number {
  const rows = Array.from(host.querySelectorAll<HTMLElement>("[data-fit]"));
  for (const row of rows) row.style.visibility = "";
  if (!enabled) return rows.length;
  const limit = host.getBoundingClientRect().bottom;
  let fits = 0;
  for (const row of rows) {
    // The space a row keeps under its own content (its padding) may run past the card; its content may not.
    const padding = parseFloat(getComputedStyle(row).paddingBottom) || 0;
    if (row.getBoundingClientRect().bottom - padding > limit + 0.5) break;
    fits++;
  }
  fits = Math.min(fits, most);
  rows.forEach((row, index) => {
    row.style.visibility = index < fits ? "" : "hidden";
  });
  return fits;
}

/** Measuring after a render may change what the render shows this many times in a row before it may only shrink (#1348). */
const SETTLE_PASSES = 3;

/**
 * The rows that fit, measured after every render, whenever the card changes size and when the fonts arrive. What a card shows
 * can change its own height (the "N more" link in its header wraps, the box shrinks), so a measure can answer a render with
 * a different number each time; set on every render that never stops (React error #185, #1348). A measure that follows a
 * render may therefore change the number a few times and then only lower it, which ends: the number it settles on is the
 * smaller of the two the card swung between, so no row is ever cut through. It may grow again when the card's rows or width
 * change, or when the size or the fonts call for a fresh measure.
 */
export function useFittingRows(enabled = true): [RefObject<HTMLDivElement | null>, number] {
  const ref = useRef<HTMLDivElement | null>(null);
  const [fits, setFits] = useState(Number.MAX_SAFE_INTEGER);
  const measure = useRef<(afterRender: boolean) => void>(() => undefined);
  // Measured after every change of what is listed...
  useLayoutEffect(() => {
    measure.current(true);
  });
  // ...and whenever the card changes size or the fonts arrive.
  useLayoutEffect(() => {
    const host = ref.current;
    if (!host) return undefined;
    let live = true;
    let shown = Number.MAX_SAFE_INTEGER;
    let streak = 0;
    let ceiling: { rows: number; width: number; most: number } | null = null;
    measure.current = (afterRender) => {
      if (!live) return;
      const rows = host.querySelectorAll("[data-fit]").length;
      const width = Math.round(host.getBoundingClientRect().width);
      if (ceiling && (ceiling.rows !== rows || ceiling.width !== width)) ceiling = null;
      let next = fitRows(host, enabled, ceiling?.most);
      if (!afterRender) streak = 0;
      else if (next === shown) streak = 0;
      else if (next > shown && ++streak > SETTLE_PASSES) {
        // Growing again after shrinking, over and over: stay at the smaller number until the card's rows or width change.
        ceiling = { rows, width, most: shown };
        next = fitRows(host, enabled, shown);
        streak = 0;
      } else if (next < shown) streak += 1;
      if (next === shown) return;
      shown = next;
      setFits(next);
    };
    const again = () => measure.current(false);
    again();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(again);
    observer?.observe(host);
    void document.fonts?.ready.then(again);
    document.fonts?.addEventListener?.("loadingdone", again);
    return () => {
      live = false;
      observer?.disconnect();
      document.fonts?.removeEventListener?.("loadingdone", again);
    };
  }, [enabled]);
  return [ref, fits];
}
