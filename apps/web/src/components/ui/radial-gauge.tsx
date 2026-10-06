/**
 * RadialGauge — a 240° arc for a bounded proportion (#270).
 *
 * Used where a bar would understate the thing: disk space, a hit rate, a health
 * percentage. The sweep animates from wherever it was to wherever it now is, so
 * a change reads as movement rather than a redraw, and the tip carries a glow so
 * the eye lands on the current value.
 *
 * The arc is the presentation of a number that is always also printed inside it,
 * so nothing here is the only way to read the value.
 */
import { useEffect, useRef, useState } from "react";
import { cn } from "../../lib/utils";
import { motionAllowed, onPageHidden } from "../../lib/page-motion";

export type GaugeTone = "primary" | "success" | "warning" | "danger" | "info";

const STROKE: Record<GaugeTone, string> = {
  primary: "hsl(var(--primary))",
  success: "hsl(var(--success))",
  warning: "hsl(var(--warning))",
  danger: "hsl(var(--destructive))",
  info: "hsl(var(--info))"
};

const SIZE = 100;
const RADIUS = 42;
/** A 240° arc, leaving the bottom open so the gap reads as deliberate. */
const SWEEP = 240;
const START = 150;
/** How long a sweep takes; timed from the change so a late frame lands on the value (#873). */
const DURATION_MS = 650;

export function RadialGauge({
  value,
  tone = "primary",
  label,
  caption,
  className
}: {
  /** 0–1. Values outside the range are clamped rather than drawn off the arc. */
  value: number;
  tone?: GaugeTone;
  /** The number, already formatted — printed large in the middle. */
  label: string;
  /** One short line under it. */
  caption?: string;
  className?: string;
}) {
  const target = Math.min(1, Math.max(0, Number.isFinite(value) ? value : 0));
  const [drawn, setDrawn] = useState(target);
  const drawnRef = useRef(target);
  const frameRef = useRef(0);

  useEffect(() => {
    drawnRef.current = drawn;
  }, [drawn]);

  useEffect(() => {
    const from = drawnRef.current;
    if (from === target) return;

    // Hidden or covered, the browser pauses the frames a sweep needs; the
    // sweep would then play from a stale angle when the page is shown (#873).
    if (!motionAllowed()) {
      setDrawn(target);
      return;
    }

    const start = performance.now();
    const step = (now: number) => {
      const progress = Math.min(1, Math.max(0, now - start) / DURATION_MS);
      const eased = 1 - (1 - progress) ** 3;
      setDrawn(progress === 1 ? target : from + (target - from) * eased);
      if (progress < 1) frameRef.current = window.requestAnimationFrame(step);
    };

    frameRef.current = window.requestAnimationFrame(step);
    const stopWatching = onPageHidden(() => {
      window.cancelAnimationFrame(frameRef.current);
      setDrawn(target);
    });
    return () => {
      window.cancelAnimationFrame(frameRef.current);
      stopWatching();
    };
  }, [target]);

  const trackLength = (SWEEP / 360) * 2 * Math.PI * RADIUS;
  const tipAngle = ((START + drawn * SWEEP) * Math.PI) / 180;
  const tipX = SIZE / 2 + RADIUS * Math.cos(tipAngle);
  const tipY = SIZE / 2 + RADIUS * Math.sin(tipAngle);

  return (
    <div className={cn("relative flex items-center justify-center", className)}>
      <svg viewBox={`0 0 ${SIZE} ${SIZE}`} className="h-full w-full -rotate-0" aria-hidden>
        <circle
          cx={SIZE / 2}
          cy={SIZE / 2}
          r={RADIUS}
          fill="none"
          stroke="hsl(var(--surface-3))"
          strokeWidth="7"
          strokeLinecap="round"
          strokeDasharray={`${trackLength} ${2 * Math.PI * RADIUS}`}
          transform={`rotate(${START} ${SIZE / 2} ${SIZE / 2})`}
        />
        <circle
          cx={SIZE / 2}
          cy={SIZE / 2}
          r={RADIUS}
          fill="none"
          stroke={STROKE[tone]}
          strokeWidth="7"
          strokeLinecap="round"
          strokeDasharray={`${trackLength * drawn} ${2 * Math.PI * RADIUS}`}
          transform={`rotate(${START} ${SIZE / 2} ${SIZE / 2})`}
        />
        {drawn > 0.01 ? (
          <circle cx={tipX} cy={tipY} r="4.5" fill={STROKE[tone]} opacity="0.32" />
        ) : null}
      </svg>

      <div className="absolute inset-0 flex flex-col items-center justify-center pt-1">
        <span className="text-(length:--type-body-sm) font-semibold tabular-nums leading-none text-foreground">
          {label}
        </span>
        {caption ? (
          <span className="mt-1 max-w-full truncate px-2 text-(length:--type-micro) text-muted-foreground">
            {caption}
          </span>
        ) : null}
      </div>
    </div>
  );
}
