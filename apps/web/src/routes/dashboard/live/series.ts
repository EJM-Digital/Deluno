/**
 * The data behind the Live tab's two charts (#1096), shaped from stored readings:
 * the speed over the last hours (the throughput samples the server keeps, plus
 * the newest reading the summary carries) and Deluno's own activity per hour
 * today. Nothing here is invented: a window with no readings is a line at 0.
 */
import type { DownloadThroughputSample } from "../../../lib/api";
import { activityKind, type ActivityKind } from "./kinds";

export interface SpeedPoint {
  t: number;
  down: number;
  up: number;
}

export interface SpeedSeries {
  points: SpeedPoint[];
  start: number;
  end: number;
  peakDown: number;
  peakUp: number;
  /** Mean of the stored readings, for the caption. */
  averageDown: number;
}

const finite = (value: number | null | undefined) => (Number.isFinite(value) && value ? Math.max(0, value as number) : 0);

/**
 * Stored samples inside the window, oldest first, plus the newest live reading
 * when it is newer than the last sample, and the last reading held to `now`
 * (a reading holds until the next one). A window with nothing in it still has
 * its reading: a flat 0 MB/s.
 */
export function shapeSpeedSeries(
  samples: DownloadThroughputSample[],
  live: { atMs: number; down: number; up: number } | null,
  now: number,
  hours: number
): SpeedSeries {
  const start = now - hours * 3_600_000;
  const points: SpeedPoint[] = samples
    .map((sample) => ({ t: Date.parse(sample.capturedUtc), down: finite(sample.speedMbps), up: finite(sample.uploadMbps) }))
    .filter((point) => Number.isFinite(point.t) && point.t >= start && point.t <= now)
    .sort((a, b) => a.t - b.t);
  const averageDown = points.length > 0 ? points.reduce((sum, point) => sum + point.down, 0) / points.length : 0;
  if (live && Number.isFinite(live.atMs) && live.atMs <= now && (points.length === 0 || live.atMs > points[points.length - 1].t)) {
    points.push({ t: Math.max(start, live.atMs), down: finite(live.down), up: finite(live.up) });
  }
  if (points.length === 0) points.push({ t: now, down: 0, up: 0 });
  const last = points[points.length - 1];
  if (last.t < now) points.push({ t: now, down: last.down, up: last.up });
  return {
    points,
    start,
    end: now,
    peakDown: Math.max(0, ...points.map((point) => point.down)),
    peakUp: Math.max(0, ...points.map((point) => point.up)),
    averageDown
  };
}

/** An SVG path through the points, scaled into a width by height box with `max` at the top. */
export function linePath(points: SpeedPoint[], pick: "down" | "up", start: number, end: number, max: number, width: number, height: number): string {
  const span = Math.max(1, end - start);
  const top = Math.max(max, 0.1);
  return points
    .map((point, index) => {
      const x = Math.min(width, Math.max(0, ((point.t - start) / span) * width));
      const y = height - Math.min(1, Math.max(0, point[pick] / top)) * height;
      return `${index === 0 ? "M" : "L"}${x.toFixed(1)} ${y.toFixed(1)}`;
    })
    .join(" ");
}

/* ---------------------------------------------------------- rhythm */

export interface HourBucket {
  hour: number;
  total: number;
  counts: Partial<Record<ActivityKind, number>>;
}

export interface RhythmRow {
  id: string;
  category: string;
  severity?: string | null;
  createdUtc: string;
}

function sameLocalDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

export interface HourCountRow {
  hour: number;
  category: string;
  count: number;
}

/** 24 buckets from the server's counts by hour and category. */
export function bucketsFromCounts(counts: HourCountRow[]): HourBucket[] {
  const buckets: HourBucket[] = Array.from({ length: 24 }, (_, hour) => ({ hour, total: 0, counts: {} }));
  for (const row of counts) {
    if (!Number.isInteger(row.hour) || row.hour < 0 || row.hour > 23 || !(row.count > 0)) continue;
    const kind = activityKind(row.category);
    const bucket = buckets[row.hour];
    bucket.counts[kind] = (bucket.counts[kind] ?? 0) + row.count;
    bucket.total += row.count;
  }
  return buckets;
}

/** Adds `extra` buckets (events that arrived since the counts were read) onto `base`. */
export function addBuckets(base: HourBucket[], extra: HourBucket[]): HourBucket[] {
  return base.map((bucket, hour) => {
    const counts = { ...bucket.counts };
    for (const [kind, count] of Object.entries(extra[hour].counts) as [ActivityKind, number][]) counts[kind] = (counts[kind] ?? 0) + count;
    return { hour, total: bucket.total + extra[hour].total, counts };
  });
}

/** 24 buckets for today's hours (local time); a row counts once however many sources carried it. */
export function bucketByHour(rows: RhythmRow[], now: number): HourBucket[] {
  const today = new Date(now);
  const buckets: HourBucket[] = Array.from({ length: 24 }, (_, hour) => ({ hour, total: 0, counts: {} }));
  const seen = new Set<string>();
  for (const row of rows) {
    if (seen.has(row.id)) continue;
    seen.add(row.id);
    const at = new Date(row.createdUtc);
    if (Number.isNaN(at.getTime()) || !sameLocalDay(at, today)) continue;
    const kind = activityKind(row.category, row.severity);
    const bucket = buckets[at.getHours()];
    bucket.counts[kind] = (bucket.counts[kind] ?? 0) + 1;
    bucket.total += 1;
  }
  return buckets;
}
