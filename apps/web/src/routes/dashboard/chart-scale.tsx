/**
 * What a graph says about its values (#1105), shared by Live's Speed chart and System's traces: a round-number scale
 * in quarters (25 / 50 / 75 / 100 of its top) labelled at the left, the time span at the right, and a reading under the pointer (a thin
 * line and a small box with the time and the values there). Nothing here moves the card: the layer sits inside the
 * chart and the labels never take room of their own.
 */
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import "./chart-scale.css";

/**
 * The top of a scale drawn in quarters (the owner, 2 Oct 2026: "25 / 50 / 75 / 100"): the next of 1, 2, 4, 10 times a power of
 * ten at or above the value, so every quarter is a clean number (2.5 / 5 / 7.5 / 10, 1 / 2 / 3 / 4, 5 / 10 / 15 / 20).
 * A rising reading moves the top up the same steps.
 */
export function quarterMax(value: number): number {
  if (!Number.isFinite(value) || value <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  for (const step of [1, 2, 4, 10]) {
    const candidate = step * power;
    if (candidate >= value * (1 - 1e-9)) return Number(candidate.toPrecision(12));
  }
  return 10 * power;
}

/**
 * The four gridlines of a scale in quarters, from the plot's top (`topPct`) to its bottom (`bottomPct`), as percentages
 * down the chart's box. The 75% and 25% labels are minor: a chart too short to hold four labels shows only their lines.
 */
export function quarterTicks(top: number, unit: "%" | "MB/s", topPct: number, bottomPct: number): ChartTick[] {
  return [1, 0.75, 0.5, 0.25].map((share) => ({
    top: bottomPct - (bottomPct - topPct) * share,
    label: scaleLabel(top * share, unit),
    side: "below" as const,
    minor: share === 0.75 || share === 0.25
  }));
}

/** A scale value as a label: "10 MB/s", "2.5 MB/s", "0.25 MB/s", "50%". */
export function scaleLabel(value: number, unit: "%" | "MB/s"): string {
  if (unit === "%") return `${Math.round(value)}%`;
  const text = value >= 10 ? String(Math.round(value)) : String(Number(value.toFixed(2)));
  return `${text} MB/s`;
}

/** A rate as the readout says it: one decimal under 10, none above ("6.2 MB/s", "42 MB/s", "0 MB/s"). */
export function rateText(value: number): string {
  const v = Number.isFinite(value) ? Math.max(0, value) : 0;
  return `${v >= 10 || v === 0 ? Math.round(v) : v.toFixed(1)} MB/s`;
}

/** The index of the point nearest a fraction (0 to 1) across the chart, given each point's own fraction in order. */
export function nearestIndex(fractions: readonly number[], at: number): number {
  let best = 0;
  let bestGap = Infinity;
  for (let index = 0; index < fractions.length; index += 1) {
    const gap = Math.abs(fractions[index] - at);
    if (gap < bestGap) {
      best = index;
      bestGap = gap;
    }
  }
  return best;
}

/** "3:05 pm", or "3:05:09 pm" with seconds. */
export function clockAt(ms: number, seconds = false): string {
  return new Date(ms)
    .toLocaleTimeString("en-AU", seconds ? { hour: "numeric", minute: "2-digit", second: "2-digit" } : { hour: "numeric", minute: "2-digit" })
    .replace(/\s/g, " ")
    .toLowerCase();
}

/** What is under the pointer: where the line goes (0 to 1) and what the box says. */
export interface ChartHit {
  x: number;
  time: string;
  text: string;
}

/** The reading of an evenly spaced trace (oldest first, the last one `nowMs`) at a fraction across it. */
export function traceHitAt(values: readonly number[], at: number, nowMs: number, text: (value: number) => string, everyMs = 10_000, seconds = true): ChartHit | null {
  const n = values.length;
  if (n === 0) return null;
  const index = n === 1 ? 0 : Math.min(n - 1, Math.max(0, Math.round(at * (n - 1))));
  const value = Number.isFinite(values[index]) ? values[index] : 0;
  return { x: n === 1 ? 1 : index / (n - 1), time: clockAt(nowMs - (n - 1 - index) * everyMs, seconds), text: text(value) };
}

/** Tracks the pointer over a chart: the hit under it, and the handlers to put on the layer. Touch shows on tap and hides on a tap elsewhere. */
export function useChartReadout(resolve: (at: number) => ChartHit | null) {
  const layer = useRef<HTMLDivElement>(null);
  const [hit, setHit] = useState<(ChartHit & { px: number }) | null>(null);

  const move = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const box = layer.current?.getBoundingClientRect();
      if (!box || box.width <= 0) return;
      const at = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
      const found = resolve(at);
      setHit(found ? { ...found, px: at * box.width } : null);
    },
    [resolve]
  );
  const leave = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.pointerType !== "touch") setHit(null);
  }, []);

  useEffect(() => {
    if (!hit) return;
    const away = (event: PointerEvent) => {
      if (event.pointerType === "touch" && layer.current && !layer.current.contains(event.target as Node)) setHit(null);
    };
    document.addEventListener("pointerdown", away);
    return () => document.removeEventListener("pointerdown", away);
  }, [hit]);

  return { layer, hit, handlers: { onPointerMove: move, onPointerDown: move, onPointerLeave: leave } };
}

export interface ChartTick {
  /** Where the gridline sits, as a percentage down the chart's box. */
  top: number;
  label: string;
  /** "above" sits the label on its line, "below" hangs it from it; the top one hangs so it is never cut off. */
  side?: "above" | "below";
  /** A line whose label is left off when the chart is too short for every label to have its own room. */
  minor?: boolean;
}

/** The space one hanging label needs between its line and the next, in px. */
const LABEL_ROOM_PX = 13;

/**
 * The scale, the time span and the readout over a chart. The span is left off where the card already says it (System's
 * cards say "last 10 minutes" in their headings), so it is never said twice and the narrow traces keep their room.
 */
export function ChartLayer({ ticks, startLabel, endLabel, resolve }: { ticks: ChartTick[]; startLabel?: string; endLabel?: string; resolve: (at: number) => ChartHit | null }) {
  const { layer, hit, handlers } = useChartReadout(resolve);
  const box = useRef<HTMLDivElement>(null);
  // The chart's own height decides whether the minor labels have room: each label hangs from its line into the space above the next.
  const [height, setHeight] = useState(0);
  useLayoutEffect(() => {
    const el = layer.current;
    if (!el) return undefined;
    const read = () => setHeight(el.getBoundingClientRect().height);
    read();
    if (typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(read);
    observer.observe(el);
    return () => observer.disconnect();
  }, [layer]);
  const sorted = [...ticks].sort((a, b) => a.top - b.top);
  const tightest = sorted.slice(1).reduce((gap, tick, index) => Math.min(gap, ((tick.top - sorted[index].top) / 100) * height), Infinity);
  const showMinor = height > 0 && tightest >= LABEL_ROOM_PX;
  // Keep the box inside its card: beside the pointer, flipped to the other side near the right edge, then clamped.
  useLayoutEffect(() => {
    const el = box.current;
    const host = layer.current;
    if (!el || !host || !hit) return;
    const card = host.closest("section") ?? host;
    const bounds = card.getBoundingClientRect();
    const own = host.getBoundingClientRect();
    const width = el.offsetWidth;
    const pointer = own.left + hit.px;
    let left = pointer + 10;
    if (left + width > bounds.right - 6) left = pointer - 10 - width;
    left = Math.max(bounds.left + 6, Math.min(left, bounds.right - 6 - width));
    el.style.left = `${left - own.left}px`;
  }, [hit, layer]);
  return (
    <div ref={layer} className={hit ? "cs-layer cs-on" : "cs-layer"} {...handlers}>
      {ticks.map((tick) => (
        <span key={tick.label + tick.top}>
          <i aria-hidden className="cs-grid" style={{ top: `${tick.top}%` }} />
          {tick.minor && !showMinor ? null : <b aria-hidden className={`cs-tick cs-${tick.side ?? "above"}`} style={{ top: `${tick.top}%` }}>{tick.label}</b>}
        </span>
      ))}
      {/* The time span is one label at the right, so the left edge is the scale's alone. */}
      {startLabel && endLabel ? <span aria-hidden className="cs-end cs-now">{startLabel} – {endLabel}</span> : null}
      {hit ? (
        <>
          <i aria-hidden className="cs-cursor" style={{ left: hit.px }} />
          <div ref={box} aria-hidden className="cs-read">
            {hit.time} · {hit.text}
          </div>
        </>
      ) : null}
    </div>
  );
}
