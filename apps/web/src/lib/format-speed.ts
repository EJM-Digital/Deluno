/**
 * The one place a download or upload speed is turned into words (#1489). The owner: under 1 MB/s reads in KB/s, from
 * 1 MB/s in MB/s, and from 1000 MB/s in GB/s, the same in every screen and in Weir.
 *
 * Every speed Deluno carries is in MB/s of 1,000,000 bytes (the backend divides each client's bytes per second by
 * 1_000_000, see `speedMbps`), so 1 MB/s is 1000 KB/s and 1 GB/s is 1000 MB/s.
 */

export type SpeedUnit = "KB/s" | "MB/s" | "GB/s";

const STEP = 1000;

function clean(mbps: number | null | undefined): number {
  return typeof mbps === "number" && Number.isFinite(mbps) ? Math.max(0, mbps) : 0;
}

/** The unit a reading is said in: KB/s under 1 MB/s, MB/s under 1000 MB/s, else GB/s (after rounding, so 999.6 KB/s is 1 MB/s). */
export function speedUnitFor(mbps: number | null | undefined): SpeedUnit {
  const value = clean(mbps);
  if (Math.round(value * STEP) < STEP) return "KB/s";
  if (value < STEP && Math.round(value) < STEP) return "MB/s";
  return "GB/s";
}

/** A reading's number in a chosen unit: whole KB/s, MB/s with one decimal under 10 and whole above, GB/s with one decimal. */
export function speedNumberIn(mbps: number | null | undefined, unit: SpeedUnit): string {
  const value = clean(mbps);
  if (unit === "KB/s") return String(Math.round(value * STEP));
  if (unit === "GB/s") return (Math.round((value / STEP) * 10) / 10).toFixed(1);
  const tenths = Math.round(value * 10) / 10;
  return tenths >= 10 ? String(Math.round(value)) : String(tenths) === String(Math.round(tenths)) ? tenths.toFixed(1) : String(tenths);
}

export interface SpeedOptions {
  /** What a speed of 0 says. Default "0 KB/s"; the queue says "Not moving". */
  zero?: string;
}

/** A reading as the number and its unit apart, so a screen can set the unit small: { value: "120", unit: "KB/s" }. */
export function speedParts(mbps: number | null | undefined, options: SpeedOptions = {}): { value: string; unit: string } {
  if (clean(mbps) === 0 && options.zero !== undefined) return { value: options.zero, unit: "" };
  const unit = speedUnitFor(mbps);
  return { value: speedNumberIn(mbps, unit), unit };
}

/** "120 KB/s", "3.4 MB/s", "42 MB/s", "1.1 GB/s". */
export function formatSpeed(mbps: number | null | undefined, options: SpeedOptions = {}): string {
  const { value, unit } = speedParts(mbps, options);
  return unit ? `${value} ${unit}` : value;
}

/** A reading from a count of bytes per second (a disk or network adapter). */
export function formatBytesPerSecond(bytesPerSecond: number | null | undefined, options: SpeedOptions = {}): string {
  return formatSpeed(clean(bytesPerSecond) / 1e6, options);
}

/** A scale value in a chosen unit for a chart axis: "25 KB/s", "2.5 MB/s", "1.5 GB/s". */
export function speedAxisLabel(mbps: number, unit: SpeedUnit): string {
  const value = clean(mbps);
  if (unit === "KB/s") return `${Number((value * STEP).toPrecision(3))} KB/s`;
  if (unit === "GB/s") return `${Number((value / STEP).toFixed(1))} GB/s`;
  return `${value >= 10 ? Math.round(value) : Number(value.toFixed(2))} MB/s`;
}
