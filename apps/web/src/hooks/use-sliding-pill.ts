import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";

export interface SlidingPillState {
  left: number;
  width: number;
  ready: boolean;
  /** Whether this position is a chosen move (animate) or a silent correction (snap). */
  animate: boolean;
}

/**
 * Positions a sliding highlight behind whichever element in the track is
 * keyed `activeKey`, and keeps it there.
 *
 * Filed as #934: the highlight sat wherever it was last told to, so it was
 * only ever right by coincidence — the chip it sits on resizes for reasons
 * that have nothing to do with picking a different one. This re-measures on
 * every one of those:
 *
 * - a ResizeObserver on the track and on every registered chip, so a label or
 *   count that changes width is caught even when the track's own box does not;
 * - `signal`, a caller-supplied string that changes whenever label or count
 *   *text* might have — a facets count arriving after first paint, most of
 *   all — for the frame before either has actually resized anything;
 * - `document.fonts.ready`, because the chip widths measured before a custom
 *   font finishes loading are the widths of the fallback font;
 * - a window resize, as a last-resort net for anything the observers above
 *   miss (a scrollbar appearing elsewhere on the page, for instance).
 *
 * Only a change to `activeKey` — a chip actually being picked — animates the
 * move. Every other re-measure snaps instantly: "it moves with its animation
 * only when you pick a different chip, never as a visible correction after
 * load" is the acceptance line this exists to satisfy.
 */
export function useSlidingPill<K>(activeKey: K, signal: string) {
  const trackElRef = useRef<HTMLElement | null>(null);
  const elementsRef = useRef<Map<K, HTMLElement | null>>(new Map());
  const observerRef = useRef<ResizeObserver | null>(null);
  const activeKeyRef = useRef(activeKey);
  activeKeyRef.current = activeKey;

  const [pill, setPill] = useState<SlidingPillState>({ left: 0, width: 0, ready: false, animate: false });

  const measure = useCallback((animate: boolean) => {
    const track = trackElRef.current;
    const el = elementsRef.current.get(activeKeyRef.current);
    if (!track || !el) return;
    const tRect = track.getBoundingClientRect();
    const bRect = el.getBoundingClientRect();
    const left = bRect.left - tRect.left;
    const width = bRect.width;
    setPill((prev) =>
      prev.ready && prev.left === left && prev.width === width && prev.animate === animate
        ? prev
        : { left, width, ready: true, animate });
  }, []);

  // Measured as soon as an element is attached, not only once some later
  // effect gets around to it — a chip that mounts after the track (or the
  // active one specifically) must not wait for an unrelated re-render before
  // the highlight knows where it is.
  const registerElement = useCallback((key: K, element: HTMLElement | null) => {
    const previous = elementsRef.current.get(key);
    if (previous && previous !== element) observerRef.current?.unobserve(previous);
    if (element) {
      elementsRef.current.set(key, element);
      observerRef.current?.observe(element);
      measure(false);
    } else {
      elementsRef.current.delete(key);
    }
  }, [measure]);

  const registerTrack = useCallback((element: HTMLElement | null) => {
    if (trackElRef.current && trackElRef.current !== element) observerRef.current?.unobserve(trackElRef.current);
    trackElRef.current = element;
    if (element) {
      observerRef.current?.observe(element);
      measure(false);
    }
  }, [measure]);

  // The move itself: the only re-measure that is a click's consequence, and
  // the only one that animates.
  useLayoutEffect(() => {
    measure(true);
  }, [activeKey, measure]);

  // Text that may have changed a chip's width without the chip itself, or the
  // track, having resized yet at the point this runs (a count arriving in the
  // same tick as this render, for instance).
  useLayoutEffect(() => {
    measure(false);
  }, [signal, measure]);

  // Actual size changes: the track's own box, and every chip inside it.
  useEffect(() => {
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => measure(false));
    observerRef.current = observer;
    if (trackElRef.current) observer.observe(trackElRef.current);
    elementsRef.current.forEach((element) => {
      if (element) observer.observe(element);
    });
    return () => {
      observer.disconnect();
      observerRef.current = null;
    };
  }, [measure]);

  // A custom font finishing load changes glyph widths without resizing
  // anything a ResizeObserver watches.
  useEffect(() => {
    const fonts = typeof document === "undefined" ? undefined : document.fonts;
    if (!fonts?.ready) return;
    let cancelled = false;
    fonts.ready.then(() => {
      if (!cancelled) measure(false);
    }).catch(() => { /* nothing to correct for */ });
    return () => { cancelled = true; };
  }, [measure]);

  // Belt-and-braces: a window resize that neither the track's nor a chip's
  // own box happens to reflect.
  useEffect(() => {
    if (typeof window === "undefined") return;
    const onResize = () => measure(false);
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, [measure]);

  return { pill, trackRef: registerTrack, registerElement };
}
