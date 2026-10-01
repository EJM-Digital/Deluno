/**
 * Shows only the rows that fit (#1098): a band card lists what it has (what is running, what comes next) and the card's
 * height decides how many whole rows it shows, so a short band shows fewer and a tall one more, and a row is never cut
 * through. Rows mark themselves with `data-fit`; the ones that do not fit are hidden (they keep their place, so what
 * fits is measured the same way every time) and the hook says how many do. Measured before the first paint, whenever the
 * card changes size, and again when the fonts are in.
 */
import { useLayoutEffect, useRef, useState, type RefObject } from "react";

/** How many of a host's `data-fit` rows fit inside its height, hiding the rest. Without `enabled` (a card that grows with its rows) every row fits. */
export function fitRows(host: HTMLElement, enabled = true): number {
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
  rows.forEach((row, index) => {
    row.style.visibility = index < fits ? "" : "hidden";
  });
  return fits;
}

export function useFittingRows(enabled = true): [RefObject<HTMLDivElement | null>, number] {
  const ref = useRef<HTMLDivElement | null>(null);
  const [fits, setFits] = useState(Number.MAX_SAFE_INTEGER);
  const measure = useRef<() => void>(() => undefined);
  // Measured after every change of what is listed...
  useLayoutEffect(() => {
    measure.current();
  });
  // ...and whenever the card changes size or the fonts arrive.
  useLayoutEffect(() => {
    const host = ref.current;
    if (!host) return undefined;
    let live = true;
    measure.current = () => {
      if (!live) return;
      const next = fitRows(host, enabled);
      setFits((current) => (current === next ? current : next));
    };
    const again = () => measure.current();
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
