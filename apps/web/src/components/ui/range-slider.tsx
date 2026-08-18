/**
 * RangeSlider — one track, two thumbs, a filled span between them.
 *
 * Both thumbs share a single scale so a row reads as a band ("this tier accepts
 * 1.5–25 GB") and rows can be compared down the column. Two separate sliders on
 * two different scales, which is what this replaces, cannot do either.
 *
 * The scale may be non-linear. File sizes run from 0.1 GB to 130 GB, so on a
 * linear ruler every low-quality tier collapses into a sliver at the left edge.
 * `scale="sqrt"` gives the bottom of the range far more room while keeping ONE
 * ruler for every row — pair it with `scaleTicks` so the axis is drawn, because
 * an undrawn non-linear ruler is worse than a cramped linear one.
 *
 * Built from two real range inputs so keyboard and screen-reader behaviour comes
 * for free; the visible track and thumbs are styled in index.css (.deluno-range),
 * where the ::-webkit-slider-thumb / ::-moz-range-thumb pseudo-elements live.
 * The inputs run in position space (0…POSITION_STEPS) rather than value space so
 * the native thumb lands where the fill does; arrow keys are handled in value
 * space so a keypress is always worth exactly one `step`.
 */
import * as React from "react";
import { cn } from "../../lib/utils";

export type RangeScale = "linear" | "sqrt";

/** Track resolution. Fine enough to drag smoothly, coarse enough to stay integral. */
const POSITION_STEPS = 1000;

/** Where `value` sits on the track, 0–100. Shared with axis rendering. */
export function scalePercent(value: number, scaleMax: number, scale: RangeScale = "linear") {
  if (scaleMax <= 0) return 0;
  const ratio = Math.min(Math.max(value, 0), scaleMax) / scaleMax;
  return (scale === "sqrt" ? Math.sqrt(ratio) : ratio) * 100;
}

/**
 * Evenly spaced tick values for a scale — equal distances on screen, rounded to
 * numbers worth printing. Returns ascending values starting at 0.
 */
export function scaleTicks(scaleMax: number, scale: RangeScale = "linear", count = 5) {
  const ticks = [0];
  for (let index = 1; index <= count; index += 1) {
    const fraction = index / count;
    const raw = scaleMax * (scale === "sqrt" ? fraction ** 2 : fraction);
    const value = index === count ? scaleMax : niceRound(raw);
    if (value > ticks[ticks.length - 1]!) ticks.push(value);
  }
  return ticks;
}

/** Round to the nearest 1, 2 or 5 × 10ⁿ so an axis never prints 23.7. */
function niceRound(value: number) {
  if (value <= 0) return 0;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  const normalized = value / magnitude;
  const snapped = normalized < 1.5 ? 1 : normalized < 3.5 ? 2 : normalized < 7.5 ? 5 : 10;
  return Number((snapped * magnitude).toPrecision(3));
}

interface RangeSliderProps {
  min: number;
  max: number;
  step: number;
  /** Shared scale ceiling for every row in a table. */
  scaleMax: number;
  /** Non-linear scales need `scaleTicks` drawn above the column, or they mislead. */
  scale?: RangeScale;
  /** Treat a max of 0 as "no upper limit": the band runs to the end of the track. */
  zeroMaxIsUnlimited?: boolean;
  minLabel: string;
  maxLabel: string;
  /** Spoken value, so a screen reader reads "2.5 GB" and not a track position. */
  formatValue?: (value: number) => string;
  onChange: (next: { min: number; max: number }) => void;
  className?: string;
}

export function RangeSlider({
  min,
  max,
  step,
  scaleMax,
  scale = "linear",
  minLabel,
  maxLabel,
  formatValue,
  onChange,
  className,
  zeroMaxIsUnlimited = false
}: RangeSliderProps) {
  const clamp = (value: number) => Math.min(Math.max(value, 0), scaleMax);
  const snap = (value: number) => Number(clamp(value).toFixed(step < 1 ? 1 : 0));

  // 0/0 is "no rule at all", not "accept everything": the control reads as off —
  // empty track, both thumbs parked at the left — rather than filled end to end.
  const off = zeroMaxIsUnlimited && min === 0 && max === 0;
  const unlimited = zeroMaxIsUnlimited && max === 0 && !off;
  const low = clamp(min);
  const high = off ? 0 : unlimited ? scaleMax : clamp(max);

  const toPosition = (value: number) => Math.round((scalePercent(value, scaleMax, scale) / 100) * POSITION_STEPS);
  const fromPosition = (position: number) => {
    const fraction = position / POSITION_STEPS;
    return snap(scaleMax * (scale === "sqrt" ? fraction ** 2 : fraction));
  };

  const leftPercent = off ? 0 : scalePercent(Math.min(low, high), scaleMax, scale);
  // A band of zero width still needs to be visible as a dot on the track.
  const widthPercent = off ? 0 : Math.max(scalePercent(Math.max(low, high), scaleMax, scale) - leftPercent, 0.75);

  /** Arrow keys move by one `step` of real size, not one track position. */
  function nudge(event: React.KeyboardEvent<HTMLInputElement>, thumb: "min" | "max") {
    const delta =
      event.key === "ArrowRight" || event.key === "ArrowUp"
        ? step
        : event.key === "ArrowLeft" || event.key === "ArrowDown"
          ? -step
          : event.key === "PageUp"
            ? step * 10
            : event.key === "PageDown"
              ? step * -10
              : event.key === "Home"
                ? -Infinity
                : event.key === "End"
                  ? Infinity
                  : null;
    if (delta === null) return;
    event.preventDefault();
    const current = thumb === "min" ? low : high;
    const next = delta === -Infinity ? 0 : delta === Infinity ? scaleMax : snap(current + delta);
    if (thumb === "min") onChange({ min: Math.min(next, high), max });
    else onChange({ min: Math.min(low, next), max: next });
  }

  return (
    <div className={cn("deluno-range", className)}>
      <div aria-hidden className="deluno-range-track">
        <span className="deluno-range-fill" style={{ left: `${leftPercent}%`, width: `${widthPercent}%` }} />
      </div>
      <input
        type="range"
        aria-label={minLabel}
        aria-valuetext={formatValue?.(low)}
        min={0}
        max={POSITION_STEPS}
        step={1}
        value={toPosition(low)}
        // Thumbs may meet — 0/0 is a valid band ("accept anything") — they just never cross.
        onChange={(event) => {
          const next = fromPosition(Number(event.target.value));
          onChange({ min: Math.min(next, high), max });
        }}
        onKeyDown={(event) => nudge(event, "min")}
        className="deluno-range-input"
      />
      <input
        type="range"
        aria-label={maxLabel}
        aria-valuetext={formatValue?.(high)}
        min={0}
        max={POSITION_STEPS}
        step={1}
        value={toPosition(high)}
        onChange={(event) => {
          const next = fromPosition(Number(event.target.value));
          onChange({ min: Math.min(low, next), max: next });
        }}
        onKeyDown={(event) => nudge(event, "max")}
        className="deluno-range-input"
      />
    </div>
  );
}

/**
 * The ruler a non-linear RangeSlider column is read against. Render it once,
 * directly under the column header, aligned to the slider track.
 */
export function RangeAxis({
  scaleMax,
  scale = "linear",
  unit,
  format,
  className
}: {
  scaleMax: number;
  scale?: RangeScale;
  unit?: string;
  format?: (value: number) => string;
  className?: string;
}) {
  const ticks = scaleTicks(scaleMax, scale);
  return (
    <div aria-hidden className={cn("relative h-4 select-none", className)}>
      {ticks.map((tick, index) => {
        const percent = scalePercent(tick, scaleMax, scale);
        const first = index === 0;
        const last = index === ticks.length - 1;
        return (
          <span
            key={tick}
            className={cn(
              "absolute top-0 flex flex-col gap-0.5 whitespace-nowrap text-[length:var(--type-micro)] tabular-nums leading-none text-muted-foreground/70",
              // The end labels tuck inside the track rather than overhang the column.
              first ? "items-start" : last ? "items-end" : "items-center -translate-x-1/2"
            )}
            style={{ left: `${percent}%`, transform: last ? "translateX(-100%)" : undefined }}
          >
            <span>
              {format ? format(tick) : tick.toLocaleString()}
              {last && unit ? ` ${unit}` : ""}
            </span>
            {/* Mark sits below the label, pointing at the tracks it measures. */}
            <span aria-hidden className="block h-1 w-px bg-hairline" />
          </span>
        );
      })}
    </div>
  );
}
