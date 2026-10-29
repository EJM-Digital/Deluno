/**
 * Small wording helpers for the Dashboard tabs. Everything here returns plain
 * words: no internal codes, no "undefined", no "NaN".
 */
import { formatBytes } from "../../lib/utils";
import { plural } from "../../lib/plural";

/** "3 min ago", "2 h ago", "4 days ago"; a missing or unreadable time says "never". */
export function ago(value: string | null | undefined, now = Date.now()): string {
  if (!value) return "never";
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return "never";
  const seconds = Math.max(0, Math.round((now - then) / 1000));
  if (seconds < 45) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  return `${plural(days, "day")} ago`;
}

/** "in 15 h", "in 2 days" for a time still to come. */
export function until(value: string | null | undefined, now = Date.now()): string {
  if (!value) return "not scheduled";
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return "not scheduled";
  const minutes = Math.max(0, Math.round((then - now) / 60_000));
  if (minutes < 1) return "any moment";
  if (minutes < 60) return `in ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `in ${hours} h`;
  const days = Math.round(hours / 24);
  return `in ${plural(days, "day")}`;
}

export function uptimeText(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 60) return "just started";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h ${minutes % 60} min`;
  return `${Math.floor(hours / 24)} days`;
}

export function bytesText(value: number | null | undefined): string {
  return value === null || value === undefined || !Number.isFinite(value) ? "unknown" : formatBytes(value);
}

export function speedText(mbps: number): string {
  const value = Number.isFinite(mbps) ? Math.max(0, mbps) : 0;
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} MB/s`;
}

/** Sum of a record of counts. */
export function total(record: Record<string, number> | null | undefined): number {
  return Object.values(record ?? {}).reduce((sum, value) => sum + value, 0);
}

/** Whole-number percentage, never NaN. */
export function percent(part: number, whole: number): number {
  return whole > 0 ? Math.round((part / whole) * 100) : 0;
}
