/**
 * A bounded, in-memory sample of a live number.
 *
 * Deluno stores no telemetry history — download speed exists only as "right
 * now". Rather than invent a backing store, this samples the value the page is
 * already polling and keeps the last N readings, so the chart is genuinely live
 * and genuinely short. It resets when you leave the page, and the caller is
 * expected to say so rather than implying it is a stored history.
 */
import { useEffect, useRef, useState } from "react";

export interface LiveSample {
  /** ISO timestamp of the reading. */
  date: string;
  value: number;
}

export function useLiveSeries(value: number, { samples = 60 }: { samples?: number } = {}) {
  const [series, setSeries] = useState<LiveSample[]>([]);
  // The effect must run per new reading, not per render, or a parent re-render
  // would stamp a duplicate sample and stretch the visible window.
  const lastRef = useRef<number | null>(null);

  useEffect(() => {
    if (lastRef.current === value && series.length > 0) return;
    lastRef.current = value;
    setSeries((current) => {
      const next = [...current, { date: new Date().toISOString(), value }];
      return next.length > samples ? next.slice(next.length - samples) : next;
    });
    // `series.length` is read only to seed the first sample; adding it as a
    // dependency would re-run this on every append.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, samples]);

  return series;
}
