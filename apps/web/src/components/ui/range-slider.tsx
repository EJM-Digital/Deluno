/**
 * RangeSlider — one track, two thumbs, a filled span between them.
 *
 * Both thumbs share a single scale so a row reads as a band ("this tier accepts
 * 1.5–25 GB") and rows can be compared down the column. Two separate sliders on
 * two different scales, which is what this replaces, cannot do either.
 *
 * Built from two real range inputs so keyboard and screen-reader behaviour comes
 * for free; the visible track and thumbs are styled in index.css (.deluno-range),
 * where the ::-webkit-slider-thumb / ::-moz-range-thumb pseudo-elements live.
 */
import * as React from "react";
import { cn } from "../../lib/utils";

interface RangeSliderProps {
  min: number;
  max: number;
  step: number;
  /** Shared scale ceiling for every row in a table. */
  scaleMax: number;
  /** Treat a max of 0 as "no upper limit": the band runs to the end of the track. */
  zeroMaxIsUnlimited?: boolean;
  minLabel: string;
  maxLabel: string;
  onChange: (next: { min: number; max: number }) => void;
  className?: string;
}

export function RangeSlider({ min, max, step, scaleMax, minLabel, maxLabel, onChange, className, zeroMaxIsUnlimited = false }: RangeSliderProps) {
  const clamp = (value: number) => Math.min(Math.max(value, 0), scaleMax);
  const unlimited = zeroMaxIsUnlimited && max === 0;
  const low = clamp(min);
  const high = unlimited ? scaleMax : clamp(max);
  const toPercent = (value: number) => (scaleMax <= 0 ? 0 : (value / scaleMax) * 100);
  const leftPercent = toPercent(Math.min(low, high));
  // A band of zero width still needs to be visible as a dot on the track.
  const widthPercent = Math.max(toPercent(Math.abs(high - low)), 0.75);

  return (
    <div className={cn("deluno-range", className)}>
      <div aria-hidden className="deluno-range-track">
        <span className="deluno-range-fill" style={{ left: `${leftPercent}%`, width: `${widthPercent}%` }} />
      </div>
      <input
        type="range"
        aria-label={minLabel}
        min={0}
        max={scaleMax}
        step={step}
        value={low}
        // Thumbs may meet — 0/0 is a valid band ("accept anything") — they just never cross.
        onChange={(event) => onChange({ min: Math.min(Number(event.target.value), high), max })}
        className="deluno-range-input"
      />
      <input
        type="range"
        aria-label={maxLabel}
        min={0}
        max={scaleMax}
        step={step}
        value={high}
        onChange={(event) => onChange({ min: Math.min(low, Number(event.target.value)), max: Number(event.target.value) })}
        className="deluno-range-input"
      />
    </div>
  );
}
