/**
 * The data behind the Live tab's two charts (#1096), shaped from stored readings:
 * the speed over the last hours (the throughput samples the server keeps, plus
 * the newest reading the summary carries) and Deluno's own activity per hour
 * today. Nothing here is invented: a window with no readings is a line at 0.
 */
import type { DownloadThroughputSample } from "../../../lib/api";

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
  // Nothing has moved in the whole window: a flat line across all of it, not a stub from the last reading.
  if (points.every((point) => point.down === 0 && point.up === 0)) points.splice(0, points.length, { t: start, down: 0, up: 0 }, { t: now, down: 0, up: 0 });
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
